using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using InvoiceApi.Models.Dtos.Stats;
using InvoiceApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    IStatsService stats,
    AppDbContext db,
    ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>Dashboard statistics for the authenticated user.</summary>
    /// <remarks>
    /// Defaults: from = today − 1 year, to = today. Capped to a maximum 5-year window.
    /// Outstanding = Sent + Overdue (by IssueDate). Paid is filtered by PaidAt. Draft by CreatedAt.
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
    {
        try
        {
            return Ok(await invoices.GetAsync(id, ct));
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>List invoices with optional status filter and pagination.</summary>
    [HttpGet]
    [ProducesResponseType<List<InvoiceResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] InvoiceStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        return Ok(await invoices.ListAsync(status, page, pageSize, ct));
    }

    /// <summary>Update the status of an invoice (e.g. mark as Paid).</summary>
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType<InvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await invoices.UpdateStatusAsync(id, request.Status, ct));
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Download the invoice as a PDF.</summary>
    [HttpGet("{id:guid}/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadPdf(Guid id, CancellationToken ct)
    {
        var userId = currentUser.CurrentUserId;

        var invoice = await db.Invoices
            .Include(i => i.LineItems)
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);

        if (invoice is null)
            return NotFound(new { error = $"Invoice {id} not found." });

        var bytes = pdf.Generate(invoice);
        return File(bytes, "application/pdf", $"{invoice.Number}.pdf");
    }

    /// <summary>Delete a draft invoice.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await invoices.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (NotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
