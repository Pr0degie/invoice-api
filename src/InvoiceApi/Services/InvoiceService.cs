using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Services;

public interface IInvoiceService
{
    Task<InvoiceResponse> CreateAsync(CreateInvoiceRequest request, CancellationToken ct = default);
    Task<InvoiceResponse> GetAsync(Guid id, CancellationToken ct = default);
    Task<List<InvoiceResponse>> ListAsync(InvoiceStatus? status, bool overdueOnly, int page, int pageSize, string? search = null, CancellationToken ct = default);
    Task<InvoiceResponse> UpdateAsync(Guid id, CreateInvoiceRequest request, CancellationToken ct = default);
    Task<InvoiceResponse> UpdateStatusAsync(Guid id, InvoiceStatus newStatus, CancellationToken ct = default);
    Task<InvoiceResponse> FinalizeAsync(Guid id, DateOnly? issueDate = null, CancellationToken ct = default);
    Task<InvoiceResponse> ReopenAsync(Guid id, CancellationToken ct = default);
    Task<InvoiceResponse> CancelAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public class InvoiceService(AppDbContext db, ICurrentUserService currentUser, IPdfService pdf, IEInvoiceService einvoice) : IInvoiceService
{
    // Concurrent finalizations race on the sequence counter (concurrency token) or,
    // as a backstop, the unique (UserId, Number) index — losers retry with a fresh number.
    private const int MaxNumberingAttempts = 5;

    public async Task<InvoiceResponse> CreateAsync(CreateInvoiceRequest req, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var userId = currentUser.CurrentUserId;

        ValidateServiceDates(req);

        var lineItems = req.LineItems.Select((li, index) => new LineItem
        {
            Description = li.Description,
            Quantity = li.Quantity,
            UnitPrice = li.UnitPrice,
            Unit = li.Unit,
            Position = index,
            DisplayMode = li.DisplayMode
        }).ToList();

        var subtotal = lineItems.Sum(li => li.Total);
        var invoice = new Invoice
        {
            UserId = userId,
            Number = null, // assigned at finalization
            SenderName = req.SenderName,
            SenderAddress = req.SenderAddress,
            RecipientName = req.RecipientName,
            RecipientAddress = ComposeRecipientAddress(req),
            RecipientStreet = req.RecipientStreet,
            RecipientPostalCode = req.RecipientPostalCode,
            RecipientCity = req.RecipientCity,
            RecipientCountryCode = req.RecipientCountryCode,
            RecipientEmail = req.RecipientEmail,
            RecipientVatId = req.RecipientVatId,
            BuyerReference = req.BuyerReference,
            IssueDate = req.IssueDate ?? today,
            DueDate = req.DueDate ?? today.AddDays(14),
            ServiceDate = req.ServiceDate,
            ServicePeriodStart = req.ServicePeriodStart,
            ServicePeriodEnd = req.ServicePeriodEnd,
            TaxRate = req.TaxRate,
            Currency = req.Currency.ToUpperInvariant(),
            Notes = req.Notes,
            LineItems = lineItems,
            TotalAmount = subtotal + Math.Round(subtotal * req.TaxRate, 2)
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);

        return invoice.ToResponse();
    }

    public async Task<InvoiceResponse> GetAsync(Guid id, CancellationToken ct = default)
    {
        var userId = currentUser.CurrentUserId;

        var invoice = await db.Invoices
            .Include(i => i.LineItems.OrderBy(li => li.Position))
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        string? cancelledByNumber = null;
        if (invoice.Status == InvoiceStatus.Cancelled)
            cancelledByNumber = await db.Invoices
                .Where(i => i.CancellationOfId == id && i.UserId == userId)
                .Select(i => i.Number)
                .FirstOrDefaultAsync(ct);

        return invoice.ToResponse(cancelledByNumber);
    }

    public async Task<List<InvoiceResponse>> ListAsync(InvoiceStatus? status, bool overdueOnly, int page, int pageSize, string? search = null, CancellationToken ct = default)
    {
        var userId = currentUser.CurrentUserId;
        var query = db.Invoices.Include(i => i.LineItems.OrderBy(li => li.Position)).Where(i => i.UserId == userId);

        if (status.HasValue)
            query = query.Where(i => i.Status == status.Value);

        if (overdueOnly)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            query = query.Where(i => i.Status == InvoiceStatus.Finalized
                && i.Type == InvoiceType.Invoice
                && i.DueDate < today);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(i =>
                (i.Number != null && i.Number.ToLower().Contains(term)) ||
                i.RecipientName.ToLower().Contains(term));
        }

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => i.ToResponse(null))
            .ToListAsync(ct);
    }

    public async Task<InvoiceResponse> UpdateAsync(Guid id, CreateInvoiceRequest req, CancellationToken ct = default)
    {
        var userId = currentUser.CurrentUserId;

        ValidateServiceDates(req);

        // Load without Include to avoid navigation-collection proxy conflicts with InMemory provider.
        var invoice = await db.Invoices
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        if (invoice.Status != InvoiceStatus.Draft)
            throw new ConflictException("Only draft invoices can be edited.");

        invoice.SenderName = req.SenderName;
        invoice.SenderAddress = req.SenderAddress;
        invoice.RecipientName = req.RecipientName;
        invoice.RecipientAddress = ComposeRecipientAddress(req);
        invoice.RecipientStreet = req.RecipientStreet;
        invoice.RecipientPostalCode = req.RecipientPostalCode;
        invoice.RecipientCity = req.RecipientCity;
        invoice.RecipientCountryCode = req.RecipientCountryCode;
        invoice.RecipientEmail = req.RecipientEmail;
        invoice.RecipientVatId = req.RecipientVatId;
        invoice.BuyerReference = req.BuyerReference;
        invoice.IssueDate = req.IssueDate ?? invoice.IssueDate;
        invoice.DueDate = req.DueDate ?? invoice.DueDate;
        invoice.ServiceDate = req.ServiceDate;
        invoice.ServicePeriodStart = req.ServicePeriodStart;
        invoice.ServicePeriodEnd = req.ServicePeriodEnd;
        invoice.Currency = req.Currency.ToUpperInvariant();
        invoice.TaxRate = req.TaxRate;
        invoice.Notes = req.Notes;
        invoice.UpdatedAt = DateTimeOffset.UtcNow;

        // Replace line items via DbSet to avoid navigation-collection proxy conflicts.
        var old = await db.LineItems.Where(li => li.InvoiceId == id).ToListAsync(ct);
        db.LineItems.RemoveRange(old);

        var newItems = req.LineItems.Select((li, index) => new LineItem
        {
            InvoiceId = id,
            Description = li.Description,
            Quantity = li.Quantity,
            Unit = li.Unit,
            UnitPrice = li.UnitPrice,
            Position = index,
            DisplayMode = li.DisplayMode
        }).ToList();
        db.LineItems.AddRange(newItems);

        var subtotal = newItems.Sum(li => li.Total);
        invoice.TotalAmount = subtotal + Math.Round(subtotal * invoice.TaxRate, 2);

        await db.SaveChangesAsync(ct);

        invoice.LineItems = newItems;
        return invoice.ToResponse();
    }

    // Draft never leaves via PATCH — finalization runs through FinalizeAsync, which
    // enforces the § 14 preconditions. Cancelled is only reachable via CancelAsync
    // (Storno). Paid→Finalized is the undo path for a mistaken payment mark.
    private static readonly Dictionary<InvoiceStatus, InvoiceStatus[]> AllowedTransitions = new()
    {
        [InvoiceStatus.Draft] = [],
        [InvoiceStatus.Finalized] = [InvoiceStatus.Paid],
        [InvoiceStatus.Paid] = [InvoiceStatus.Finalized],
        [InvoiceStatus.Cancelled] = [],
    };

    public async Task<InvoiceResponse> UpdateStatusAsync(Guid id, InvoiceStatus newStatus, CancellationToken ct = default)
    {
        var userId = currentUser.CurrentUserId;

        var invoice = await db.Invoices
            .Include(i => i.LineItems.OrderBy(li => li.Position))
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        if (invoice.Type == InvoiceType.Cancellation)
            throw new ConflictException("Cancellation invoices cannot change status.");

        if (!AllowedTransitions[invoice.Status].Contains(newStatus))
            throw new ConflictException(invoice.Status == InvoiceStatus.Draft && newStatus == InvoiceStatus.Finalized
                ? "Drafts are finalized via POST /api/invoices/{id}/finalize."
                : $"Cannot change status from {invoice.Status} to {newStatus}.");

        invoice.Status = newStatus;
        invoice.UpdatedAt = DateTimeOffset.UtcNow;

        if (newStatus == InvoiceStatus.Paid)
            invoice.PaidAt ??= DateOnly.FromDateTime(DateTime.UtcNow);
        // Paid→Finalized keeps PaidAt so a Paid→Finalized→Paid round trip preserves the original date

        await db.SaveChangesAsync(ct);
        return invoice.ToResponse();
    }

    public async Task<InvoiceResponse> FinalizeAsync(Guid id, DateOnly? issueDate = null, CancellationToken ct = default)
    {
        var userId = currentUser.CurrentUserId;
        var today = DateOnly.FromDateTime(DateTime.Today);

        if (issueDate > today)
            throw new ValidationException("The issue date must not be in the future.");

        var invoice = await db.Invoices
            .Include(i => i.LineItems.OrderBy(li => li.Position))
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        if (invoice.Status != InvoiceStatus.Draft)
            throw new ConflictException("Only draft invoices can be finalized.");

        if (invoice.ServiceDate is null && invoice.ServicePeriodStart is null)
            throw new ConflictException("A service date or service period is required to finalize (§ 14 Abs. 4 UStG).");

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null || user.DeletedAt is not null) // anonymized account (ADR 0005) = gone
            throw new UnauthorizedException("User not found.");
        EnsureSenderProfileComplete(user);
        EnsureRecipientComplete(invoice);

        // E-Rechnung mandatory defaults (BR-DE-15 buyer reference, buyer country).
        invoice.BuyerReference = string.IsNullOrWhiteSpace(invoice.BuyerReference)
            ? "-"
            : invoice.BuyerReference.Trim();
        invoice.RecipientCountryCode = string.IsNullOrWhiteSpace(invoice.RecipientCountryCode)
            ? "DE"
            : invoice.RecipientCountryCode.Trim().ToUpperInvariant();

        // Ausstellungsdatum is the finalization date, not the draft's creation date —
        // a stale draft date would become the legal issue date on the archived PDF.
        // Payment terms travel with it: the draft's DueDate−IssueDate span (never
        // negative) is re-applied relative to the new IssueDate. Must happen before
        // AssignNumberAndSaveAsync — the invoice-number year derives from IssueDate.
        var paymentTermDays = Math.Max(0, invoice.DueDate.DayNumber - invoice.IssueDate.DayNumber);
        invoice.IssueDate = issueDate ?? today;
        invoice.DueDate = invoice.IssueDate.AddDays(paymentTermDays);

        // § 19 UStG: Kleinunternehmer invoices carry no VAT — enforced here, at the
        // legal fixation point, regardless of what the draft contained.
        if (user.IsSmallBusiness)
            invoice.TaxRate = 0m;
        invoice.IsSmallBusiness = user.IsSmallBusiness;
        invoice.TotalAmount = invoice.Total;
        invoice.Status = InvoiceStatus.Finalized;
        invoice.UpdatedAt = DateTimeOffset.UtcNow;

        await AssignNumberAndSaveAsync(invoice, user, ct);
        return invoice.ToResponse();
    }

    // Deliberate, audited exception to invoice immutability (ADR 0003): a finalized
    // invoice that has NOT yet reached the recipient may be reset to Draft for
    // correction. The assigned number stays on the invoice and the sequence is
    // untouched — re-finalization reuses the number, so numbering stays gap-free.
    // Sent/booked documents are corrected via Storno + new invoice, never reopened.
    public async Task<InvoiceResponse> ReopenAsync(Guid id, CancellationToken ct = default)
    {
        var userId = currentUser.CurrentUserId;

        var invoice = await db.Invoices
            .Include(i => i.LineItems.OrderBy(li => li.Position))
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        if (invoice.Type == InvoiceType.Cancellation)
            throw new ConflictException("Cancellation invoices cannot be reopened.");

        if (invoice.Status == InvoiceStatus.Draft)
            throw new ValidationException("The invoice is already a draft.");

        if (invoice.Status != InvoiceStatus.Finalized)
            throw new ConflictException(
                $"Cannot reopen a {invoice.Status} invoice — corrections go through Storno + new invoice.");

        invoice.Status = InvoiceStatus.Draft;
        invoice.UpdatedAt = DateTimeOffset.UtcNow;

        // The archived PDF/XML no longer represents the (now mutable) draft — discard
        // both. A fresh render + XRechnung is archived at re-finalization.
        var archived = await db.InvoicePdfs.FindAsync([invoice.Id], ct);
        if (archived is not null)
            db.InvoicePdfs.Remove(archived);
        var archivedXml = await db.InvoiceXmls.FindAsync([invoice.Id], ct);
        if (archivedXml is not null)
            db.InvoiceXmls.Remove(archivedXml);

        db.InvoiceAuditEntries.Add(new InvoiceAuditEntry
        {
            InvoiceId = invoice.Id,
            UserId = userId,
            Action = InvoiceAuditActions.Reopened,
            Note = $"Invoice {invoice.Number} reset to draft for pre-dispatch correction; number retained."
        });

        await db.SaveChangesAsync(ct);
        return invoice.ToResponse();
    }

    public async Task<InvoiceResponse> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var userId = currentUser.CurrentUserId;
        var today = DateOnly.FromDateTime(DateTime.Today);

        var invoice = await db.Invoices
            .Include(i => i.LineItems.OrderBy(li => li.Position))
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        if (invoice.Type == InvoiceType.Cancellation)
            throw new ConflictException("A cancellation invoice cannot be cancelled.");

        if (invoice.Status != InvoiceStatus.Finalized)
            throw new ConflictException(invoice.Status == InvoiceStatus.Paid
                ? "Paid invoices must be set back to Finalized before cancelling."
                : "Only finalized invoices can be cancelled.");

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null || user.DeletedAt is not null) // anonymized account (ADR 0005) = gone
            throw new UnauthorizedException("User not found.");

        var storno = new Invoice
        {
            UserId = userId,
            Type = InvoiceType.Cancellation,
            Status = InvoiceStatus.Finalized,
            SenderName = invoice.SenderName,
            SenderAddress = invoice.SenderAddress,
            RecipientName = invoice.RecipientName,
            RecipientAddress = invoice.RecipientAddress,
            // Storno inherits the original's structured buyer data so its XML validates.
            RecipientStreet = invoice.RecipientStreet,
            RecipientPostalCode = invoice.RecipientPostalCode,
            RecipientCity = invoice.RecipientCity,
            RecipientCountryCode = invoice.RecipientCountryCode,
            RecipientEmail = invoice.RecipientEmail,
            RecipientVatId = invoice.RecipientVatId,
            BuyerReference = invoice.BuyerReference,
            IssueDate = today,
            DueDate = today,
            ServiceDate = invoice.ServiceDate,
            ServicePeriodStart = invoice.ServicePeriodStart,
            ServicePeriodEnd = invoice.ServicePeriodEnd,
            TaxRate = invoice.TaxRate,
            IsSmallBusiness = invoice.IsSmallBusiness,
            Currency = invoice.Currency,
            CancellationOfId = invoice.Id,
            CancellationOfNumber = invoice.Number,
            LineItems = invoice.LineItems.Select(li => new LineItem
            {
                Description = li.Description,
                Quantity = -li.Quantity,
                UnitPrice = li.UnitPrice,
                Unit = li.Unit,
                Position = li.Position,
                DisplayMode = li.DisplayMode
            }).ToList(),
        };
        storno.TotalAmount = storno.Total;

        invoice.Status = InvoiceStatus.Cancelled;
        invoice.PaidAt = null;
        invoice.UpdatedAt = DateTimeOffset.UtcNow;

        db.Invoices.Add(storno);
        await AssignNumberAndSaveAsync(storno, user, ct);
        return storno.ToResponse();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var userId = currentUser.CurrentUserId;

        var invoice = await db.Invoices
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct)
            ?? throw new NotFoundException($"Invoice {id} not found.");

        if (invoice.Status != InvoiceStatus.Draft)
            throw new ConflictException("Only draft invoices can be deleted.");

        // A reopened draft still owns its issued number. Deleting it would tear a
        // gap into the sequence (ADR 0002: numbers are never reused) — the only way
        // forward is re-finalizing (and cancelling via Storno if needed).
        if (invoice.Number is not null)
            throw new ConflictException(
                "This draft was finalized before and keeps its invoice number — it cannot be deleted. Re-finalize it (and cancel via Storno if needed).");

        db.Invoices.Remove(invoice);
        await db.SaveChangesAsync(ct);
    }

    // ---

    private static void ValidateServiceDates(CreateInvoiceRequest req)
    {
        if (req.ServiceDate.HasValue && (req.ServicePeriodStart.HasValue || req.ServicePeriodEnd.HasValue))
            throw new ValidationException("Provide either a service date or a service period, not both.");
        if (req.ServicePeriodStart.HasValue != req.ServicePeriodEnd.HasValue)
            throw new ValidationException("A service period needs both a start and an end date.");
        if (req.ServicePeriodStart.HasValue && req.ServicePeriodEnd < req.ServicePeriodStart)
            throw new ValidationException("The service period end must not be before its start.");
    }

    private static void EnsureSenderProfileComplete(User user)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(user.TaxNumber) && string.IsNullOrWhiteSpace(user.VatId))
            missing.Add("tax number or VAT ID");
        if (string.IsNullOrWhiteSpace(user.Street)) missing.Add("street");
        if (string.IsNullOrWhiteSpace(user.PostalCode)) missing.Add("postal code");
        if (string.IsNullOrWhiteSpace(user.City)) missing.Add("city");
        if (string.IsNullOrWhiteSpace(user.Country)) missing.Add("country");
        // BT-42 seller contact telephone — mandatory for the E-Rechnung (BR-DE-2..7).
        if (string.IsNullOrWhiteSpace(user.Phone)) missing.Add("phone");

        if (missing.Count > 0)
            throw new ConflictException(
                $"Sender profile incomplete — missing: {string.Join(", ", missing)}. Complete your tax settings first.");
    }

    // Structured buyer data required for a valid E-Rechnung (XRechnung): buyer
    // electronic address BT-49 and postal address BR-DE-8/9. Enforced at the legal
    // fixation point so every finalized invoice can produce compliant XML.
    private static void EnsureRecipientComplete(Invoice invoice)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(invoice.RecipientEmail)) missing.Add("recipient email");
        if (string.IsNullOrWhiteSpace(invoice.RecipientStreet)) missing.Add("recipient street");
        if (string.IsNullOrWhiteSpace(invoice.RecipientPostalCode)) missing.Add("recipient postal code");
        if (string.IsNullOrWhiteSpace(invoice.RecipientCity)) missing.Add("recipient city");

        if (missing.Count > 0)
            throw new ConflictException(
                $"Recipient data incomplete for E-Rechnung — missing: {string.Join(", ", missing)}. Add the structured recipient address and email.");
    }

    // The persisted free-text RecipientAddress column (used by the PDF and legacy
    // invoices) is composed from the structured fields when they're supplied,
    // falling back to whatever free text the request carried.
    private static string ComposeRecipientAddress(CreateInvoiceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.RecipientStreet)
            && string.IsNullOrWhiteSpace(req.RecipientPostalCode)
            && string.IsNullOrWhiteSpace(req.RecipientCity))
            return req.RecipientAddress ?? "";

        var cityLine = $"{req.RecipientPostalCode} {req.RecipientCity}".Trim();
        var lines = new[] { req.RecipientStreet, cityLine, req.RecipientCountryCode }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join("\n", lines);
    }

    // Assigns the next sequential number to a Finalized invoice, renders and stores
    // its PDF, and saves everything in one SaveChanges (one DB transaction). Retries
    // on sequence-counter races and unique-index violations.
    // A re-finalized invoice (reopened via ReopenAsync) already owns its number:
    // it is reused verbatim, the sequence stays untouched, and no retry is needed —
    // the unique (UserId, Number) slot is already ours.
    private async Task AssignNumberAndSaveAsync(Invoice invoice, User user, CancellationToken ct)
    {
        var year = invoice.IssueDate.Year;
        var keepExistingNumber = invoice.Number is not null;

        // Usually null (reopen discards the archive), but overwrite defensively —
        // the archived PDF/XML must always show the finalized state being saved here.
        var archived = await db.InvoicePdfs.FindAsync([invoice.Id], ct);
        var archivedXml = await db.InvoiceXmls.FindAsync([invoice.Id], ct);

        for (var attempt = 1; ; attempt++)
        {
            if (!keepExistingNumber)
                invoice.Number = await ReserveNumberAsync(invoice.UserId, year, ct);

            var bytes = pdf.Generate(invoice, user);
            if (archived is null)
            {
                archived = new InvoicePdf { InvoiceId = invoice.Id, Data = bytes };
                db.InvoicePdfs.Add(archived);
            }
            else
            {
                archived.Data = bytes; // retry or re-finalize: replace with current render
                archived.CreatedAt = DateTimeOffset.UtcNow;
            }

            // Archive the legally binding XRechnung XML alongside the PDF — same
            // upsert + retry semantics; the invoice number is embedded in both.
            var xmlBytes = einvoice.Generate(invoice, user);
            if (archivedXml is null)
            {
                archivedXml = new InvoiceXml { InvoiceId = invoice.Id, Data = xmlBytes };
                db.InvoiceXmls.Add(archivedXml);
            }
            else
            {
                archivedXml.Data = xmlBytes;
                archivedXml.CreatedAt = DateTimeOffset.UtcNow;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (!keepExistingNumber && attempt < MaxNumberingAttempts)
            {
                // Lost the race (concurrency token or unique index). Drop our stale
                // view of the sequence row and try again with the next number.
                var seqEntry = db.ChangeTracker.Entries<InvoiceNumberSequence>()
                    .FirstOrDefault(e => e.Entity.UserId == invoice.UserId && e.Entity.Year == year);
                if (seqEntry is not null)
                    seqEntry.State = EntityState.Detached;
            }
        }
    }

    private async Task<string> ReserveNumberAsync(Guid userId, int year, CancellationToken ct)
    {
        var seq = await db.InvoiceNumberSequences.FindAsync([userId, year], ct);
        if (seq is null)
        {
            seq = new InvoiceNumberSequence { UserId = userId, Year = year, Counter = 0 };
            db.InvoiceNumberSequences.Add(seq);
        }

        seq.Counter++;
        return $"{year}-{seq.Counter:D3}";
    }
}

