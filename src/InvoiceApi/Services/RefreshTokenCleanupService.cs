using InvoiceApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InvoiceApi.Services;

/// <summary>
/// Periodically hard-deletes refresh tokens that are expired or revoked and
/// past the retention window — without it the <c>RefreshTokens</c> table grows
/// unbounded (rotation/logout only ever revoke, never delete). Runs once at
/// startup, then every <see cref="RefreshTokenCleanupOptions.Interval"/>.
/// A failed run is logged, never propagated — the next tick simply tries again
/// (same contract as <see cref="EmailBackgroundService"/>).
/// </summary>
public class RefreshTokenCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<RefreshTokenCleanupOptions> options,
    ILogger<RefreshTokenCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown — not an error.
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var deleted = await CleanupAsync(db, DateTime.UtcNow, options.Value.Retention, ct);

            if (deleted > 0)
                logger.LogInformation("Refresh-Token-Cleanup: {Anzahl} abgelaufene/revokte Tokens gelöscht", deleted);
            else
                logger.LogDebug("Refresh-Token-Cleanup: nichts zu löschen");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Refresh-Token-Cleanup-Lauf fehlgeschlagen — nächster Versuch in {Interval}",
                options.Value.Interval);
        }
    }

    /// <summary>
    /// Deletes every refresh token whose expiry or revocation lies further back
    /// than <paramref name="retention"/> relative to <paramref name="now"/>.
    /// One DELETE statement via <c>ExecuteDeleteAsync</c>, no entity loading.
    /// Deliberately not user-scoped: this is system-wide maintenance, not a
    /// request-path query. Public (the repo has no InternalsVisibleTo) so tests
    /// can hit the rule directly without timer mechanics.
    /// </summary>
    /// <returns>Number of deleted rows.</returns>
    public static Task<int> CleanupAsync(AppDbContext db, DateTime now, TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = now - retention;
        return db.RefreshTokens
            .Where(t => t.ExpiresAt < cutoff || (t.RevokedAt != null && t.RevokedAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }
}
