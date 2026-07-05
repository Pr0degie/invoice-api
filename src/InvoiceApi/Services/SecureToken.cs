using System.Security.Cryptography;

namespace InvoiceApi.Services;

/// <summary>
/// Generates cryptographically-random, URL-safe tokens for out-of-band flows
/// (password reset, e-mail verification). The raw value travels once in an
/// e-mail link; only its SHA-256 hash (<see cref="RefreshTokenHasher.Hash"/>)
/// is persisted — same hardening as refresh tokens (see ADR 0001 / 0006).
/// </summary>
public static class SecureToken
{
    public static string Generate()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        // URL-safe base64 (no padding) so the raw token drops straight into a
        // ?token=... query string without percent-encoding.
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
