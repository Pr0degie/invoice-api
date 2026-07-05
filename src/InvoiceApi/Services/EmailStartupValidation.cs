namespace InvoiceApi.Services;

/// <summary>
/// Fail-fast validation of the e-mail configuration, called once from
/// <c>Program.cs</c>. Delivery runs in a background worker after the HTTP
/// request has already returned 200 — a broken SMTP config would otherwise
/// surface only as a runtime log entry while every mail is silently lost.
/// </summary>
public static class EmailStartupValidation
{
    public const string DefaultFrontendBaseUrl = "http://localhost:3000";

    /// <summary>
    /// Single source of truth for the base URL used in mail links.
    /// The FRONTEND_BASE_URL environment variable wins over the
    /// App:FrontendBaseUrl default baked into appsettings.json — with the
    /// opposite precedence the baked-in localhost value would shadow the
    /// env var on every deployment.
    /// </summary>
    public static string ResolveFrontendBaseUrl(IConfiguration config) =>
        (config["FRONTEND_BASE_URL"] ?? config["App:FrontendBaseUrl"] ?? DefaultFrontendBaseUrl)
            .TrimEnd('/');

    public static void Validate(IConfiguration config, bool isProduction)
    {
        var provider = config["Email:Provider"]?.Trim() ?? "";
        var isSmtp = provider.Equals("Smtp", StringComparison.OrdinalIgnoreCase);
        var isLog = provider.Equals("Log", StringComparison.OrdinalIgnoreCase);

        // An unrecognized value (e.g. a typo like "SMPT") would fall through to
        // the log-only sender and drop every mail without any error.
        if (provider.Length > 0 && !isSmtp && !isLog)
            throw new InvalidOperationException(
                $"Email:Provider '{provider}' is unknown (allowed: Smtp, Log). Refusing to start " +
                "because an unrecognized provider would silently route all mail to the log.");

        // appsettings.Production.json blanks the provider, so a production
        // deployment must make an explicit choice via Email__Provider.
        if (isProduction && provider.Length == 0)
            throw new InvalidOperationException(
                "Email:Provider is not configured. Production requires an explicit choice: " +
                "Email__Provider=Smtp (real delivery) or Email__Provider=Log " +
                "(log-only — outgoing mail is NOT delivered).");

        if (!isSmtp)
            return;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(config["Email:Smtp:Host"]))
            missing.Add("Email__Smtp__Host");
        if (string.IsNullOrWhiteSpace(config["Email:FromAddress"]))
            missing.Add("Email__FromAddress");
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Email:Provider=Smtp, but required settings are missing: {string.Join(", ", missing)}.");

        var portRaw = config["Email:Smtp:Port"];
        if (portRaw is not null && (!int.TryParse(portRaw, out var port) || port is < 1 or > 65535))
            throw new InvalidOperationException(
                $"Email:Smtp:Port '{portRaw}' is not a valid TCP port (1-65535).");

        var user = config["Email:Smtp:User"];
        var password = config["Email:Smtp:Password"];
        if (string.IsNullOrEmpty(user) != string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                "Email:Smtp:User and Email:Smtp:Password must be set together — " +
                "a partial credential pair fails on every send.");

        var frontendBaseUrl = ResolveFrontendBaseUrl(config);
        if (!Uri.TryCreate(frontendBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"Frontend base URL '{frontendBaseUrl}' is not an absolute http(s) URL. " +
                "Set FRONTEND_BASE_URL to the public frontend URL.");

        if (isProduction && uri.IsLoopback)
            throw new InvalidOperationException(
                "The frontend base URL resolves to localhost — every link in outgoing mail " +
                "would be unreachable for recipients. Set FRONTEND_BASE_URL to the public frontend URL.");
    }
}
