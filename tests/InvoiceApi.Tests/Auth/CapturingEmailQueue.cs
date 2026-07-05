using System.Text.RegularExpressions;
using System.Threading.Channels;
using InvoiceApi.Services;

namespace InvoiceApi.Tests.Auth;

/// <summary>
/// Test double for <see cref="IEmailQueue"/>: records every enqueued message so a
/// test can assert one was queued and pull the raw token out of the link (the only
/// place the raw token exists — at rest it's a SHA-256 hash). Recording on enqueue
/// keeps the assertions synchronous; no background worker is exercised in unit tests.
/// </summary>
public class CapturingEmailQueue : IEmailQueue
{
    public record Sent(string To, string Subject, string Body);

    public List<Sent> Messages { get; } = new();

    private readonly Channel<EmailMessage> _channel = Channel.CreateUnbounded<EmailMessage>();

    public ChannelReader<EmailMessage> Reader => _channel.Reader;

    public void Enqueue(EmailMessage message)
    {
        Messages.Add(new Sent(message.To, message.Subject, message.Body));
        _channel.Writer.TryWrite(message);
    }

    public Sent Last => Messages[^1];

    /// <summary>Extracts the <c>?token=…</c> value from the most recent mail's link.</summary>
    public string LastToken()
    {
        var match = Regex.Match(Last.Body, @"token=([A-Za-z0-9_\-]+)");
        if (!match.Success)
            throw new InvalidOperationException("No token found in the last e-mail body.");
        return match.Groups[1].Value;
    }
}
