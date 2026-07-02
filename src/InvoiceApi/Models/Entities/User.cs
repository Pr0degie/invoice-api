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

    // Sender tax profile (§ 14 Abs. 4 UStG Pflichtangaben). An invoice can only be
    // finalized when the postal address is complete and TaxNumber or VatId is set.
    public string? TaxNumber { get; set; }   // Steuernummer
    public string? VatId { get; set; }       // USt-IdNr.
    public bool IsSmallBusiness { get; set; } = true; // Kleinunternehmer, § 19 UStG

    public string? Street { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }

    // Bank details for the PDF footer — optional, not finalization-blocking.
    public string? Iban { get; set; }
    public string? Bic { get; set; }
    public string? BankName { get; set; }

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

    /// <summary>Hash of the token that replaced this one on rotation. Set = revoked by rotation (vs logout).</summary>
    public string? ReplacedByTokenHash { get; set; }

    public bool IsRevoked => RevokedAt.HasValue;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}
