using FluentAssertions;
using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using InvoiceApi.Models.Dtos;
using InvoiceApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvoiceApi.Tests.Auth;

public class AuthServiceTests : IDisposable
{
    private readonly AppDbContext _db;
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

        _sut = new AuthService(_db, passwordHasher, jwtService, config);
    }

    [Fact]
    public async Task Register_ShouldCreateUserAndReturnTokens()
    {
        var dto = new RegisterDto("test@example.com", "password123", "Test User");

        var result = await _sut.RegisterAsync(dto);

        result.Token.Should().NotBeNullOrEmpty();
        result.RefreshToken.Should().NotBeNullOrEmpty();
        result.User.Email.Should().Be("test@example.com");
        result.User.Name.Should().Be("Test User");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == "test@example.com");
        user.Should().NotBeNull();

        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.UserId == user!.Id);
        token.Should().NotBeNull();
        token!.IsRevoked.Should().BeFalse();
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
    public async Task Login_WithValidCredentials_ShouldReturnTokens()
    {
        await _sut.RegisterAsync(new RegisterDto("login@example.com", "mypassword", "Login User"));

        var result = await _sut.LoginAsync(new LoginDto("login@example.com", "mypassword"));

        result.Token.Should().NotBeNullOrEmpty();
        result.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShouldThrowUnauthorizedException()
    {
        await _sut.RegisterAsync(new RegisterDto("user@example.com", "correctpass", "User"));

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
    public async Task Refresh_WithValidToken_ShouldReturnNewTokensAndRevokeOld()
    {
        var registered = await _sut.RegisterAsync(new RegisterDto("refresh@example.com", "password123", "User"));
        var oldRefreshToken = registered.RefreshToken;

        var refreshed = await _sut.RefreshAsync(oldRefreshToken);

        refreshed.Token.Should().NotBeNullOrEmpty();
        refreshed.RefreshToken.Should().NotBe(oldRefreshToken);

        var oldToken = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.Token == RefreshTokenHasher.Hash(oldRefreshToken));
        oldToken!.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task Register_ShouldStoreRefreshTokenHashed_NotRaw()
    {
        var result = await _sut.RegisterAsync(new RegisterDto("hashed@example.com", "password123", "User"));

        (await _db.RefreshTokens.AnyAsync(t => t.Token == result.RefreshToken)).Should().BeFalse();
        (await _db.RefreshTokens.AnyAsync(t => t.Token == RefreshTokenHasher.Hash(result.RefreshToken)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Login_ShouldDeleteExpiredRefreshTokens()
    {
        var registered = await _sut.RegisterAsync(new RegisterDto("expired@example.com", "password123", "User"));
        var userId = registered.User.Id;

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = RefreshTokenHasher.Hash("some-expired-token"),
            UserId = userId,
            CreatedAt = DateTime.UtcNow.AddDays(-60),
            ExpiresAt = DateTime.UtcNow.AddDays(-30)
        });
        await _db.SaveChangesAsync();

        await _sut.LoginAsync(new LoginDto("expired@example.com", "password123"));

        (await _db.RefreshTokens.AnyAsync(t => t.UserId == userId && t.ExpiresAt < DateTime.UtcNow))
            .Should().BeFalse();
        // non-expired tokens survive: the register token + the new login token
        (await _db.RefreshTokens.CountAsync(t => t.UserId == userId)).Should().Be(2);
    }

    [Fact]
    public async Task Refresh_WithRevokedToken_ShouldThrowUnauthorizedException()
    {
        var registered = await _sut.RegisterAsync(new RegisterDto("revoke@example.com", "password123", "User"));
        var refreshToken = registered.RefreshToken;

        await _sut.RevokeRefreshTokenAsync(refreshToken);

        var act = () => _sut.RefreshAsync(refreshToken);

        await act.Should().ThrowAsync<UnauthorizedException>();
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
        var registered = await _sut.RegisterAsync(new RegisterDto("chpw@example.com", "oldpassword", "User"));
        var oldRefreshToken = registered.RefreshToken;
        var userId = registered.User.Id;

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
        var registered = await _sut.RegisterAsync(new RegisterDto("chpw2@example.com", "correctpass", "User"));
        var userId = registered.User.Id;
        var originalHash = (await _db.Users.FindAsync(userId))!.PasswordHash;

        var act = () => _sut.ChangePasswordAsync(userId, new ChangePasswordDto("wrongpass", "newpassword123"));

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("invalid_current_password");

        var user = await _db.Users.FindAsync(userId);
        user!.PasswordHash.Should().Be(originalHash);
    }

    [Fact]
    public async Task ChangePassword_NonExistentUser_ThrowsNotFoundException()
    {
        var act = () => _sut.ChangePasswordAsync(Guid.NewGuid(), new ChangePasswordDto("any", "newpassword123"));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ChangePassword_OldPasswordLoginFails_NewPasswordLoginSucceeds()
    {
        await _sut.RegisterAsync(new RegisterDto("chpw3@example.com", "oldpassword", "User"));
        var userId = (await _db.Users.FirstAsync(u => u.Email == "chpw3@example.com")).Id;

        await _sut.ChangePasswordAsync(userId, new ChangePasswordDto("oldpassword", "newpassword123"));

        var failAct = () => _sut.LoginAsync(new LoginDto("chpw3@example.com", "oldpassword"));
        await failAct.Should().ThrowAsync<UnauthorizedException>();

        var result = await _sut.LoginAsync(new LoginDto("chpw3@example.com", "newpassword123"));
        result.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ChangePassword_PreChangeRefreshToken_IsRevoked()
    {
        var registered = await _sut.RegisterAsync(new RegisterDto("chpw4@example.com", "oldpassword", "User"));
        var preChangeRefreshToken = registered.RefreshToken;
        var userId = registered.User.Id;

        await _sut.ChangePasswordAsync(userId, new ChangePasswordDto("oldpassword", "newpassword123"));

        var act = () => _sut.RefreshAsync(preChangeRefreshToken);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task UpdateProfile_ShouldChangeOnlyProvidedFields()
    {
        var registered = await _sut.RegisterAsync(new RegisterDto("profile@example.com", "password123", "Original Name"));
        var userId = registered.User.Id;

        var result = await _sut.UpdateProfileAsync(userId, new UpdateProfileDto("New Name", null, null));

        result.Name.Should().Be("New Name");
        result.DefaultSenderName.Should().BeNull();
        result.DefaultSenderAddress.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProfile_ShouldClearWhenEmptyString()
    {
        var registered = await _sut.RegisterAsync(new RegisterDto("clear@example.com", "password123", "User"));
        var userId = registered.User.Id;

        await _sut.UpdateProfileAsync(userId, new UpdateProfileDto(null, "Tobias", "123 Street"));
        var result = await _sut.UpdateProfileAsync(userId, new UpdateProfileDto(null, "", null));

        result.DefaultSenderName.Should().BeNull();
        result.DefaultSenderAddress.Should().Be("123 Street");
    }

    [Fact]
    public async Task UpdateProfile_ShouldUpdateUpdatedAt()
    {
        var registered = await _sut.RegisterAsync(new RegisterDto("updated@example.com", "password123", "User"));
        var userId = registered.User.Id;
        var before = DateTime.UtcNow;

        await _sut.UpdateProfileAsync(userId, new UpdateProfileDto(null, "Tobias", null));

        var user = await _db.Users.FindAsync(userId);
        user!.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task UpdateProfile_NonExistentUser_ShouldThrowNotFoundException()
    {
        var act = () => _sut.UpdateProfileAsync(Guid.NewGuid(), new UpdateProfileDto("Name", null, null));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task DeleteAccount_RemovesUserAndCascadesInvoicesAndRefreshTokens()
    {
        var registered = await _sut.RegisterAsync(new RegisterDto("delete@example.com", "password123", "User"));
        var userId = registered.User.Id;

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
        var lineItemId = invoice.LineItems[0].Id;

        await _sut.DeleteAccountAsync(userId);

        (await _db.Users.CountAsync(u => u.Id == userId)).Should().Be(0);
        (await _db.Invoices.CountAsync(i => i.UserId == userId)).Should().Be(0);
        (await _db.LineItems.CountAsync(li => li.InvoiceId == invoiceId)).Should().Be(0);
        (await _db.RefreshTokens.CountAsync(t => t.UserId == userId)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteAccount_NonExistent_ThrowsNotFound()
    {
        var act = () => _sut.DeleteAccountAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
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
                ["Jwt:RefreshTokenDays"] = "30"
            })
            .Build();

    public void Dispose() => _db.Dispose();
}
