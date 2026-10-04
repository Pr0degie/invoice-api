namespace InvoiceApi.Services;

public static class LogRedaction
{
    /// <summary>
    /// "tobias@example.com" → "t***@example.com". Logs outlive the account
    /// (retention, shipping to a log host), so they get enough to follow one
    /// delivery through the lines — not the address itself.
    /// </summary>
    public static string MaskEmail(string email)
    {
        var at = email.LastIndexOf('@');
        return at < 1 ? "***" : $"{email[0]}***{email[at..]}";
    }
}
