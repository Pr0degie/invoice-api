using InvoiceApi.Data;
using InvoiceApi.Models;
using InvoiceApi.Models.Dtos.Stats;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Services;

public interface IStatsService
{
    Task<StatsDto> GetStatsAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public class StatsService(AppDbContext db) : IStatsService
{
    public async Task<StatsDto> GetStatsAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        // DateTimeOffset bounds for the CreatedAt (DateTimeOffset) draft filter
        var fromOffset = new DateTimeOffset(from, TimeOnly.MinValue, TimeSpan.Zero);
        var toExclusive = new DateTimeOffset(to.AddDays(1), TimeOnly.MinValue, TimeSpan.Zero);

        // Q1 — Outstanding: Sent + Overdue, filtered by IssueDate
        var outstandingGroups = await db.Invoices
            .Where(i => i.UserId == userId
                && (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Overdue)
                && i.IssueDate >= from && i.IssueDate <= to)
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Sum = g.Sum(i => i.TotalAmount), Count = g.Count() })
            .ToListAsync(ct);

        var totalOutstanding = outstandingGroups.Sum(g => g.Sum);
        var sentCount = outstandingGroups.FirstOrDefault(g => g.Status == InvoiceStatus.Sent)?.Count ?? 0;
        var overdueCount = outstandingGroups.FirstOrDefault(g => g.Status == InvoiceStatus.Overdue)?.Count ?? 0;

        // Q2 — Paid: filtered by PaidAt; load TotalAmount + RecipientName for topRecipients in memory
        var paidItems = await db.Invoices
            .Where(i => i.UserId == userId
                && i.Status == InvoiceStatus.Paid
                && i.PaidAt.HasValue && i.PaidAt >= from && i.PaidAt <= to)
            .Select(i => new { i.TotalAmount, i.RecipientName })
            .ToListAsync(ct);

        var totalPaid = paidItems.Sum(i => i.TotalAmount);
        var paidCount = paidItems.Count;

        var topRecipients = paidItems
            .GroupBy(i => i.RecipientName)
            .Select(g => new TopRecipientDto(g.Key, g.Sum(i => i.TotalAmount), g.Count()))
            .OrderByDescending(r => r.Total)
            .Take(5)
            .ToList();

        // Q3 — Draft: filtered by CreatedAt
        var draftAmounts = await db.Invoices
            .Where(i => i.UserId == userId
                && i.Status == InvoiceStatus.Draft
                && i.CreatedAt >= fromOffset && i.CreatedAt < toExclusive)
            .Select(i => i.TotalAmount)
            .ToListAsync(ct);

        var totalDraft = draftAmounts.Sum();
        var draftCount = draftAmounts.Count;

        // Q4 — Monthly revenue: 12 months backwards from 'to'
        var monthWindowStart = new DateOnly(to.Year, to.Month, 1).AddMonths(-11);

        var paidByMonth = await db.Invoices
            .Where(i => i.UserId == userId
                && i.Status == InvoiceStatus.Paid
                && i.PaidAt.HasValue && i.PaidAt >= monthWindowStart && i.PaidAt <= to)
            .GroupBy(i => new { i.PaidAt!.Value.Year, i.PaidAt!.Value.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Total = g.Sum(i => i.TotalAmount) })
            .ToListAsync(ct);

        var sentByMonth = await db.Invoices
            .Where(i => i.UserId == userId
                && (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Overdue)
                && i.IssueDate >= monthWindowStart && i.IssueDate <= to)
            .GroupBy(i => new { i.IssueDate.Year, i.IssueDate.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Total = g.Sum(i => i.TotalAmount) })
            .ToListAsync(ct);

        var monthlyRevenue = Enumerable.Range(0, 12)
            .Select(offset => monthWindowStart.AddMonths(offset))
            .Select(m => new MonthlyRevenueDto(
                $"{m.Year:D4}-{m.Month:D2}",
                paidByMonth.FirstOrDefault(p => p.Year == m.Year && p.Month == m.Month)?.Total ?? 0m,
                sentByMonth.FirstOrDefault(s => s.Year == m.Year && s.Month == m.Month)?.Total ?? 0m))
            .ToList();

        return new StatsDto(
            totalOutstanding, totalPaid, totalDraft,
            overdueCount, draftCount, sentCount, paidCount,
            monthlyRevenue, topRecipients);
    }
}
