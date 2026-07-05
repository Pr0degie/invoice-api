using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using InvoiceApi.Models.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace InvoiceApi.Services;

public interface IAuthService
{
    Task<MessageResponseDto> RegisterAsync(RegisterDto dto, CancellationToken ct = default);
    Task<AuthResponseDto> LoginAsync(LoginDto dto, CancellationToken ct = default);
    Task<AuthResponseDto> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default);
    Task<UserDto> UpdateProfileAsync(Guid userId, UpdateProfileDto dto, CancellationToken ct = default);
    Task ChangePasswordAsync(Guid userId, ChangePasswordDto dto, CancellationToken ct = default);
    Task DeleteAccountAsync(Guid userId, CancellationToken ct = default);
    Task<MessageResponseDto> ForgotPasswordAsync(ForgotPasswordDto dto, CancellationToken ct = default);
    Task ResetPasswordAsync(ResetPasswordDto dto, CancellationToken ct = default);
    Task VerifyEmailAsync(VerifyEmailDto dto, CancellationToken ct = default);
    Task<MessageResponseDto> ResendVerificationAsync(ResendVerificationDto dto, CancellationToken ct = default);
}

public class AuthService(
    AppDbContext db,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IEmailQueue emailQueue,
    IConfiguration config,
    IMemoryCache cache) : IAuthService
{
    // Concurrent refreshes (two tabs, request retry) race on single-use rotation.
    // A token rotated less than this many seconds ago replays its successor
    // instead of 401ing. See docs/adr/0001-refresh-token-rotation-grace.md.
    private const int RotationGraceSeconds = 60;

    // Static cost-12 BCrypt hash (of a throwaway string, matches BCryptPasswordHasher.WorkFactor).
    // Verified against when the email is unknown so both login paths do the same BCrypt work —
    // otherwise the response time reveals whether an email is registered.
    private const string DummyPasswordHash = "$2a$12$nK4B7XO330gbKamdYlUP2uZ4WTpLWLvzxjNPyX5Aov9tAGQ67gOIq";

    // Out-of-band token lifetimes (ADR 0006).
    private static readonly TimeSpan PasswordResetTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan EmailVerificationTtl = TimeSpan.FromHours(24);

    // Identical reply for hit and miss so forgot-password / resend-verification
    // never disclose whether an address is registered.
    private const string GenericEmailFlowMessage =
        "Falls ein Konto mit dieser E-Mail-Adresse existiert, wurde eine E-Mail mit weiteren Anweisungen versendet.";

    // UI languages we can localize a mail into and embed as a URL path segment.
    // The request's `locale` is a hint only — anything off this list (or absent)
    // falls back to German. Normalizing here means the value that reaches the URL
    // is always one of these literals, so the field can't be a URL-injection vector.
    private const string DefaultLocale = "de";

    private static string NormalizeLocale(string? locale)
    {
        var candidate = locale?.Trim().ToLowerInvariant();
        return candidate is "de" or "en" ? candidate : DefaultLocale;
    }

    private static string GraceCacheKey(string tokenHash) => $"refresh-grace:{tokenHash}";

    public async Task<MessageResponseDto> RegisterAsync(RegisterDto dto, CancellationToken ct = default)
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
            // Unverified until the e-mailed link is redeemed — login stays blocked
            // (403 email_not_verified) meanwhile. No session issued at registration.
            EmailVerifiedAt = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        var rawToken = await CreateUserTokenAsync(user.Id, UserTokenType.EmailVerification, EmailVerificationTtl, ct);
        await db.SaveChangesAsync(ct);

        QueueVerificationEmail(user, rawToken, NormalizeLocale(dto.Locale));

        return new MessageResponseDto(
            "Registrierung erfolgreich. Bitte bestätige deine E-Mail-Adresse über den zugesandten Link.");
    }

    public async Task<AuthResponseDto> LoginAsync(LoginDto dto, CancellationToken ct = default)
    {
        var normalizedEmail = dto.Email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        if (user is null || user.DeletedAt is not null)
        {
            // Timing equalization: do the same BCrypt work as the known-email path.
            // Anonymized accounts (ADR 0005) take this path too — they are dead.
            passwordHasher.Verify(dto.Password, DummyPasswordHash);
            throw new UnauthorizedException("Invalid credentials.");
        }

        if (!passwordHasher.Verify(dto.Password, user.PasswordHash))
            throw new UnauthorizedException("Invalid credentials.");

        // Unverified accounts get a distinguishable 403 (not the generic 401): the
        // user must understand the problem, and enumeration is moot here — whoever
        // logs in just proved they hold the credentials. See ADR 0006.
        if (user.EmailVerifiedAt is null)
            throw new ForbiddenException("email_not_verified");

        // Housekeeping: drop this user's expired refresh tokens
        var now = DateTime.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);

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

        // Anonymized accounts (ADR 0005) have all their refresh tokens deleted;
        // the DeletedAt check is belt-and-braces against any straggler.
        if (token is null || token.ExpiresAt < DateTime.UtcNow || token.User.DeletedAt is not null)
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
                var now = DateTime.UtcNow;
                await db.RefreshTokens
                    .Where(t => t.UserId == token.UserId && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
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

    public async Task<MessageResponseDto> ForgotPasswordAsync(ForgotPasswordDto dto, CancellationToken ct = default)
    {
        var normalizedEmail = dto.Email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        if (user is not null)
        {
            var rawToken = await CreateUserTokenAsync(user.Id, UserTokenType.PasswordReset, PasswordResetTtl, ct);
            await db.SaveChangesAsync(ct);
            QueuePasswordResetEmail(user, rawToken, NormalizeLocale(dto.Locale));
        }
        else
        {
            // The dominant timing gap — an inline SMTP round-trip on the hit path —
            // is gone now that delivery is queued (both paths just enqueue/skip and
            // return). This keeps the cheap token-generation work symmetric too, so
            // neither the body nor the response time discloses whether the address
            // is registered.
            _ = SecureToken.Generate();
        }

        return new MessageResponseDto(GenericEmailFlowMessage);
    }

    public async Task ResetPasswordAsync(ResetPasswordDto dto, CancellationToken ct = default)
    {
        var token = await ConsumeTokenAsync(dto.Token, UserTokenType.PasswordReset, ct);

        token.User.PasswordHash = passwordHasher.Hash(dto.NewPassword);
        token.User.UpdatedAt = DateTime.UtcNow;

        // A reset means the old password is compromised or forgotten — kill every
        // active session so a lurking refresh token can't outlive the reset.
        var now = DateTime.UtcNow;
        var activeTokens = await db.RefreshTokens
            .Where(t => t.UserId == token.UserId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var t in activeTokens)
            t.RevokedAt = now;

        await db.SaveChangesAsync(ct);
    }

    public async Task VerifyEmailAsync(VerifyEmailDto dto, CancellationToken ct = default)
    {
        var token = await ConsumeTokenAsync(dto.Token, UserTokenType.EmailVerification, ct);

        // Idempotent enough: a second valid token just re-stamps the same instant.
        token.User.EmailVerifiedAt ??= DateTime.UtcNow;
        token.User.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<MessageResponseDto> ResendVerificationAsync(ResendVerificationDto dto, CancellationToken ct = default)
    {
        var normalizedEmail = dto.Email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        // Only unverified accounts get a fresh link; verified (or unknown) do the
        // dummy path — same generic reply, no enumeration.
        if (user is not null && user.EmailVerifiedAt is null)
        {
            var rawToken = await CreateUserTokenAsync(user.Id, UserTokenType.EmailVerification, EmailVerificationTtl, ct);
            await db.SaveChangesAsync(ct);
            QueueVerificationEmail(user, rawToken, NormalizeLocale(dto.Locale));
        }
        else
        {
            _ = SecureToken.Generate();
        }

        return new MessageResponseDto(GenericEmailFlowMessage);
    }

    // Creates a fresh single-use token, clearing this user's prior tokens of the
    // same type plus any globally-expired ones (RemoveRange, not ExecuteDelete, so
    // the InMemory test provider stays supported — same pattern as LoginAsync).
    // Adds to the change tracker; the caller owns the SaveChanges.
    private async Task<string> CreateUserTokenAsync(Guid userId, UserTokenType type, TimeSpan ttl, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var stale = await db.UserTokens
            .Where(t => t.ExpiresAt < now || (t.UserId == userId && t.Type == type))
            .ToListAsync(ct);
        db.UserTokens.RemoveRange(stale);

        var rawToken = SecureToken.Generate();
        db.UserTokens.Add(new UserToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            TokenHash = RefreshTokenHasher.Hash(rawToken),
            CreatedAt = now,
            ExpiresAt = now.Add(ttl)
        });
        return rawToken;
    }

    // Resolves a raw token to its (unconsumed, unexpired, right-type) row and
    // stamps it consumed. Does NOT SaveChanges — the caller commits alongside its
    // own mutation so consumption and effect are atomic.
    private async Task<UserToken> ConsumeTokenAsync(string rawToken, UserTokenType type, CancellationToken ct)
    {
        var hash = RefreshTokenHasher.Hash(rawToken);
        var token = await db.UserTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Type == type, ct);

        if (token is null || token.ConsumedAt is not null || token.ExpiresAt < DateTime.UtcNow)
            throw new ValidationException("invalid_or_expired_token");

        token.ConsumedAt = DateTime.UtcNow;
        return token;
    }

    // The link always carries an explicit locale segment (e.g. /de/verify-email),
    // even for the default locale: next-intl with localePrefix "as-needed" serves
    // prefixed URLs for every locale, and being explicit is more robust than
    // re-deriving the frontend's prefix rules here. `locale` is pre-normalized.
    private void QueueVerificationEmail(User user, string rawToken, string locale)
    {
        var link = $"{FrontendBaseUrl()}/{locale}/verify-email?token={rawToken}";
        var (subject, body) = locale == "en"
            ? ("Confirm your email address",
                $"Hi {user.Name},\n\n" +
                "please confirm your email address for InvoiceFlow by opening the following link:\n\n" +
                $"{link}\n\n" +
                "The link is valid for 24 hours.\n\n" +
                "If you didn't sign up for InvoiceFlow, you can ignore this email.\n\n" +
                "Best regards\nYour InvoiceFlow team")
            : ("Bestätige deine E-Mail-Adresse",
                $"Hallo {user.Name},\n\n" +
                "bitte bestätige deine E-Mail-Adresse für InvoiceFlow, indem du den folgenden Link öffnest:\n\n" +
                $"{link}\n\n" +
                "Der Link ist 24 Stunden gültig.\n\n" +
                "Wenn du dich nicht bei InvoiceFlow registriert hast, kannst du diese E-Mail ignorieren.\n\n" +
                "Viele Grüße\nDein InvoiceFlow-Team");
        emailQueue.Enqueue(new EmailMessage(user.Email, subject, body));
    }

    private void QueuePasswordResetEmail(User user, string rawToken, string locale)
    {
        var link = $"{FrontendBaseUrl()}/{locale}/reset-password?token={rawToken}";
        var (subject, body) = locale == "en"
            ? ("Reset your password",
                $"Hi {user.Name},\n\n" +
                "a reset of your InvoiceFlow password was requested. " +
                "Open the following link to set a new password:\n\n" +
                $"{link}\n\n" +
                "The link is valid for 1 hour.\n\n" +
                "If this wasn't you, ignore this email — your password stays unchanged.\n\n" +
                "Best regards\nYour InvoiceFlow team")
            : ("Passwort zurücksetzen",
                $"Hallo {user.Name},\n\n" +
                "es wurde ein Zurücksetzen deines InvoiceFlow-Passworts angefordert. " +
                "Öffne den folgenden Link, um ein neues Passwort zu vergeben:\n\n" +
                $"{link}\n\n" +
                "Der Link ist 1 Stunde gültig.\n\n" +
                "Wenn du das nicht warst, ignoriere diese E-Mail — dein Passwort bleibt unverändert.\n\n" +
                "Viele Grüße\nDein InvoiceFlow-Team");
        emailQueue.Enqueue(new EmailMessage(user.Email, subject, body));
    }

    private string FrontendBaseUrl() =>
        (config["App:FrontendBaseUrl"] ?? config["FRONTEND_BASE_URL"] ?? "http://localhost:3000")
            .TrimEnd('/');

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
        var user = await GetActiveUserAsync(userId, ct);

        if (dto.Name is not null)
            user.Name = dto.Name == "" ? throw new ValidationException("Name cannot be empty.") : dto.Name.Trim();

        if (dto.DefaultSenderName is not null)
            user.DefaultSenderName = dto.DefaultSenderName == "" ? null : dto.DefaultSenderName.Trim();

        if (dto.DefaultSenderAddress is not null)
            user.DefaultSenderAddress = dto.DefaultSenderAddress == "" ? null : dto.DefaultSenderAddress.Trim();

        if (dto.TaxNumber is not null)
            user.TaxNumber = NullIfEmpty(dto.TaxNumber);

        if (dto.VatId is not null)
            user.VatId = NullIfEmpty(dto.VatId)?.Replace(" ", "").ToUpperInvariant();

        if (dto.IsSmallBusiness is not null)
            user.IsSmallBusiness = dto.IsSmallBusiness.Value;

        if (dto.Street is not null) user.Street = NullIfEmpty(dto.Street);
        if (dto.PostalCode is not null) user.PostalCode = NullIfEmpty(dto.PostalCode);
        if (dto.City is not null) user.City = NullIfEmpty(dto.City);
        if (dto.Country is not null) user.Country = NullIfEmpty(dto.Country);
        if (dto.Phone is not null) user.Phone = NullIfEmpty(dto.Phone);

        if (dto.Iban is not null)
        {
            var iban = NullIfEmpty(dto.Iban)?.Replace(" ", "").ToUpperInvariant();
            if (iban is not null && !System.Text.RegularExpressions.Regex.IsMatch(iban, "^[A-Z]{2}[0-9]{2}[A-Z0-9]{10,30}$"))
                throw new ValidationException("IBAN format is invalid.");
            user.Iban = iban;
        }

        if (dto.Bic is not null)
            user.Bic = NullIfEmpty(dto.Bic)?.Replace(" ", "").ToUpperInvariant();

        if (dto.BankName is not null)
            user.BankName = NullIfEmpty(dto.BankName);

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ToUserDto(user);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordDto dto, CancellationToken ct = default)
    {
        var user = await GetActiveUserAsync(userId, ct);

        if (!passwordHasher.Verify(dto.CurrentPassword, user.PasswordHash))
            throw new ValidationException("invalid_current_password");

        user.PasswordHash = passwordHasher.Hash(dto.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task DeleteAccountAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await GetActiveUserAsync(userId, ct);

        // GoBD/§ 147 AO (ADR 0005): numbered invoices are Buchungsbelege under an
        // 8-year retention duty — they must survive account deletion. "Numbered"
        // covers Finalized/Paid/Cancelled AND reopened drafts, which keep their
        // number (ADR 0003) and therefore their slot in the invoice sequence.
        var hasRetainedInvoices = await db.Invoices.AnyAsync(
            i => i.UserId == userId && (i.Status != InvoiceStatus.Draft || i.Number != null), ct);

        if (!hasRetainedInvoices)
        {
            // Common case (test accounts, never finalized anything): hard delete.
            // FK cascades take the drafts, line items and refresh tokens with it.
            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            return;
        }

        // Anonymization path: erase the person, keep the Belege (ADR 0005).
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Unnumbered drafts have no Beleg character — hard delete (cascades to line items).
        await db.Invoices
            .Where(i => i.UserId == userId && i.Status == InvoiceStatus.Draft && i.Number == null)
            .ExecuteDeleteAsync(ct);

        // Kill every session; combined with the invalidated password hash and the
        // DeletedAt guard in LoginAsync, the account is unreachable from now on.
        await db.RefreshTokens
            .Where(t => t.UserId == userId)
            .ExecuteDeleteAsync(ct);

        // Non-reversible placeholder; GUID keeps the unique email index happy and
        // .invalid (RFC 2606) guarantees the address can never be routed or re-registered.
        user.Email = $"deleted-{Guid.NewGuid():N}@anonym.invalid";
        // Hash of a random 256-bit throwaway value nobody knows — stays a valid
        // BCrypt hash (no Verify() foot-gun) but can never be matched.
        user.PasswordHash = passwordHasher.Hash(
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        user.Name = "Gelöschtes Konto";
        user.DefaultSenderName = null;
        user.DefaultSenderAddress = null;
        user.TaxNumber = null;
        user.VatId = null;
        user.Street = null;
        user.PostalCode = null;
        user.City = null;
        user.Country = null;
        user.Phone = null;
        user.Iban = null;
        user.Bic = null;
        user.BankName = null;
        user.DeletedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // Valid JWT but no live user row = account deleted/anonymized → 401 so the client signs out.
    private async Task<User> GetActiveUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct);
        if (user is null || user.DeletedAt is not null)
            throw new UnauthorizedException("User not found.");
        return user;
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

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static UserDto ToUserDto(User user) =>
        new(user.Id, user.Email, user.Name, user.CreatedAt,
            user.DefaultSenderName, user.DefaultSenderAddress,
            user.TaxNumber, user.VatId, user.IsSmallBusiness,
            user.Street, user.PostalCode, user.City, user.Country, user.Phone,
            user.Iban, user.Bic, user.BankName);
}
