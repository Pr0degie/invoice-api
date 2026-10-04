using Npgsql;

namespace InvoiceApi.Services;

public static class DatabaseStartup
{
    /// <summary>
    /// Runs the startup migration, waiting out a database that isn't reachable yet
    /// (Postgres still booting, or restarted alongside the API after a host reboot)
    /// instead of crash-looping the container. Only transient connection errors are
    /// retried — a broken migration or wrong credentials fail the boot at once.
    /// </summary>
    /// <remarks>
    /// Deliberately not EnableRetryOnFailure: that execution strategy rejects the
    /// explicit transaction in AuthService.DeleteAccountAsync.
    /// </remarks>
    public static async Task MigrateWithRetryAsync(
        Func<Task> migrate, ILogger logger, int maxAttempts = 10, TimeSpan? delay = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await migrate();
                return;
            }
            catch (NpgsqlException ex) when (ex.IsTransient && attempt < maxAttempts)
            {
                logger.LogWarning("Database not reachable (attempt {Attempt}/{MaxAttempts}): {Message}",
                    attempt, maxAttempts, ex.Message);
                await Task.Delay(delay ?? TimeSpan.FromSeconds(3));
            }
        }
    }
}
