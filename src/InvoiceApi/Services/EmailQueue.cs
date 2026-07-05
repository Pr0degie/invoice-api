using System.Threading.Channels;

namespace InvoiceApi.Services;

/// <summary>A queued outbound e-mail — plain text, resolved link already baked in.</summary>
public readonly record struct EmailMessage(string To, string Subject, string Body);

/// <summary>
/// Hands an e-mail off for out-of-band delivery. Enqueuing is non-blocking and
/// never touches the network, so the request path can't leak SMTP latency (an
/// enumeration oracle on forgot-password / resend-verification) and a mail
/// server outage can't fail the request. Actual sending happens in
/// <see cref="EmailBackgroundService"/>.
/// </summary>
public interface IEmailQueue
{
    void Enqueue(EmailMessage message);
    ChannelReader<EmailMessage> Reader { get; }
}

/// <summary>
/// <see cref="System.Threading.Channels"/>-backed in-process queue. Unbounded:
/// mail volume is auth-flow-driven (register / reset / resend), so the queue
/// stays tiny and dropping a message would be worse than briefly growing it.
/// Registered as a singleton; the background sender is the single reader.
/// </summary>
public class ChannelEmailQueue : IEmailQueue
{
    private readonly Channel<EmailMessage> _channel =
        Channel.CreateUnbounded<EmailMessage>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<EmailMessage> Reader => _channel.Reader;

    public void Enqueue(EmailMessage message) => _channel.Writer.TryWrite(message);
}
