using System.Security.Cryptography;
using System.Text;

namespace InvoiceApi.Services;

/// <summary>
/// Refresh tokens are stored as SHA-256 hashes so a database leak doesn't
/// expose usable tokens. The raw token is returned to the client exactly once.
/// </summary>
public static class RefreshTokenHasher
{
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
