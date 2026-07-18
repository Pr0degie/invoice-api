namespace InvoiceApi.Services;

/// <summary>
/// Configuration for <see cref="RefreshTokenCleanupService"/>, bound from the
/// <c>RefreshTokenCleanup</c> section. The defaults apply when the section is
/// absent — no config entry is required for the service to run.
/// </summary>
public class RefreshTokenCleanupOptions
{
    /// <summary>Time between two cleanup runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long an expired or revoked token is kept before it is hard-deleted.
    /// Must stay far above <c>AuthService.RotationGraceSeconds</c> so the
    /// rotation grace window is never undercut by the cleanup.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}
