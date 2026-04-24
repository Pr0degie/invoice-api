using FluentAssertions;
using InvoiceApi.Data;
using InvoiceApi.Models;
using InvoiceApi.Services;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Tests;

public class StatsServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly StatsService _sut;
    private readonly Guid _userId = Guid.NewGuid();

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly Jan1 = new(Today.Year, 1, 1);
    private static readonly DateOnly Dec31 = new(Today.Year, 12, 31);

    public StatsServiceTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(opts);
        _sut = new StatsService(_db);
    }

    // ── Empty state ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_EmptyUser_ReturnsAllZeros()
    {
        var stats = await _sut.GetStatsAsync(_userId, Jan1, Dec31);

        stats.TotalOutstanding.Should().Be(0);
        stats.TotalPaid.Should().Be(0);
        stats.TotalDraft.Should().Be(0);
        stats.OverdueCount.Should().Be(0);
        stats.DraftCount.Should().Be(0);
        stats.SentCount.Should().Be(0);
        stats.PaidCount.Should().Be(0);
        stats.TopRecipients.Should().BeEmpty();
        stats.MonthlyRevenue.Should().HaveCount(12);
        stats.MonthlyRevenue.Should().AllSatisfy(m => { m.Paid.Should().Be(0); m.Sent.Should().Be(0); });
    }

    // ── Per-status counts and sums ───────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_OneInvoicePerStatus_CountsAndSumsAreCorrect()
    {
        var issue = Jan1.AddMonths(1);
        var paid = Jan1.AddMonths(2);

        Add(InvoiceStatus.Draft,   100m, issue, createdAt: new DateTimeOffset(issue.Year, issue.Month, issue.Day, 0, 0, 0, TimeSpan.Zero));
        Add(InvoiceStatus.Sent,    200m, issue);
        Add(InvoiceStatus.Overdue, 300m, issue);
        Add(InvoiceStatus.Paid,    400m, issue, paidAt: paid);
        await _db.SaveChangesAsync();

        var stats = await _sut.GetStatsAsync(_userId, Jan1, Dec31);

        stats.TotalOutstanding.Should().Be(500m); // Sent + Overdue
        stats.TotalPaid.Should().Be(400m);
        stats.TotalDraft.Should().Be(100m);
        stats.SentCount.Should().Be(1);
        stats.OverdueCount.Should().Be(1);
        stats.PaidCount.Should().Be(1);
        stats.DraftCount.Should().Be(1);
    }

    // ── User isolation ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_OtherUsersInvoices_AreNotCounted()
    {
        var otherUser = Guid.NewGuid();
        var issue = Jan1.AddMonths(1);
        var paid = Jan1.AddMonths(2);

        // Other user's invoices — must not appear in _userId's stats
        _db.Invoices.Add(MakeInvoice(otherUser, InvoiceStatus.Sent, 999m, issue));
        _db.Invoices.Add(MakeInvoice(otherUser, InvoiceStatus.Paid, 888m, issue, paidAt: paid));
        // One invoice for the correct user
        Add(InvoiceStatus.Sent, 100m, issue);
        await _db.SaveChangesAsync();

        var stats = await _sut.GetStatsAsync(_userId, Jan1, Dec31);

        stats.TotalOutstanding.Should().Be(100m);
        stats.TotalPaid.Should().Be(0m);
        stats.SentCount.Should().Be(1);
        stats.PaidCount.Should().Be(0);
    }

    // ── Date-range filter ────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_InvoicesOutsideDateRange_AreExcluded()
    {
        var insideIssue = Jan1.AddMonths(6);
        var outsideIssue = Jan1.AddYears(-1); // previous year — outside range
        var insidePaid = Jan1.AddMonths(6);
        var outsidePaid = Jan1.AddYears(-1);

        Add(InvoiceStatus.Sent,  200m, insideIssue);
        Add(InvoiceStatus.Sent,  999m, outsideIssue); // excluded
        Add(InvoiceStatus.Paid,  300m, insideIssue, paidAt: insidePaid);
        Add(InvoiceStatus.Paid,  888m, outsideIssue, paidAt: outsidePaid); // excluded
        await _db.SaveChangesAsync();

        var stats = await _sut.GetStatsAsync(_userId, Jan1, Dec31);

        stats.TotalOutstanding.Should().Be(200m);
        stats.TotalPaid.Should().Be(300m);
        stats.SentCount.Should().Be(1);
        stats.PaidCount.Should().Be(1);
    }

    // ── Monthly revenue ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_MonthlyRevenue_Always12Entries()
    {
        var stats = await _sut.GetStatsAsync(_userId, Jan1, Dec31);

        stats.MonthlyRevenue.Should().HaveCount(12);
    }

    [Fact]
    public async Task GetStatsAsync_MonthlyRevenue_ThreeDistinctMonths_Correct()
    {
        var year = Today.Year;
        var m1 = new DateOnly(year, 1, 15);
        var m2 = new DateOnly(year, 3, 10);
        var m3 = new DateOnly(year, 6, 1);

        Add(InvoiceStatus.Paid, 100m, m1, paidAt: m1);
        Add(InvoiceStatus.Paid, 200m, m2, paidAt: m2);
        Add(InvoiceStatus.Paid, 300m, m3, paidAt: m3);
        await _db.SaveChangesAsync();

        var stats = await _sut.GetStatsAsync(_userId, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

        var months = stats.MonthlyRevenue.ToDictionary(m => m.Month);
        months[$"{year:D4}-01"].Paid.Should().Be(100m);
        months[$"{year:D4}-03"].Paid.Should().Be(200m);
        months[$"{year:D4}-06"].Paid.Should().Be(300m);

        // months with no data should be 0
        months[$"{year:D4}-02"].Paid.Should().Be(0m);
    }

    [Fact]
    public async Task GetStatsAsync_MonthlyRevenue_SentAmountsGroupedByIssueDate()
    {
        var year = Today.Year;
        var m4 = new DateOnly(year, 4, 5);
        var m7 = new DateOnly(year, 7, 20);

        Add(InvoiceStatus.Sent, 150m, m4);
        Add(InvoiceStatus.Overdue, 250m, m7); // Overdue also counts as "sent" in monthly

        await _db.SaveChangesAsync();

        var stats = await _sut.GetStatsAsync(_userId, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

        var months = stats.MonthlyRevenue.ToDictionary(m => m.Month);
        months[$"{year:D4}-04"].Sent.Should().Be(150m);
        months[$"{year:D4}-07"].Sent.Should().Be(250m);
    }

    // ── Top recipients ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_TopRecipients_MaxFiveReturnedSortedDesc()
    {
        var issue = Jan1.AddMonths(1);
        var paid = Jan1.AddMonths(2);

        for (var i = 1; i <= 10; i++)
            Add(InvoiceStatus.Paid, i * 100m, issue, paidAt: paid, recipient: $"Client {i:D2}");

        await _db.SaveChangesAsync();

        var stats = await _sut.GetStatsAsync(_userId, Jan1, Dec31);

        stats.TopRecipients.Should().HaveCount(5);
        stats.TopRecipients[0].Total.Should().Be(1000m); // Client 10
        stats.TopRecipients[1].Total.Should().Be(900m);  // Client 09
        stats.TopRecipients[4].Total.Should().Be(600m);  // Client 06
    }

    [Fact]
    public async Task GetStatsAsync_TopRecipients_MultipleInvoicesSameRecipient_Aggregated()
    {
        var issue = Jan1.AddMonths(1);
        var paid = Jan1.AddMonths(2);

        Add(InvoiceStatus.Paid, 100m, issue, paidAt: paid, recipient: "Acme");
        Add(InvoiceStatus.Paid, 200m, issue, paidAt: paid, recipient: "Acme");
        Add(InvoiceStatus.Paid,  50m, issue, paidAt: paid, recipient: "Other");
        await _db.SaveChangesAsync();

        var stats = await _sut.GetStatsAsync(_userId, Jan1, Dec31);

        stats.TopRecipients.Should().HaveCount(2);
        var acme = stats.TopRecipients.First(r => r.Name == "Acme");
        acme.Total.Should().Be(300m);
        acme.Count.Should().Be(2);
    }

    // ── Helper methods ───────────────────────────────────────────────────────

    private void Add(
        InvoiceStatus status,
        decimal totalAmount,
        DateOnly issueDate,
        DateOnly? paidAt = null,
        DateTimeOffset? createdAt = null,
        string recipient = "Test Client")
    {
        _db.Invoices.Add(MakeInvoice(_userId, status, totalAmount, issueDate, paidAt, createdAt, recipient));
    }

    private static Invoice MakeInvoice(
        Guid userId,
        InvoiceStatus status,
        decimal totalAmount,
        DateOnly issueDate,
        DateOnly? paidAt = null,
        DateTimeOffset? createdAt = null,
        string recipient = "Test Client") => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Number = $"INV-{Guid.NewGuid():N}",
        SenderName = "Test Sender",
        SenderAddress = "Musterstraße 1",
        RecipientName = recipient,
        RecipientAddress = "Testweg 5",
        IssueDate = issueDate,
        DueDate = issueDate.AddDays(14),
        Status = status,
        TotalAmount = totalAmount,
        PaidAt = paidAt,
        CreatedAt = createdAt ?? new DateTimeOffset(issueDate.Year, issueDate.Month, issueDate.Day, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => _db.Dispose();
}
