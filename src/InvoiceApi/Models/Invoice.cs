namespace InvoiceApi.Models;

public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = default!;

    public string SenderName { get; set; } = default!;
    public string SenderAddress { get; set; } = default!;

    public string RecipientName { get; set; } = default!;
    public string RecipientAddress { get; set; } = default!;

    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }

    public List<LineItem> LineItems { get; set; } = [];

    public decimal TaxRate { get; set; } = 0.19m;
    public string Currency { get; set; } = "EUR";
    public string? Notes { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    // Stored snapshot of Total (Subtotal + TaxAmount) — kept in sync by InvoiceService.
    // Needed for stats aggregate queries because the computed Total property is EF-ignored.
    public decimal TotalAmount { get; set; }

    // Set when Status transitions to Paid; cleared on any other status.
    public DateOnly? PaidAt { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // computed — not stored
    public decimal Subtotal => LineItems.Sum(i => i.Total);
    public decimal TaxAmount => Math.Round(Subtotal * TaxRate, 2);
    public decimal Total => Subtotal + TaxAmount;
}

public class LineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }

    public string Description { get; set; } = default!;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string Unit { get; set; } = "h"; // h, piece, day, flat, ...

    public decimal Total => Math.Round(Quantity * UnitPrice, 2);
}

public enum InvoiceStatus
{
    Draft,
    Sent,
    Paid,
    Overdue,
    Cancelled
}
