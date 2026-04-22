using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using InvoiceApi.Models.Dtos;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Services;

public interface IAuthService
{
    Task<AuthResponseDto> RegisterAsync(RegisterDto dto, CancellationToken ct = default);
    Task<AuthResponseDto> LoginAsync(LoginDto dto, CancellationToken ct = default);
    Task<AuthResponseDto> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default);
}

public class AuthService(
    AppDbContext db,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IConfiguration config) : IAuthService
{
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

        var refreshToken = CreateRefreshToken(user.Id);
        db.RefreshTokens.Add(refreshToken);

        await db.SaveChangesAsync(ct);

        return BuildAuthResponse(user, refreshToken.Token);
    }

    public async Task<AuthResponseDto> LoginAsync(LoginDto dto, CancellationToken ct = default)
    {
        var normalizedEmail = dto.Email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        if (user is null || !passwordHasher.Verify(dto.Password, user.PasswordHash))
            throw new UnauthorizedException("Invalid credentials.");

        var refreshToken = CreateRefreshToken(user.Id);
        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync(ct);

        return BuildAuthResponse(user, refreshToken.Token);
    }

    public async Task<AuthResponseDto> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var token = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Token == refreshToken, ct);

        if (token is null || token.IsRevoked || token.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedException("Invalid or expired refresh token.");

        token.RevokedAt = DateTime.UtcNow;

        var newRefreshToken = CreateRefreshToken(token.UserId);
        db.RefreshTokens.Add(newRefreshToken);

        await db.SaveChangesAsync(ct);

        return BuildAuthResponse(token.User, newRefreshToken.Token);
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == refreshToken, ct);
        if (token is not null && !token.IsRevoked)
        {
            token.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private RefreshToken CreateRefreshToken(Guid userId)
    {
        var days = config.GetValue("Jwt:RefreshTokenDays", 30);
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = jwtTokenService.GenerateRefreshToken(),
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(days)
        };
    }

    private AuthResponseDto BuildAuthResponse(User user, string refreshToken)
    {
        var minutes = config.GetValue("Jwt:AccessTokenMinutes", 15);
        var accessToken = jwtTokenService.GenerateAccessToken(user);
        return new AuthResponseDto(
            accessToken,
            refreshToken,
            DateTime.UtcNow.AddMinutes(minutes),
            new UserDto(user.Id, user.Email, user.Name, user.CreatedAt)
        );
    }
}
