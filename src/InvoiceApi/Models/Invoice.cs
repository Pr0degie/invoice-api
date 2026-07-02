namespace InvoiceApi.Models;

public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Null while Draft. Assigned atomically at finalization from InvoiceNumberSequence
    // (format "{year}-{counter:000}", per user, resets each year). Never reused.
    public string? Number { get; set; }

    public InvoiceType Type { get; set; } = InvoiceType.Invoice;

    public string SenderName { get; set; } = default!;
    public string SenderAddress { get; set; } = default!;

    public string RecipientName { get; set; } = default!;
    public string RecipientAddress { get; set; } = default!;

    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }

    // Leistungsdatum bzw. -zeitraum (§ 14 Abs. 4 Nr. 6 UStG) — exactly one of the
    // two forms must be set before the invoice can be finalized.
    public DateOnly? ServiceDate { get; set; }
    public DateOnly? ServicePeriodStart { get; set; }
    public DateOnly? ServicePeriodEnd { get; set; }

    public List<LineItem> LineItems { get; set; } = [];

    public decimal TaxRate { get; set; } = 0.19m;
    public string Currency { get; set; } = "EUR";
    public string? Notes { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    // Snapshot of User.IsSmallBusiness taken at finalization — drives the § 19 UStG
    // notice on the archived PDF and must not change retroactively.
    public bool IsSmallBusiness { get; set; }

    // Set on Cancellation invoices (Storno): the original they reverse.
    public Guid? CancellationOfId { get; set; }
    public string? CancellationOfNumber { get; set; }

    // Stored snapshot of Total (Subtotal + TaxAmount) — kept in sync by InvoiceService.
    // Needed for stats aggregate queries because the computed Total property is EF-ignored.
    public decimal TotalAmount { get; set; }

    // Set when Status transitions to Paid; cleared on Cancelled.
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
    Draft = 0,

    // Legally fixed: number assigned, PDF archived, business fields immutable.
    Finalized = 1,

    Paid = 2,

    // Terminal. Reached only via the Storno endpoint, which issues the
    // reversing Cancellation invoice. "Overdue" is not a stored status — it is
    // derived (Finalized + past due date) and exposed as IsOverdue.
    Cancelled = 3,
}

public enum InvoiceType
{
    Invoice = 0,
    Cancellation = 1, // Stornorechnung — negative amounts, references the original
}

// Archived render of a finalized invoice (GoBD: the PDF handed out is the PDF
// stored — finalized documents are never re-rendered from live data).
public class InvoicePdf
{
    public Guid InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;
    public byte[] Data { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Per-user, per-year invoice number counter. Counter doubles as the concurrency
// token: two concurrent finalizations conflict on save and one retries.
public class InvoiceNumberSequence
{
    public Guid UserId { get; set; }
    public int Year { get; set; }
    public int Counter { get; set; }
}
