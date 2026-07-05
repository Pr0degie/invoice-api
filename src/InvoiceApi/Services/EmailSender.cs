using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace InvoiceApi.Services;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default);
}

/// <summary>
/// Development/test sender: writes the full message (subject + body incl. link)
/// to the log instead of sending it. Selected by default outside of an
/// explicit <c>Email:Provider=Smtp</c> configuration.
/// </summary>
public class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        logger.LogInformation(
            "[LogEmailSender] Mail an {To}\nBetreff: {Subject}\n{Body}",
            toEmail, subject, body);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Production sender over SMTP (MailKit). Configured via <c>Email:Smtp*</c>
/// (env vars <c>Email__Smtp__Host</c> etc.) plus <c>Email:FromAddress</c>/<c>FromName</c>.
/// Plain text only — no template framework.
/// </summary>
public class SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default)
    {
        var host = config["Email:Smtp:Host"]
            ?? throw new InvalidOperationException("Email:Smtp:Host is not configured.");
        var port = config.GetValue("Email:Smtp:Port", 587);
        var user = config["Email:Smtp:User"];
        var password = config["Email:Smtp:Password"];
        var useStartTls = config.GetValue("Email:Smtp:UseStartTls", true);

        var fromAddress = config["Email:FromAddress"] ?? "no-reply@invoiceflow.app";
        var fromName = config["Email:FromName"] ?? "InvoiceFlow";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        var socketOptions = useStartTls
            ? SecureSocketOptions.StartTls
            : SecureSocketOptions.Auto;

        await client.ConnectAsync(host, port, socketOptions, ct);
        if (!string.IsNullOrEmpty(user))
            await client.AuthenticateAsync(user, password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);

        logger.LogInformation("E-Mail an {To} versendet (Betreff: {Subject})", toEmail, subject);
    }
}