internal static class InvoiceMappings
{
    public static InvoiceResponse ToResponse(this Invoice i, string? cancelledByNumber = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new(
            i.Id,
            i.Number,
            i.Status,
            i.Type,
            i.Status == InvoiceStatus.Finalized && i.Type == InvoiceType.Invoice && i.DueDate < today,
            i.SenderName,
            i.SenderAddress,
            i.RecipientName,
            i.RecipientAddress,
            i.RecipientStreet,
            i.RecipientPostalCode,
            i.RecipientCity,
            i.RecipientCountryCode,
            i.RecipientEmail,
            i.RecipientVatId,
            i.BuyerReference,
            i.IssueDate,
            i.DueDate,
            i.ServiceDate,
            i.ServicePeriodStart,
            i.ServicePeriodEnd,
            i.PaidAt,
            i.Currency,
            i.TaxRate,
            i.IsSmallBusiness,
            i.Subtotal,
            i.TaxAmount,
            i.Total,
            i.LineItems.OrderBy(li => li.Position).Select(li => new LineItemResponse(
                li.Id, li.Description, li.Quantity, li.Unit, li.UnitPrice, li.Total, li.DisplayMode
            )).ToList(),
            i.Notes,
            i.CancellationOfId,
            i.CancellationOfNumber,
            cancelledByNumber,
            i.CreatedAt,
            i.UpdatedAt
        );
    }
}
