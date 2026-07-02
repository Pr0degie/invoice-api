using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Models;

public record CreateInvoiceRequest
{
    [Required, MinLength(1)] public string SenderName { get; init; } = default!;
    [Required, MinLength(1)] public string SenderAddress { get; init; } = default!;
    [Required, MinLength(1)] public string RecipientName { get; init; } = default!;
    [Required, MinLength(1)] public string RecipientAddress { get; init; } = default!;
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
}

public record UpdateStatusRequest
{
    [Required] public InvoiceStatus Status { get; init; }
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
    decimal Total
);
