using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Models;

public record CreateInvoiceRequest
{
    [Required, MinLength(1)] public string SenderName { get; init; } = default!;
    [Required, MinLength(1)] public string SenderAddress { get; init; } = default!;
    [Required, MinLength(1)] public string RecipientName { get; init; } = default!;

    // Legacy free-text recipient address. Optional now that structured fields exist:
    // the server composes this column from them when they're provided, so new clients
    // can leave it empty. Structured fields are enforced at finalization, not here.
    public string? RecipientAddress { get; init; }

    // Structured recipient (buyer) data for the E-Rechnung (XRechnung).
    public string? RecipientStreet { get; init; }
    public string? RecipientPostalCode { get; init; }
    public string? RecipientCity { get; init; }
    public string? RecipientCountryCode { get; init; } = "DE"; // ISO 3166-1 alpha-2
    [EmailAddress] public string? RecipientEmail { get; init; }  // BT-49
    public string? RecipientVatId { get; init; }                 // BT-48 (optional)
    public string? BuyerReference { get; init; }                 // BT-10 (defaults to "-" at finalize)

    public DateOnly? IssueDate { get; init; }
    public DateOnly? DueDate { get; init; }

    // Leistungsdatum or Leistungszeitraum — one of the two forms, never both.
    // Optional while Draft; required at finalization.
    public DateOnly? ServiceDate { get; init; }
    public DateOnly? ServicePeriodStart { get; init; }
    public DateOnly? ServicePeriodEnd { get; init; }

    [Required, MinLength(1)] public List<CreateLineItemRequest> LineItems { get; init; } = default!;
    [Range(0, 1)] public decimal TaxRate { get; init; } = 0.19m;
    public string Currency { get; init; } = "EUR";
    public string? Notes { get; init; }
}

public record CreateLineItemRequest
{
    [Required] public string Description { get; init; } = default!;
    [Range(0.001, double.MaxValue)] public decimal Quantity { get; init; }
    [Range(0, double.MaxValue)] public decimal UnitPrice { get; init; }
    public string Unit { get; init; } = "h";

    // Display-only: FlatRate renders the position as 1 × pauschal × line total.
    public LineItemDisplayMode DisplayMode { get; init; } = LineItemDisplayMode.AsEntered;
}

public record UpdateStatusRequest
{
    [Required] public InvoiceStatus Status { get; init; }
}

// Body of POST /api/invoices/{id}/finalize — entirely optional (no body = defaults).
public record FinalizeInvoiceRequest
{
    // Ausstellungsdatum for the finalized invoice. Defaults to today; future dates
    // are rejected (400). The invoice-number year derives from this date.
    public DateOnly? IssueDate { get; init; }
}

public record InvoiceResponse(
    Guid Id,
    string? Number,          // null while Draft
    InvoiceStatus Status,
    InvoiceType Type,
    bool IsOverdue,          // derived: Finalized invoice past its due date
    string SenderName,
    string SenderAddress,
    string RecipientName,
    string RecipientAddress,
    string? RecipientStreet,
    string? RecipientPostalCode,
    string? RecipientCity,
    string? RecipientCountryCode,
    string? RecipientEmail,
    string? RecipientVatId,
    string? BuyerReference,
    DateOnly IssueDate,
    DateOnly DueDate,
    DateOnly? ServiceDate,
    DateOnly? ServicePeriodStart,
    DateOnly? ServicePeriodEnd,
    DateOnly? PaidAt,
    string Currency,
    decimal TaxRate,
    bool IsSmallBusiness,    // § 19 UStG snapshot, meaningful once finalized
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total,
    List<LineItemResponse> LineItems,
    string? Notes,
    Guid? CancellationOfId,        // on Cancellation invoices: the reversed original
    string? CancellationOfNumber,
    string? CancelledByNumber,     // on Cancelled originals: the Storno's number (detail only)
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record LineItemResponse(
    Guid Id,
    string Description,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal Total,
    LineItemDisplayMode DisplayMode
);
