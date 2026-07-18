using FluentAssertions;
using InvoiceApi.Data;
using InvoiceApi.Models;
using InvoiceApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Tests.Auth;

public class RefreshTokenCleanupServiceTests : IDisposable
{
    // SQLite in-memory instead of the InMemory provider: CleanupAsync uses
    // ExecuteDeleteAsync, which InMemory does not support.
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly DateTime _now = DateTime.UtcNow;

    public RefreshTokenCleanupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(opts);
        _db.Database.EnsureCreated();
    }

    private async Task<User> AddUserAsync(string email)
    {
        var user = new User { Email = email, PasswordHash = "hash", Name = "Test User" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task<RefreshToken> AddTokenAsync(User user, DateTime expiresAt, DateTime? revokedAt = null)
    {
        var token = new RefreshToken
        {
            Token = Guid.NewGuid().ToString("N"),
            ExpiresAt = expiresAt,
            CreatedAt = _now.AddDays(-30),
            RevokedAt = revokedAt,
            UserId = user.Id,
        };
        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync();
        return token;
    }

    private Task<int> CleanupAsync() =>
        RefreshTokenCleanupService.CleanupAsync(_db, _now, Retention);

    [Fact]
    public async Task ExpiredToken_OlderThanRetention_IsDeleted()
    {
        var user = await AddUserAsync("cleanup1@example.com");
        await AddTokenAsync(user, expiresAt: _now - Retention - TimeSpan.FromDays(1));

        var deleted = await CleanupAsync();

        deleted.Should().Be(1);
        (await _db.RefreshTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RevokedToken_OlderThanRetention_IsDeleted()
    {
        var user = await AddUserAsync("cleanup2@example.com");
        // Not yet expired — deletion is triggered by the old revocation alone.
        await AddTokenAsync(user, expiresAt: _now.AddDays(10),
            revokedAt: _now - Retention - TimeSpan.FromDays(1));

        var deleted = await CleanupAsync();

        deleted.Should().Be(1);
        (await _db.RefreshTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ValidToken_IsKept()
    {
        var user = await AddUserAsync("cleanup3@example.com");
        await AddTokenAsync(user, expiresAt: _now.AddDays(10));

        var deleted = await CleanupAsync();

        deleted.Should().Be(0);
        (await _db.RefreshTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task FreshlyExpiredToken_WithinRetention_IsKept()
    {
        var user = await AddUserAsync("cleanup4@example.com");
        await AddTokenAsync(user, expiresAt: _now.AddHours(-1));

        var deleted = await CleanupAsync();

        deleted.Should().Be(0);
        (await _db.RefreshTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task FreshlyRevokedToken_WithinRetention_IsKept()
    {
        // Guards the RotationGraceSeconds semantics in AuthService: a token
        // revoked seconds ago must still be acceptable within the rotation
        // grace window, so the cleanup must never touch it. Retention (7 d)
        // dwarfs the grace period (60 s) — this test pins that invariant.
        var user = await AddUserAsync("cleanup5@example.com");
        await AddTokenAsync(user, expiresAt: _now.AddDays(10),
            revokedAt: _now.AddSeconds(-30));

        var deleted = await CleanupAsync();

        deleted.Should().Be(0);
        (await _db.RefreshTokens.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task MixedTokensAcrossUsers_DeletesOnlyMatchingRows()
    {
        var alice = await AddUserAsync("alice@example.com");
        var bob = await AddUserAsync("bob@example.com");

        var aliceExpiredOld = await AddTokenAsync(alice, expiresAt: _now - Retention - TimeSpan.FromDays(2));
        var aliceValid = await AddTokenAsync(alice, expiresAt: _now.AddDays(10));
        var bobRevokedOld = await AddTokenAsync(bob, expiresAt: _now.AddDays(10),
            revokedAt: _now - Retention - TimeSpan.FromHours(1));
        var bobFreshlyExpired = await AddTokenAsync(bob, expiresAt: _now.AddDays(-1));

        var deleted = await CleanupAsync();

        deleted.Should().Be(2);

        var remainingIds = await _db.RefreshTokens.Select(t => t.Id).ToListAsync();
        remainingIds.Should().BeEquivalentTo([aliceValid.Id, bobFreshlyExpired.Id]);
        remainingIds.Should().NotContain([aliceExpiredOld.Id, bobRevokedOld.Id]);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
