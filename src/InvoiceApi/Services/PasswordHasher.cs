namespace InvoiceApi.Services;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public class BCryptPasswordHasher : IPasswordHasher
{
    // Pinned explicitly instead of relying on the library default. BCrypt encodes
    // the cost in the hash itself, so existing hashes with a different cost keep
    // verifying — they are transparently upgraded only when the password changes.
    public const int WorkFactor = 12;

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}
