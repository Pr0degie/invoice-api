using FluentAssertions;
using InvoiceApi.Models;
using InvoiceApi.Services;
using Microsoft.Extensions.Configuration;
using System.IdentityModel.Tokens.Jwt;

namespace InvoiceApi.Tests.Auth;

public class JwtTokenServiceTests
{
    private readonly JwtTokenService _sut;
    private readonly User _testUser;

    public JwtTokenServiceTests()
    {
        _sut = new JwtTokenService(BuildConfig());
        _testUser = new User
        {
            Id = Guid.NewGuid(),
            Email = "test@example.com",
            Name = "Test User",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    [Fact]
    public void GenerateAccessToken_ShouldContainCorrectClaims()
    {
        var token = _sut.GenerateAccessToken(_testUser);

        token.Should().NotBeNullOrEmpty();

        var principal = _sut.ValidateToken(token);
        principal.Should().NotBeNull();

        principal!.FindFirst("sub")?.Value.Should().Be(_testUser.Id.ToString());
        principal.FindFirst("email")?.Value.Should().Be(_testUser.Email);
        principal.FindFirst("name")?.Value.Should().Be(_testUser.Name);
    }

    [Fact]
    public void GenerateAccessToken_ShouldHave15MinuteExpiry()
    {
        var before = DateTime.UtcNow;
        var token = _sut.GenerateAccessToken(_testUser);
        var after = DateTime.UtcNow;

        var handler = new JwtSecurityTokenHandler();
        var parsed = handler.ReadJwtToken(token);

        parsed.ValidTo.Should().BeOnOrAfter(before.AddMinutes(14));
        parsed.ValidTo.Should().BeOnOrBefore(after.AddMinutes(16));
    }

    [Fact]
    public void ValidateToken_WithExpiredToken_ShouldReturnNull()
    {
        // Build a service with 0-minute token lifetime
        var shortConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "invoice-api",
                ["Jwt:Audience"] = "invoiceflow",
                ["Jwt:SigningKey"] = "test-signing-key-for-unit-tests-only-32chars",
                ["Jwt:AccessTokenMinutes"] = "0"
            })
            .Build();

        var shortService = new JwtTokenService(shortConfig);
        var token = shortService.GenerateAccessToken(_testUser);

        // Small delay to ensure expiry
        Thread.Sleep(100);

        var principal = shortService.ValidateToken(token);
        principal.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_WithTamperedToken_ShouldReturnNull()
    {
        var token = _sut.GenerateAccessToken(_testUser);
        var tampered = token[..^5] + "XXXXX";

        var principal = _sut.ValidateToken(tampered);
        principal.Should().BeNull();
    }

    [Fact]
    public void GenerateRefreshToken_ShouldBeUniqueEachTime()
    {
        var token1 = _sut.GenerateRefreshToken();
        var token2 = _sut.GenerateRefreshToken();

        token1.Should().NotBe(token2);
        token1.Should().HaveLength(88); // 64 bytes base64 = 88 chars
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
}
