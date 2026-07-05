using System.Text.RegularExpressions;
using InvoiceApi.Services;

namespace InvoiceApi.Tests.Auth;

/// <summary>
/// Test double for <see cref="IEmailSender"/>: records every message so a test
/// can assert one was sent and pull the raw token out of the link (the only
/// place the raw token exists — at rest it's a SHA-256 hash).
/// </summary>
public class CapturingEmailSender : IEmailSender
{
    public record Sent(string To, string Subject, string Body);

    public List<Sent> Messages { get; } = new();

    public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        Messages.Add(new Sent(toEmail, subject, body));
        return Task.CompletedTask;
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
