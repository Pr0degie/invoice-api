namespace InvoiceApi.Models;

public class User
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string? DefaultSenderName { get; set; }
    public string? DefaultSenderAddress { get; set; }

    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}

public class RefreshToken
{
    public Guid Id { get; set; }
    public required string Token { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsRevoked => RevokedAt.HasValue;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}
