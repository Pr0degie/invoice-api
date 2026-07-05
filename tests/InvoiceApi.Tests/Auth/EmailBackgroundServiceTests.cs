using FluentAssertions;
using InvoiceApi.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace InvoiceApi.Tests.Auth;

/// <summary>
/// Covers the request-path decoupling itself: what the queue hands off, the
/// background worker delivers via the scoped <see cref="IEmailSender"/>, and a
/// sender failure is swallowed (logged) rather than crashing the worker.
/// </summary>
public class EmailBackgroundServiceTests
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = new();

        public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
        {
            Sent.Add((toEmail, subject, body));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSender : IEmailSender
    {
        public int Attempts { get; private set; }

        public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
        {
            Attempts++;
            throw new InvalidOperationException("SMTP down");
        }
    }

    private static (ChannelEmailQueue queue, EmailBackgroundService service) Build(IEmailSender sender)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => sender)
            .BuildServiceProvider();
        var queue = new ChannelEmailQueue();
        var service = new EmailBackgroundService(
            queue,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EmailBackgroundService>.Instance);
        return (queue, service);
    }

    // Drives the worker until `predicate` holds (message delivered) or the timeout
    // trips, then signals shutdown so ExecuteAsync completes.
    private static async Task RunUntilAsync(EmailBackgroundService service, Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await service.StopAsync(CancellationToken.None);
        await run;
    }

    [Fact]
    public async Task Worker_DrainsQueue_DeliversViaSender()
    {
        var sender = new RecordingSender();
        var (queue, service) = Build(sender);

        queue.Enqueue(new EmailMessage("a@example.com", "Subject A", "Body A"));
        queue.Enqueue(new EmailMessage("b@example.com", "Subject B", "Body B"));

        await RunUntilAsync(service, () => sender.Sent.Count == 2);

        sender.Sent.Should().BeEquivalentTo(new[]
        {
            ("a@example.com", "Subject A", "Body A"),
            ("b@example.com", "Subject B", "Body B")
        });
    }

    [Fact]
    public async Task Worker_SwallowsSenderFailure_AndKeepsDraining()
    {
        var sender = new ThrowingSender();
        var (queue, service) = Build(sender);

        queue.Enqueue(new EmailMessage("boom@example.com", "S", "B"));
        // A second message must still be attempted — one failure can't kill the loop.
        queue.Enqueue(new EmailMessage("next@example.com", "S", "B"));

        var act = () => RunUntilAsync(service, () => sender.Attempts == 2);

        await act.Should().NotThrowAsync();
        sender.Attempts.Should().Be(2);
    }
}
