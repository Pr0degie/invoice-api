using FluentAssertions;
using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using InvoiceApi.Models.Dtos;
using InvoiceApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace InvoiceApi.Tests.Auth;

public class PasswordResetAndVerificationTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly CapturingEmailSender _email = new();
    private readonly AuthService _sut;

    public PasswordResetAndVerificationTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(opts);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "invoice-api",
                ["Jwt:Audience"] = "invoiceflow",
                ["Jwt:SigningKey"] = "test-signing-key-for-unit-tests-only-32chars",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30",
                ["App:FrontendBaseUrl"] = "https://app.invoiceflow.test"
            })
            .Build();

        _sut = new AuthService(_db, new BCryptPasswordHasher(), new JwtTokenService(config), _email, config,
            new MemoryCache(new MemoryCacheOptions()));
    }

    private async Task<User> RegisterVerifiedAsync(string email, string password = "password123")
    {
        await _sut.RegisterAsync(new RegisterDto(email, password, "Test User"));
        var user = await _db.Users.FirstAsync(u => u.Email == email.ToLowerInvariant());
        user.EmailVerifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _email.Messages.Clear(); // drop the registration mail so LastToken() targets the flow under test
        return user;
    }

    // ── E-mail verification ───────────────────────────────────────────────────

    [Fact]
    public async Task VerifyEmail_HappyPath_SetsVerifiedAndConsumesToken()
    {
        await _sut.RegisterAsync(new RegisterDto("v1@example.com", "password123", "User"));
        var rawToken = _email.LastToken();

        await _sut.VerifyEmailAsync(new VerifyEmailDto(rawToken));

        var user = await _db.Users.FirstAsync(u => u.Email == "v1@example.com");
        user.EmailVerifiedAt.Should().NotBeNull();

        var token = await _db.UserTokens.FirstAsync(t => t.TokenHash == RefreshTokenHasher.Hash(rawToken));
        token.ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task VerifyEmail_TokenIsSingleUse()
    {
        await _sut.RegisterAsync(new RegisterDto("v2@example.com", "password123", "User"));
        var rawToken = _email.LastToken();

        await _sut.VerifyEmailAsync(new VerifyEmailDto(rawToken));

        var act = () => _sut.VerifyEmailAsync(new VerifyEmailDto(rawToken));
        await act.Should().ThrowAsync<ValidationException>().WithMessage("invalid_or_expired_token");
    }

    [Fact]
    public async Task VerifyEmail_WithExpiredToken_Throws()
    {
        var user = await RegisterVerifiedAsync("v3@example.com");
        var raw = SeedToken(user.Id, UserTokenType.EmailVerification, expiresIn: TimeSpan.FromHours(-1));

        var act = () => _sut.VerifyEmailAsync(new VerifyEmailDto(raw));
        await act.Should().ThrowAsync<ValidationException>().WithMessage("invalid_or_expired_token");
    }

    [Fact]
    public async Task VerifyEmail_WithWrongToken_Throws()
    {
        var act = () => _sut.VerifyEmailAsync(new VerifyEmailDto("not-a-real-token"));
        await act.Should().ThrowAsync<ValidationException>().WithMessage("invalid_or_expired_token");
    }

    [Fact]
    public async Task VerifyEmail_RejectsPasswordResetTokenOfSameRawValue()
    {
        // Type discriminator matters: a reset token must not verify an e-mail.
        var user = await RegisterVerifiedAsync("v4@example.com");
        var raw = SeedToken(user.Id, UserTokenType.PasswordReset, expiresIn: TimeSpan.FromHours(1));

        var act = () => _sut.VerifyEmailAsync(new VerifyEmailDto(raw));
        await act.Should().ThrowAsync<ValidationException>().WithMessage("invalid_or_expired_token");
    }

    // ── Resend verification (anti-enumeration) ──────────────────────────────────

    [Fact]
    public async Task ResendVerification_KnownUnverified_SendsNewLink()
    {
        await _sut.RegisterAsync(new RegisterDto("r1@example.com", "password123", "User"));
        _email.Messages.Clear();

        var result = await _sut.ResendVerificationAsync(new ResendVerificationDto("r1@example.com"));

        result.Message.Should().NotBeNullOrEmpty();
        _email.Messages.Should().ContainSingle();
        var tokenHash = RefreshTokenHasher.Hash(_email.LastToken());
        (await _db.UserTokens.AnyAsync(t =>
            t.TokenHash == tokenHash && t.Type == UserTokenType.EmailVerification)).Should().BeTrue();
    }

    [Fact]
    public async Task ResendVerification_AlreadyVerified_NoMail_GenericMessage()
    {
        await RegisterVerifiedAsync("r2@example.com");

        var result = await _sut.ResendVerificationAsync(new ResendVerificationDto("r2@example.com"));

        result.Message.Should().NotBeNullOrEmpty();
        _email.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task ResendVerification_UnknownEmail_NoMail_GenericMessage()
    {
        var result = await _sut.ResendVerificationAsync(new ResendVerificationDto("nobody@example.com"));

        result.Message.Should().NotBeNullOrEmpty();
        _email.Messages.Should().BeEmpty();
    }

    // ── Forgot password (anti-enumeration) ──────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_KnownEmail_CreatesResetToken_SendsMail()
    {
        await RegisterVerifiedAsync("f1@example.com");

        var result = await _sut.ForgotPasswordAsync(new ForgotPasswordDto("f1@example.com"));

        result.Message.Should().NotBeNullOrEmpty();
        _email.Messages.Should().ContainSingle();
        var tokenHash = RefreshTokenHasher.Hash(_email.LastToken());
        (await _db.UserTokens.AnyAsync(t =>
            t.TokenHash == tokenHash && t.Type == UserTokenType.PasswordReset)).Should().BeTrue();
    }

    [Fact]
    public async Task ForgotPassword_UnknownEmail_NoMail_NoToken()
    {
        var result = await _sut.ForgotPasswordAsync(new ForgotPasswordDto("ghost@example.com"));

        result.Message.Should().NotBeNullOrEmpty();
        _email.Messages.Should().BeEmpty();
        (await _db.UserTokens.AnyAsync(t => t.Type == UserTokenType.PasswordReset)).Should().BeFalse();
    }

    [Fact]
    public async Task ForgotPassword_And_Resend_ReturnIdenticalMessage_ForKnownAndUnknown()
    {
        await RegisterVerifiedAsync("known@example.com");

        var forgotKnown = await _sut.ForgotPasswordAsync(new ForgotPasswordDto("known@example.com"));
        var forgotUnknown = await _sut.ForgotPasswordAsync(new ForgotPasswordDto("unknown@example.com"));
        forgotKnown.Message.Should().Be(forgotUnknown.Message);

        // Resend uses the same generic message for unverified/verified/unknown alike
        var resendKnown = await _sut.ResendVerificationAsync(new ResendVerificationDto("known@example.com"));
        var resendUnknown = await _sut.ResendVerificationAsync(new ResendVerificationDto("unknown@example.com"));
        resendKnown.Message.Should().Be(resendUnknown.Message);
    }

    // ── Password reset ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_HappyPath_ChangesPassword_ConsumesToken()
    {
        await RegisterVerifiedAsync("p1@example.com", "oldpassword");
        await _sut.ForgotPasswordAsync(new ForgotPasswordDto("p1@example.com"));
        var rawToken = _email.LastToken();

        await _sut.ResetPasswordAsync(new ResetPasswordDto(rawToken, "brandnewpass1"));

        var token = await _db.UserTokens.FirstAsync(t => t.TokenHash == RefreshTokenHasher.Hash(rawToken));
        token.ConsumedAt.Should().NotBeNull();

        var user = await _db.Users.FirstAsync(u => u.Email == "p1@example.com");
        new BCryptPasswordHasher().Verify("brandnewpass1", user.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task ResetPassword_OldPasswordFails_NewPasswordLogsIn()
    {
        await RegisterVerifiedAsync("p2@example.com", "oldpassword");
        await _sut.ForgotPasswordAsync(new ForgotPasswordDto("p2@example.com"));

        await _sut.ResetPasswordAsync(new ResetPasswordDto(_email.LastToken(), "brandnewpass1"));

        var oldLogin = () => _sut.LoginAsync(new LoginDto("p2@example.com", "oldpassword"));
        await oldLogin.Should().ThrowAsync<UnauthorizedException>();

        var result = await _sut.LoginAsync(new LoginDto("p2@example.com", "brandnewpass1"));
        result.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResetPassword_RevokesAllRefreshTokens()
    {
        var user = await RegisterVerifiedAsync("p3@example.com", "oldpassword");
        // two live sessions
        var session1 = await _sut.LoginAsync(new LoginDto("p3@example.com", "oldpassword"));
        var session2 = await _sut.LoginAsync(new LoginDto("p3@example.com", "oldpassword"));
        _email.Messages.Clear();

        await _sut.ForgotPasswordAsync(new ForgotPasswordDto("p3@example.com"));
        await _sut.ResetPasswordAsync(new ResetPasswordDto(_email.LastToken(), "brandnewpass1"));

        (await _db.RefreshTokens.AnyAsync(t => t.UserId == user.Id && t.RevokedAt == null))
            .Should().BeFalse();
        await ((Func<Task>)(() => _sut.RefreshAsync(session1.RefreshToken))).Should().ThrowAsync<UnauthorizedException>();
        await ((Func<Task>)(() => _sut.RefreshAsync(session2.RefreshToken))).Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task ResetPassword_TokenIsSingleUse()
    {
        await RegisterVerifiedAsync("p4@example.com", "oldpassword");
        await _sut.ForgotPasswordAsync(new ForgotPasswordDto("p4@example.com"));
        var rawToken = _email.LastToken();

        await _sut.ResetPasswordAsync(new ResetPasswordDto(rawToken, "brandnewpass1"));

        var act = () => _sut.ResetPasswordAsync(new ResetPasswordDto(rawToken, "anotherpass99"));
        await act.Should().ThrowAsync<ValidationException>().WithMessage("invalid_or_expired_token");
    }

    [Fact]
    public async Task ResetPassword_WithExpiredToken_Throws()
    {
        var user = await RegisterVerifiedAsync("p5@example.com");
        var raw = SeedToken(user.Id, UserTokenType.PasswordReset, expiresIn: TimeSpan.FromMinutes(-1));

        var act = () => _sut.ResetPasswordAsync(new ResetPasswordDto(raw, "brandnewpass1"));
        await act.Should().ThrowAsync<ValidationException>().WithMessage("invalid_or_expired_token");
    }

    [Fact]
    public async Task ResetPassword_WithWrongToken_Throws()
    {
        var act = () => _sut.ResetPasswordAsync(new ResetPasswordDto("bogus-token", "brandnewpass1"));
        await act.Should().ThrowAsync<ValidationException>().WithMessage("invalid_or_expired_token");
    }

    [Fact]
    public async Task ForgotPassword_SupersedesPriorResetToken()
    {
        await RegisterVerifiedAsync("p6@example.com");

        await _sut.ForgotPasswordAsync(new ForgotPasswordDto("p6@example.com"));
        var firstToken = _email.LastToken();
        await _sut.ForgotPasswordAsync(new ForgotPasswordDto("p6@example.com"));

        // the first link is invalidated when a second is requested
        var act = () => _sut.ResetPasswordAsync(new ResetPasswordDto(firstToken, "brandnewpass1"));
        await act.Should().ThrowAsync<ValidationException>().WithMessage("invalid_or_expired_token");
    }

    // ── Locale in mail links (Prompt 18c) ───────────────────────────────────────

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task Register_LinkCarriesRequestedLocalePrefix(string locale)
    {
        await _sut.RegisterAsync(new RegisterDto($"loc-{locale}@example.com", "password123", "User", locale));

        _email.Last.Body.Should().Contain($"https://app.invoiceflow.test/{locale}/verify-email?token=");
    }

    [Fact]
    public async Task Register_EnglishLocale_UsesEnglishSubject()
    {
        await _sut.RegisterAsync(new RegisterDto("loc-en-subj@example.com", "password123", "User", "en"));

        _email.Last.Subject.Should().Be("Confirm your email address");
    }

    [Fact]
    public async Task Register_GermanLocale_UsesGermanSubject()
    {
        await _sut.RegisterAsync(new RegisterDto("loc-de-subj@example.com", "password123", "User", "de"));

        _email.Last.Subject.Should().Be("Bestätige deine E-Mail-Adresse");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("EN-US")]
    public async Task Register_MissingOrUnknownLocale_FallsBackToGerman(string? locale)
    {
        await _sut.RegisterAsync(new RegisterDto("loc-fallback@example.com", "password123", "User", locale));

        _email.Last.Body.Should().Contain("https://app.invoiceflow.test/de/verify-email?token=");
        _email.Last.Subject.Should().Be("Bestätige deine E-Mail-Adresse");
    }

    [Theory]
    [InlineData("en/../../evil.com")]
    [InlineData("de/admin")]
    [InlineData("en?x=1")]
    [InlineData("https://evil.com")]
    public async Task Register_LocaleCannotInjectIntoUrl_FallsBackToGerman(string malicious)
    {
        await _sut.RegisterAsync(new RegisterDto("loc-inject@example.com", "password123", "User", malicious));

        // The path segment is always one allowlisted literal — the payload never
        // reaches the URL.
        _email.Last.Body.Should().Contain("https://app.invoiceflow.test/de/verify-email?token=");
        _email.Last.Body.Should().NotContain("evil");
        _email.Last.Body.Should().NotContain("admin");
    }

    [Fact]
    public async Task ForgotPassword_EnglishLocale_LocalizesLinkAndSubject()
    {
        await RegisterVerifiedAsync("loc-forgot@example.com");

        await _sut.ForgotPasswordAsync(new ForgotPasswordDto("loc-forgot@example.com", "en"));

        _email.Last.Body.Should().Contain("https://app.invoiceflow.test/en/reset-password?token=");
        _email.Last.Subject.Should().Be("Reset your password");
    }

    [Fact]
    public async Task ResendVerification_EnglishLocale_LocalizesLink()
    {
        await _sut.RegisterAsync(new RegisterDto("loc-resend@example.com", "password123", "User"));
        _email.Messages.Clear();

        await _sut.ResendVerificationAsync(new ResendVerificationDto("loc-resend@example.com", "en"));

        _email.Last.Body.Should().Contain("https://app.invoiceflow.test/en/verify-email?token=");
        _email.Last.Subject.Should().Be("Confirm your email address");
    }

    // Inserts a token row directly with a known raw value and TTL.
    private string SeedToken(Guid userId, UserTokenType type, TimeSpan expiresIn)
    {
        var raw = SecureToken.Generate();
        _db.UserTokens.Add(new UserToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = type,
            TokenHash = RefreshTokenHasher.Hash(raw),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(expiresIn)
        });
        _db.SaveChanges();
        return raw;
    }

    public void Dispose() => _db.Dispose();
}
