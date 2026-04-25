using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Models;

public record CreateInvoiceRequest
{
    [Required, MinLength(1)]
    public string SenderName { get; init; } = default!;

    [Required, MinLength(1)]
    public string SenderAddress { get; init; } = default!;

    [Required, MinLength(1)]
    public string RecipientName { get; init; } = default!;

    [Required, MinLength(1)]
    public string RecipientAddress { get; init; } = default!;

    public DateOnly? IssueDate { get; init; }
    public DateOnly? DueDate { get; init; }

    [Required, MinLength(1)]
    public List<CreateLineItemRequest> LineItems { get; init; } = default!;

    [Range(0, 1)]
    public decimal TaxRate { get; init; } = 0.19m;

    public string Currency { get; init; } = "EUR";
    public string? Notes { get; init; }
}

public record CreateLineItemRequest
{
    [Required]
    public string Description { get; init; } = default!;

    [Range(0.001, double.MaxValue)]
    public decimal Quantity { get; init; }

    [Range(0, double.MaxValue)]
    public decimal UnitPrice { get; init; }

    public string Unit { get; init; } = "h";
}

public record UpdateStatusRequest
{
    [Required]
    public InvoiceStatus Status { get; init; }
}

public record InvoiceResponse(
    Guid Id,
    string Number,
    InvoiceStatus Status,
    string SenderName,
    string SenderAddress,
    string RecipientName,
    string RecipientAddress,
    DateOnly IssueDate,
    DateOnly DueDate,
    DateOnly? PaidAt,
    string Currency,
    decimal TaxRate,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total,
    List<LineItemResponse> LineItems,
    string? Notes,
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
