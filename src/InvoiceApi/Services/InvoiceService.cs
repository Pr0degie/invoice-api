using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Services;

public interface IInvoiceService
{
    Task<InvoiceResponse> CreateAsync(CreateInvoiceRequest request, CancellationToken ct = default);
    Task<InvoiceResponse> GetAsync(Guid id, CancellationToken ct = default);
    Task<List<InvoiceResponse>> ListAsync(InvoiceStatus? status, int page, int pageSize, CancellationToken ct = default);
    Task<InvoiceResponse> UpdateStatusAsync(Guid id, InvoiceStatus newStatus, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public class InvoiceService(AppDbContext db) : IInvoiceService
{
    public async Task<InvoiceResponse> CreateAsync(CreateInvoiceRequest req, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        var invoice = new Invoice
        {
Number = await GenerateNumberAsync(ct) ?? "INV-ERROR",
            SenderName = req.SenderName,
            SenderAddress = req.SenderAddress,
            RecipientName = req.RecipientName,
            RecipientAddress = req.RecipientAddress,
            IssueDate = req.IssueDate ?? today,
            DueDate = req.DueDate ?? today.AddDays(14),
            TaxRate = req.TaxRate,
            Currency = req.Currency.ToUpperInvariant(),
            Notes = req.Notes,
            LineItems = req.LineItems.Select(li => new LineItem
            {
                Description = li.Description,
                Quantity = li.Quantity,
                UnitPrice = li.UnitPrice,
                Unit = li.Unit
            }).ToList()
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);

        return invoice.ToResponse();
    }

    public async Task<InvoiceResponse> GetAsync(Guid id, CancellationToken ct = default)
    {
        var invoice = await db.Invoices
            .Include(i => i.LineItems)
            .FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        return invoice.ToResponse();
    }

    public async Task<List<InvoiceResponse>> ListAsync(InvoiceStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.Invoices.Include(i => i.LineItems).AsQueryable();

        if (status.HasValue)
            query = query.Where(i => i.Status == status.Value);

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => i.ToResponse())
            .ToListAsync(ct);
    }

    public async Task<InvoiceResponse> UpdateStatusAsync(Guid id, InvoiceStatus newStatus, CancellationToken ct = default)
    {
        var invoice = await db.Invoices
            .Include(i => i.LineItems)
            .FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        invoice.Status = newStatus;
        invoice.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return invoice.ToResponse();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.FindAsync([id], ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        db.Invoices.Remove(invoice);
        await db.SaveChangesAsync(ct);
    }

    // ---

    private async Task<string?> GenerateNumberAsync(CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var count = await db.Invoices.CountAsync(i => i.IssueDate.Year == year, ct);
        return $"INV-{year}-{(count + 1):D4}";
    }
}

internal static class InvoiceMappings
{
    public static InvoiceResponse ToResponse(this Invoice i) => new(
        i.Id,
        i.Number,
        i.SenderName,
        i.RecipientName,
        i.IssueDate,
        i.DueDate,
        i.Subtotal,
        i.TaxAmount,
        i.Total,
        i.Currency,
        i.Status,
        i.LineItems.Select(li => new LineItemResponse(
            li.Id, li.Description, li.Quantity, li.Unit, li.UnitPrice, li.Total
        )).ToList(),
        i.Notes,
        i.CreatedAt
    );
}
