using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using InvoiceApi.Models.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace InvoiceApi.Services;

public interface IAuthService
{
    Task<AuthResponseDto> RegisterAsync(RegisterDto dto, CancellationToken ct = default);
    Task<AuthResponseDto> LoginAsync(LoginDto dto, CancellationToken ct = default);
    Task<AuthResponseDto> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default);
    Task<UserDto> UpdateProfileAsync(Guid userId, UpdateProfileDto dto, CancellationToken ct = default);
    Task ChangePasswordAsync(Guid userId, ChangePasswordDto dto, CancellationToken ct = default);
    Task DeleteAccountAsync(Guid userId, CancellationToken ct = default);
}

public class AuthService(
    AppDbContext db,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IConfiguration config,
    IMemoryCache cache) : IAuthService
{
    // Concurrent refreshes (two tabs, request retry) race on single-use rotation.
    // A token rotated less than this many seconds ago replays its successor
    // instead of 401ing. See docs/adr/0001-refresh-token-rotation-grace.md.
    private const int RotationGraceSeconds = 60;

    private static string GraceCacheKey(string tokenHash) => $"refresh-grace:{tokenHash}";

    public async Task<AuthResponseDto> RegisterAsync(RegisterDto dto, CancellationToken ct = default)
    {
        var normalizedEmail = dto.Email.ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct))
            throw new ValidationException("Email already in use.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            PasswordHash = passwordHasher.Hash(dto.Password),
            Name = dto.Name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);

        var (refreshToken, rawToken) = CreateRefreshToken(user.Id);
        db.RefreshTokens.Add(refreshToken);

        await db.SaveChangesAsync(ct);

        return BuildAuthResponse(user, rawToken);
    }

    public async Task<AuthResponseDto> LoginAsync(LoginDto dto, CancellationToken ct = default)
    {
        var normalizedEmail = dto.Email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        if (user is null || !passwordHasher.Verify(dto.Password, user.PasswordHash))
            throw new UnauthorizedException("Invalid credentials.");

        // Housekeeping: drop this user's expired refresh tokens
        var now = DateTime.UtcNow;
        var expiredTokens = await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.ExpiresAt < now)
            .ToListAsync(ct);
        db.RefreshTokens.RemoveRange(expiredTokens);

        var (refreshToken, rawToken) = CreateRefreshToken(user.Id);
        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync(ct);

        return BuildAuthResponse(user, rawToken);
    }

    public async Task<AuthResponseDto> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = RefreshTokenHasher.Hash(refreshToken);
        var token = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Token == tokenHash, ct);

        if (token is null || token.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedException("Invalid or expired refresh token.");

        if (token.IsRevoked)
        {
            var withinGrace = token.RevokedAt >= DateTime.UtcNow.AddSeconds(-RotationGraceSeconds);

            // Rotated moments ago (concurrent refresh) → replay the same successor tokens.
            if (token.ReplacedByTokenHash is not null && withinGrace
                && cache.TryGetValue<AuthResponseDto>(GraceCacheKey(tokenHash), out var cachedSuccessor))
            {
                return cachedSuccessor!;
            }

            // Reuse of a rotated token after the grace window is a theft signal:
            // someone replayed a token whose successor is already in use. Kill all sessions.
            if (token.ReplacedByTokenHash is not null && !withinGrace)
            {
                var activeTokens = await db.RefreshTokens
                    .Where(t => t.UserId == token.UserId && t.RevokedAt == null)
                    .ToListAsync(ct);
                var now = DateTime.UtcNow;
                foreach (var t in activeTokens)
                    t.RevokedAt = now;
                await db.SaveChangesAsync(ct);
            }

            throw new UnauthorizedException("Invalid or expired refresh token.");
        }

        token.RevokedAt = DateTime.UtcNow;

        var (newRefreshToken, rawToken) = CreateRefreshToken(token.UserId);
        token.ReplacedByTokenHash = newRefreshToken.Token;
        db.RefreshTokens.Add(newRefreshToken);

        await db.SaveChangesAsync(ct);

        var response = BuildAuthResponse(token.User, rawToken);
        cache.Set(GraceCacheKey(tokenHash), response, TimeSpan.FromSeconds(RotationGraceSeconds));
        return response;
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = RefreshTokenHasher.Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == tokenHash, ct);
        if (token is not null && !token.IsRevoked)
        {
            token.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private (RefreshToken entity, string rawToken) CreateRefreshToken(Guid userId)
    {
        var days = config.GetValue("Jwt:RefreshTokenDays", 30);
        var rawToken = jwtTokenService.GenerateRefreshToken();
        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = RefreshTokenHasher.Hash(rawToken),
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(days)
        };
        return (entity, rawToken);
    }

    public async Task<UserDto> UpdateProfileAsync(Guid userId, UpdateProfileDto dto, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([userId], ct)
            ?? throw new NotFoundException("User not found.");

        if (dto.Name is not null)
            user.Name = dto.Name == "" ? throw new ValidationException("Name cannot be empty.") : dto.Name.Trim();

        if (dto.DefaultSenderName is not null)
            user.DefaultSenderName = dto.DefaultSenderName == "" ? null : dto.DefaultSenderName.Trim();

        if (dto.DefaultSenderAddress is not null)
            user.DefaultSenderAddress = dto.DefaultSenderAddress == "" ? null : dto.DefaultSenderAddress.Trim();

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ToUserDto(user);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordDto dto, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([userId], ct)
            ?? throw new NotFoundException("User not found.");

        if (!passwordHasher.Verify(dto.CurrentPassword, user.PasswordHash))
            throw new ValidationException("invalid_current_password");

        user.PasswordHash = passwordHasher.Hash(dto.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        var activeTokens = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var t in activeTokens)
            t.RevokedAt = now;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAccountAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([userId], ct)
            ?? throw new NotFoundException("User not found.");

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
    }

    private AuthResponseDto BuildAuthResponse(User user, string refreshToken)
    {
        var minutes = config.GetValue("Jwt:AccessTokenMinutes", 15);
        var accessToken = jwtTokenService.GenerateAccessToken(user);
        return new AuthResponseDto(
            accessToken,
            refreshToken,
            DateTime.UtcNow.AddMinutes(minutes),
            ToUserDto(user)
        );
    }

    private static UserDto ToUserDto(User user) =>
        new(user.Id, user.Email, user.Name, user.CreatedAt, user.DefaultSenderName, user.DefaultSenderAddress);
}
