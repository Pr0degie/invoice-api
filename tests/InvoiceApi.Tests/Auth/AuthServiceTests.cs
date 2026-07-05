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

public class AuthServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly CapturingEmailQueue _email = new();
    private readonly AuthService _sut;

    public AuthServiceTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(opts);

        var config = BuildConfig();
        var passwordHasher = new BCryptPasswordHasher();
        var jwtService = new JwtTokenService(config);

        _sut = new AuthService(_db, passwordHasher, jwtService, _email, config,
            new MemoryCache(new MemoryCacheOptions()));
    }

    // Register then flip the account to verified so it can log in — the common
    // arrangement for tests that aren't about the verification flow itself.
    private async Task<User> RegisterVerifiedAsync(string email, string password = "password123", string name = "Test User")
    {
        await _sut.RegisterAsync(new RegisterDto(email, password, name));
        var user = await _db.Users.FirstAsync(u => u.Email == email.ToLowerInvariant());
        user.EmailVerifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task<AuthResponseDto> RegisterVerifyLoginAsync(string email, string password = "password123", string name = "Test User")
    {
        await RegisterVerifiedAsync(email, password, name);
        return await _sut.LoginAsync(new LoginDto(email, password));
    }

    [Fact]
    public async Task Register_CreatesUnverifiedUser_SendsVerificationEmail_NoSession()
    {
        var result = await _sut.RegisterAsync(new RegisterDto("test@example.com", "password123", "Test User"));

        result.Message.Should().NotBeNullOrEmpty();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == "test@example.com");
        user.Should().NotBeNull();
        user!.EmailVerifiedAt.Should().BeNull();

        // No session (refresh token) is minted at registration
        (await _db.RefreshTokens.AnyAsync(t => t.UserId == user.Id)).Should().BeFalse();

        // A verification e-mail with a redeemable token went out
        _email.Messages.Should().ContainSingle();
        _email.Last.To.Should().Be("test@example.com");
        var tokenHash = RefreshTokenHasher.Hash(_email.LastToken());
        (await _db.UserTokens.AnyAsync(t =>
            t.TokenHash == tokenHash && t.Type == UserTokenType.EmailVerification)).Should().BeTrue();
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ShouldThrowValidationException()
    {
        await _sut.RegisterAsync(new RegisterDto("dup@example.com", "password123", "User 1"));

        var act = () => _sut.RegisterAsync(new RegisterDto("dup@example.com", "password123", "User 2"));

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*Email already in use*");
    }

    [Fact]
    public async Task Register_ShouldNormalizeEmailToLowercase()
    {
        await _sut.RegisterAsync(new RegisterDto("Upper@Example.COM", "password123", "User"));

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == "upper@example.com");
        user.Should().NotBeNull();
    }

    [Fact]
    public async Task Login_WithValidVerifiedCredentials_ShouldReturnTokens()
    {
        await RegisterVerifiedAsync("login@example.com", "mypassword");

        var result = await _sut.LoginAsync(new LoginDto("login@example.com", "mypassword"));

        result.Token.Should().NotBeNullOrEmpty();
        result.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_Unverified_ShouldThrowForbidden_EmailNotVerified()
    {
        await _sut.RegisterAsync(new RegisterDto("unverified@example.com", "password123", "User"));

        var act = () => _sut.LoginAsync(new LoginDto("unverified@example.com", "password123"));

        (await act.Should().ThrowAsync<ForbiddenException>())
            .WithMessage("email_not_verified");
    }

    [Fact]
    public async Task Login_AfterVerification_ShouldSucceed()
    {
        await _sut.RegisterAsync(new RegisterDto("verifyme@example.com", "password123", "User"));

        // Still blocked before verification
        var before = () => _sut.LoginAsync(new LoginDto("verifyme@example.com", "password123"));
        await before.Should().ThrowAsync<ForbiddenException>();

        await _sut.VerifyEmailAsync(new VerifyEmailDto(_email.LastToken()));

        var after = await _sut.LoginAsync(new LoginDto("verifyme@example.com", "password123"));
        after.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShouldThrowUnauthorizedException()
    {
        await RegisterVerifiedAsync("user@example.com", "correctpass");

        var act = () => _sut.LoginAsync(new LoginDto("user@example.com", "wrongpass"));

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Login_WithUnknownEmail_ShouldThrowUnauthorizedException()
    {
        var act = () => _sut.LoginAsync(new LoginDto("nobody@example.com", "somepass"));

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Login_ShouldStoreRefreshTokenHashed_NotRaw()
    {
        var result = await RegisterVerifyLoginAsync("hashed@example.com");

        (await _db.RefreshTokens.AnyAsync(t => t.Token == result.RefreshToken)).Should().BeFalse();
        (await _db.RefreshTokens.AnyAsync(t => t.Token == RefreshTokenHasher.Hash(result.RefreshToken)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Login_ShouldDeleteExpiredRefreshTokens()
    {
        var user = await RegisterVerifiedAsync("expired@example.com");

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = RefreshTokenHasher.Hash("some-expired-token"),
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow.AddDays(-60),
            ExpiresAt = DateTime.UtcNow.AddDays(-30)
        });
        await _db.SaveChangesAsync();

        await _sut.LoginAsync(new LoginDto("expired@example.com", "password123"));

        (await _db.RefreshTokens.AnyAsync(t => t.UserId == user.Id && t.ExpiresAt < DateTime.UtcNow))
            .Should().BeFalse();
        // only the freshly-minted login token survives
        (await _db.RefreshTokens.CountAsync(t => t.UserId == user.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Refresh_WithValidToken_ShouldReturnNewTokensAndRevokeOld()
    {
        var loggedIn = await RegisterVerifyLoginAsync("refresh@example.com");
        var oldRefreshToken = loggedIn.RefreshToken;

        var refreshed = await _sut.RefreshAsync(oldRefreshToken);

        refreshed.Token.Should().NotBeNullOrEmpty();
        refreshed.RefreshToken.Should().NotBe(oldRefreshToken);

        var oldToken = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.Token == RefreshTokenHasher.Hash(oldRefreshToken));
        oldToken!.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_WithRevokedToken_ShouldThrowUnauthorizedException()
    {
        var loggedIn = await RegisterVerifyLoginAsync("revoke@example.com");
        var refreshToken = loggedIn.RefreshToken;

        await _sut.RevokeRefreshTokenAsync(refreshToken);

        var act = () => _sut.RefreshAsync(refreshToken);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Refresh_WithJustRotatedToken_WithinGrace_ReturnsSameSuccessorTokens()
    {
        var loggedIn = await RegisterVerifyLoginAsync("grace@example.com");
        var oldRefreshToken = loggedIn.RefreshToken;

        var first = await _sut.RefreshAsync(oldRefreshToken);
        var second = await _sut.RefreshAsync(oldRefreshToken);

        second.RefreshToken.Should().Be(first.RefreshToken);
        second.Token.Should().Be(first.Token);
    }

    [Fact]
    public async Task Refresh_WithRotatedToken_AfterGrace_RevokesAllUserTokens()
    {
        var loggedIn = await RegisterVerifyLoginAsync("theft@example.com");
        var userId = loggedIn.User.Id;
        var oldRefreshToken = loggedIn.RefreshToken;

        var successor = await _sut.RefreshAsync(oldRefreshToken);

        var rotated = await _db.RefreshTokens
            .FirstAsync(t => t.Token == RefreshTokenHasher.Hash(oldRefreshToken));
        rotated.RevokedAt = DateTime.UtcNow.AddSeconds(-120);
        await _db.SaveChangesAsync();

        var act = () => _sut.RefreshAsync(oldRefreshToken);
        await act.Should().ThrowAsync<UnauthorizedException>();

        (await _db.RefreshTokens.AnyAsync(t => t.UserId == userId && t.RevokedAt == null))
            .Should().BeFalse();
        var successorAct = () => _sut.RefreshAsync(successor.RefreshToken);
        await successorAct.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Refresh_WithInvalidToken_ShouldThrowUnauthorizedException()
    {
        var act = () => _sut.RefreshAsync("totally-invalid-token");

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task ChangePassword_WithCorrectCurrent_UpdatesHashAndRevokesRefreshTokens()
    {
        var loggedIn = await RegisterVerifyLoginAsync("chpw@example.com", "oldpassword");
        var oldRefreshToken = loggedIn.RefreshToken;
        var userId = loggedIn.User.Id;

        await _sut.ChangePasswordAsync(userId, new ChangePasswordDto("oldpassword", "newpassword123"));

        var user = await _db.Users.FindAsync(userId);
        new BCryptPasswordHasher().Verify("newpassword123", user!.PasswordHash).Should().BeTrue();

        var token = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.Token == RefreshTokenHasher.Hash(oldRefreshToken));
        token!.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ChangePassword_WithWrongCurrent_ThrowsValidationException_AndLeavesDbUnchanged()
    {
        var user = await RegisterVerifiedAsync("chpw2@example.com", "correctpass");
        var originalHash = user.PasswordHash;

        var act = () => _sut.ChangePasswordAsync(user.Id, new ChangePasswordDto("wrongpass", "newpassword123"));

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("invalid_current_password");

        var reloaded = await _db.Users.FindAsync(user.Id);
        reloaded!.PasswordHash.Should().Be(originalHash);
    }

    [Fact]
    public async Task ChangePassword_NonExistentUser_ThrowsUnauthorizedException()
    {
        var act = () => _sut.ChangePasswordAsync(Guid.NewGuid(), new ChangePasswordDto("any", "newpassword123"));

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task ChangePassword_OldPasswordLoginFails_NewPasswordLoginSucceeds()
    {
        var user = await RegisterVerifiedAsync("chpw3@example.com", "oldpassword");

        await _sut.ChangePasswordAsync(user.Id, new ChangePasswordDto("oldpassword", "newpassword123"));

        var failAct = () => _sut.LoginAsync(new LoginDto("chpw3@example.com", "oldpassword"));
        await failAct.Should().ThrowAsync<UnauthorizedException>();

        var result = await _sut.LoginAsync(new LoginDto("chpw3@example.com", "newpassword123"));
        result.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ChangePassword_PreChangeRefreshToken_IsRevoked()
    {
        var loggedIn = await RegisterVerifyLoginAsync("chpw4@example.com", "oldpassword");
        var preChangeRefreshToken = loggedIn.RefreshToken;

        await _sut.ChangePasswordAsync(loggedIn.User.Id, new ChangePasswordDto("oldpassword", "newpassword123"));

        var act = () => _sut.RefreshAsync(preChangeRefreshToken);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task UpdateProfile_ShouldChangeOnlyProvidedFields()
    {
        var user = await RegisterVerifiedAsync("profile@example.com", name: "Original Name");

        var result = await _sut.UpdateProfileAsync(user.Id, new UpdateProfileDto("New Name", null, null));

        result.Name.Should().Be("New Name");
        result.DefaultSenderName.Should().BeNull();
        result.DefaultSenderAddress.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProfile_ShouldClearWhenEmptyString()
    {
        var user = await RegisterVerifiedAsync("clear@example.com");

        await _sut.UpdateProfileAsync(user.Id, new UpdateProfileDto(null, "Tobias", "123 Street"));
        var result = await _sut.UpdateProfileAsync(user.Id, new UpdateProfileDto(null, "", null));

        result.DefaultSenderName.Should().BeNull();
        result.DefaultSenderAddress.Should().Be("123 Street");
    }

    [Fact]
    public async Task UpdateProfile_ShouldUpdateUpdatedAt()
    {
        var user = await RegisterVerifiedAsync("updated@example.com");
        var before = DateTime.UtcNow;

        await _sut.UpdateProfileAsync(user.Id, new UpdateProfileDto(null, "Tobias", null));

        var reloaded = await _db.Users.FindAsync(user.Id);
        reloaded!.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task UpdateProfile_NonExistentUser_ShouldThrowUnauthorizedException()
    {
        var act = () => _sut.UpdateProfileAsync(Guid.NewGuid(), new UpdateProfileDto("Name", null, null));

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task DeleteAccount_RemovesUserAndCascadesInvoicesAndRefreshTokens()
    {
        var loggedIn = await RegisterVerifyLoginAsync("delete@example.com");
        var userId = loggedIn.User.Id;

        var invoice = new Invoice
        {
            UserId = userId,
            Number = "INV-2026-0001",
            SenderName = "Sender",
            SenderAddress = "Addr",
            RecipientName = "Recipient",
            RecipientAddress = "Addr",
            IssueDate = DateOnly.FromDateTime(DateTime.Today),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(30)),
            LineItems = [new LineItem { Description = "Work", Quantity = 1, UnitPrice = 100m }]
        };
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync();
        var invoiceId = invoice.Id;

        await _sut.DeleteAccountAsync(userId);

        (await _db.Users.CountAsync(u => u.Id == userId)).Should().Be(0);
        (await _db.Invoices.CountAsync(i => i.UserId == userId)).Should().Be(0);
        (await _db.LineItems.CountAsync(li => li.InvoiceId == invoiceId)).Should().Be(0);
        (await _db.RefreshTokens.CountAsync(t => t.UserId == userId)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteAccount_NonExistent_ThrowsUnauthorized()
    {
        var act = () => _sut.DeleteAccountAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    // ---

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "invoice-api",
                ["Jwt:Audience"] = "invoiceflow",
                ["Jwt:SigningKey"] = "test-signing-key-for-unit-tests-only-32chars",
                ["Jwt:AccessTokenMinutes"] = "15",
                ["Jwt:RefreshTokenDays"] = "30",
                ["App:FrontendBaseUrl"] = "http://localhost:3000"
            })
            .Build();

    public void Dispose() => _db.Dispose();
}
