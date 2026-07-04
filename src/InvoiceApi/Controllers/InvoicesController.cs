using InvoiceApi.Data;
using InvoiceApi.Models;
using InvoiceApi.Models.Dtos.Stats;
using InvoiceApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Controllers;

[ApiController]
[Route("api/invoices")]
[Produces("application/json")]
[Authorize]
[EnableRateLimiting("api-user")]
public class InvoicesController(
    IInvoiceService invoices,
    IPdfService pdf,
    IEInvoiceService einvoice,
    IStatsService stats,
    AppDbContext db,
    ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>Dashboard statistics for the authenticated user.</summary>
    /// <remarks>
    /// Defaults: from = today − 1 year, to = today. Capped to a maximum 5-year window.
    /// Outstanding = Finalized (by IssueDate); overdue = subset past its due date.
    /// Paid is filtered by PaidAt. Draft by CreatedAt. Cancellation invoices excluded.
    /// </remarks>
    [HttpGet("stats")]
    [ProducesResponseType<StatsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetStats(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var toDate = DateOnly.FromDateTime((to ?? DateTime.UtcNow).ToUniversalTime());
        var fromDate = DateOnly.FromDateTime((from ?? DateTime.UtcNow.AddYears(-1)).ToUniversalTime());

        if (fromDate > toDate)
            return BadRequest(new { error = "'from' must be before or equal to 'to'." });

        // Cap to 5 years to prevent runaway scans
        var floor = toDate.AddYears(-5);
        if (fromDate < floor) fromDate = floor;

        var result = await stats.GetStatsAsync(currentUser.CurrentUserId, fromDate, toDate, ct);
        return Ok(result);
    }

    /// <summary>Create a new invoice.</summary>
    [HttpPost]
    [ProducesResponseType<InvoiceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateInvoiceRequest request, CancellationToken ct)
    {
        var result = await invoices.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    /// <summary>Get a single invoice by ID.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<InvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => Ok(await invoices.GetAsync(id, ct));

    /// <summary>List invoices with optional status filter and pagination.</summary>
    /// <remarks>
    /// status: Draft | Finalized | Paid | Cancelled | Overdue. "Overdue" is a virtual
    /// filter (Finalized invoices past their due date) — it is not a stored status.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<List<InvoiceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);

        InvoiceStatus? statusFilter = null;
        var overdueOnly = false;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status.Equals("Overdue", StringComparison.OrdinalIgnoreCase))
                overdueOnly = true;
            else if (Enum.TryParse<InvoiceStatus>(status, ignoreCase: true, out var parsed)
                     && Enum.IsDefined(parsed))
                statusFilter = parsed;
            else
                return BadRequest(new { error = $"Unknown status '{status}'." });
        }

        return Ok(await invoices.ListAsync(statusFilter, overdueOnly, page, pageSize, search, ct));
    }

    /// <summary>Finalize a draft: assign its sequential number, freeze it, archive the PDF.</summary>
    /// <remarks>
    /// Requires a complete sender tax profile (address + Steuernummer or USt-IdNr.)
    /// and a service date/period on the invoice — 409 otherwise. Afterwards the
    /// invoice is immutable; corrections go through Storno + new invoice.
    /// The optional body sets the Ausstellungsdatum (default: today; future dates
    /// → 400). The due date shifts to keep the draft's payment-term span.
    /// </remarks>
    [HttpPost("{id:guid}/finalize")]
    [ProducesResponseType<InvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Finalize(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] FinalizeInvoiceRequest? request,
        CancellationToken ct)
        => Ok(await invoices.FinalizeAsync(id, request?.IssueDate, ct));

    /// <summary>Reopen a finalized invoice: reset it to Draft for corrections before dispatch.</summary>
    /// <remarks>
    /// Deliberate, audited exception to invoice immutability (ADR 0003) — intended
    /// ONLY for invoices that have not been sent to the recipient yet. The invoice
    /// keeps its assigned number (re-finalizing reuses it; the number sequence is
    /// untouched), the archived PDF is discarded and re-archived at re-finalization,
    /// and an append-only audit entry records the reopening (GoBD). Already-sent
    /// invoices are corrected via Storno instead. 400 if the invoice is a draft;
    /// 409 for Paid, Cancelled and Cancellation invoices.
    /// </remarks>
    [HttpPost("{id:guid}/reopen")]
    [ProducesResponseType<InvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken ct)
        => Ok(await invoices.ReopenAsync(id, ct));

    /// <summary>Cancel a finalized invoice by issuing a Stornorechnung.</summary>
    /// <remarks>
    /// Creates a Cancellation invoice (own sequential number, negative amounts,
    /// reference to the original) and sets the original to Cancelled. Returns the
    /// Stornorechnung.
    /// </remarks>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<InvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
        => Ok(await invoices.CancelAsync(id, ct));

    /// <summary>Update a draft invoice.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<InvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateInvoiceRequest request, CancellationToken ct)
        => Ok(await invoices.UpdateAsync(id, request, ct));

    /// <summary>Update the status of an invoice (e.g. mark as Paid).</summary>
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType<InvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusRequest request, CancellationToken ct)
        => Ok(await invoices.UpdateStatusAsync(id, request.Status, ct));

    /// <summary>Download the invoice as a PDF.</summary>
    /// <remarks>
    /// Finalized invoices return the PDF archived at finalization (GoBD — never
    /// re-rendered from live data). Drafts return a live preview with an ENTWURF
    /// watermark.
    /// </remarks>
    [HttpGet("{id:guid}/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadPdf(Guid id, CancellationToken ct)
    {
        var userId = currentUser.CurrentUserId;

        var invoice = await db.Invoices
            .Include(i => i.LineItems.OrderBy(li => li.Position))
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);

        if (invoice is null)
            return NotFound(new { error = $"Invoice {id} not found." });

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
            return NotFound(new { error = "User not found." });

        if (invoice.Status == InvoiceStatus.Draft)
            return File(pdf.Generate(invoice, user), "application/pdf", "Entwurf.pdf");

        var archived = await db.InvoicePdfs.FindAsync([invoice.Id], ct);
        if (archived is null)
        {
            // Backfill for invoices finalized before PDF archiving existed: render
            // once from current data and persist that render as the archive.
            archived = new InvoicePdf { InvoiceId = invoice.Id, Data = pdf.Generate(invoice, user) };
            db.InvoicePdfs.Add(archived);
            await db.SaveChangesAsync(ct);
        }

        return File(archived.Data, "application/pdf", $"{invoice.Number}.pdf");
    }

    /// <summary>Download the invoice as an E-Rechnung (XRechnung / EN 16931 CII XML).</summary>
    /// <remarks>
    /// Returns the XML archived at finalization (GoBD — the structured document is
    /// preserved unaltered). Drafts have no legal E-Rechnung (409). Invoices finalized
    /// before E-Rechnung support are backfilled on demand, but only when they carry the
    /// structured recipient data + email and a seller phone; otherwise 409 names what's missing.
    /// </remarks>
    [HttpGet("{id:guid}/xml")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DownloadXml(Guid id, CancellationToken ct)
    {
        var userId = currentUser.CurrentUserId;

        var invoice = await db.Invoices
            .Include(i => i.LineItems.OrderBy(li => li.Position))
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);

        if (invoice is null)
            return NotFound(new { error = $"Invoice {id} not found." });

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null)
            return NotFound(new { error = "User not found." });

        if (invoice.Status == InvoiceStatus.Draft)
            return Conflict(new { error = "Drafts have no E-Rechnung — finalize the invoice first." });

        var archived = await db.InvoiceXmls.FindAsync([invoice.Id], ct);
        if (archived is null)
        {
            // Backfill for invoices finalized before E-Rechnung support existed —
            // only possible when the structured data required for a valid XRechnung
            // is present. Otherwise report exactly what the legacy invoice is missing.
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(invoice.RecipientEmail)) missing.Add("recipient email");
            if (string.IsNullOrWhiteSpace(invoice.RecipientStreet)) missing.Add("recipient street");
            if (string.IsNullOrWhiteSpace(invoice.RecipientPostalCode)) missing.Add("recipient postal code");
            if (string.IsNullOrWhiteSpace(invoice.RecipientCity)) missing.Add("recipient city");
            if (string.IsNullOrWhiteSpace(user.Phone)) missing.Add("seller phone");

            if (missing.Count > 0)
                return Conflict(new
                {
                    error = $"This invoice predates E-Rechnung support and cannot produce a valid XRechnung — missing: {string.Join(", ", missing)}."
                });

            archived = new InvoiceXml { InvoiceId = invoice.Id, Data = einvoice.Generate(invoice, user) };
            db.InvoiceXmls.Add(archived);
            await db.SaveChangesAsync(ct);
        }

        return File(archived.Data, "application/xml", $"{invoice.Number}.xml");
    }

    /// <summary>Delete a draft invoice.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await invoices.DeleteAsync(id, ct);
        return NoContent();
    }
}
