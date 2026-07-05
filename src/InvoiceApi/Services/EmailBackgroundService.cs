namespace InvoiceApi.Services;

/// <summary>
/// Drains the <see cref="IEmailQueue"/> and delivers each message via the
/// configured <see cref="IEmailSender"/>. A delivery failure is logged, never
/// propagated — the originating request already returned, so a dead SMTP server
/// costs a log line, not a failed registration or reset. Runs a fresh DI scope
/// per message so the scoped sender is resolved cleanly.
/// </summary>
public class EmailBackgroundService(
    IEmailQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<EmailBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
                await sender.SendAsync(message.To, message.Subject, message.Body, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown — stop draining, don't log as an error.
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "E-Mail an {To} konnte nicht versendet werden (Betreff: {Subject})",
                    message.To, message.Subject);
            }
        }
    }
}
