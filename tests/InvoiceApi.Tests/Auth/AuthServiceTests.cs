using FluentAssertions;
using InvoiceApi.Data;
using InvoiceApi.Exceptions;
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

        var oldToken = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == oldRefreshToken);
        oldToken!.IsRevoked.Should().BeTrue();
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
