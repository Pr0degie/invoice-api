using FluentAssertions;
using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using InvoiceApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InvoiceApi.Tests;

public class InvoiceServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly InvoiceService _sut;
    private readonly Guid _userId = Guid.NewGuid();

    public InvoiceServiceTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(opts);
        _sut = new InvoiceService(_db, new FakeCurrentUserService(_userId), new FakePdfService(), new FakeEInvoiceService());
    }

    // ── Create ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ShouldPersistDraftWithoutNumber()
    {
        var result = await _sut.CreateAsync(BuildRequest());

        result.Should().NotBeNull();
        result.Status.Should().Be(InvoiceStatus.Draft);
        result.Number.Should().BeNull();
        result.LineItems.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateAsync_ShouldCalculateTotalsCorrectly()
    {
        // 2h à 80 + 1 flat à 50 = 210 net, 19% = 39.90, total = 249.90
        var result = await _sut.CreateAsync(BuildRequest());

        result.Subtotal.Should().Be(210m);
        result.TaxAmount.Should().Be(39.90m);
        result.Total.Should().Be(249.90m);
    }

    [Fact]
    public async Task CreateAsync_ShouldAllowMultipleDrafts_AllWithoutNumbers()
    {
        await _sut.CreateAsync(BuildRequest());
        await _sut.CreateAsync(BuildRequest());
        var third = await _sut.CreateAsync(BuildRequest());

        third.Number.Should().BeNull();
        (await _sut.ListAsync(InvoiceStatus.Draft, false, 1, 10)).Should().HaveCount(3);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectServiceDateAndPeriodTogether()
    {
        var act = () => _sut.CreateAsync(BuildRequest() with
        {
            ServiceDate = Today,
            ServicePeriodStart = Today.AddDays(-10),
            ServicePeriodEnd = Today,
        });

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectHalfOpenServicePeriod()
    {
        var act = () => _sut.CreateAsync(BuildRequest() with
        {
            ServiceDate = null,
            ServicePeriodStart = Today.AddDays(-10),
            ServicePeriodEnd = null,
        });

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectServicePeriodEndBeforeStart()
    {
        var act = () => _sut.CreateAsync(BuildRequest() with
        {
            ServiceDate = null,
            ServicePeriodStart = Today,
            ServicePeriodEnd = Today.AddDays(-1),
        });

        await act.Should().ThrowAsync<ValidationException>();
    }

    // ── Get / list ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ShouldReturnInvoice_WhenExists()
    {
        var created = await _sut.CreateAsync(BuildRequest());

        var fetched = await _sut.GetAsync(created.Id);

        fetched.Id.Should().Be(created.Id);
        fetched.RecipientName.Should().Be("ACME GmbH");
    }

    [Fact]
    public async Task GetAsync_ShouldThrowNotFoundException_WhenNotExists()
    {
        var act = () => _sut.GetAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByStatus()
    {
        await SeedCompleteUser();
        var inv = await _sut.CreateAsync(BuildRequest());
        await _sut.FinalizeAsync(inv.Id);
        await _sut.UpdateStatusAsync(inv.Id, InvoiceStatus.Paid);
        await _sut.CreateAsync(BuildRequest()); // Draft

        var paid = await _sut.ListAsync(InvoiceStatus.Paid, false, 1, 10);
        var drafts = await _sut.ListAsync(InvoiceStatus.Draft, false, 1, 10);

        paid.Should().HaveCount(1);
        drafts.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListAsync_OverdueFilter_ReturnsOnlyFinalizedPastDueDate()
    {
        await SeedCompleteUser();

        // Finalized with a back-dated Ausstellungsdatum so the shifted DueDate is past
        var overdue = await _sut.CreateAsync(BuildRequest() with
        {
            IssueDate = Today.AddDays(-15),
            DueDate = Today.AddDays(-1), // 14-day span
        });
        await _sut.FinalizeAsync(overdue.Id, issueDate: Today.AddDays(-15));

        var current = await _sut.CreateAsync(BuildRequest() with { DueDate = Today.AddDays(30) });
        await _sut.FinalizeAsync(current.Id);

        await _sut.CreateAsync(BuildRequest() with { DueDate = Today.AddDays(-1) }); // Draft, past due — not overdue

        var result = await _sut.ListAsync(null, true, 1, 10);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be(overdue.Id);
        result[0].IsOverdue.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_ShouldNotReturnOtherUsersInvoice()
    {
        var invoiceA = await _sut.CreateAsync(BuildRequest());

        var userBService = ServiceFor(Guid.NewGuid());
        var act = () => userBService.GetAsync(invoiceA.Id);

        // 404 (not 403 — don't leak existence)
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ListAsync_ShouldIsolateInvoicesPerUser()
    {
        await _sut.CreateAsync(BuildRequest());
        await _sut.CreateAsync(BuildRequest());

        var userBService = ServiceFor(Guid.NewGuid());
        await userBService.CreateAsync(BuildRequest());

        var userAInvoices = await _sut.ListAsync(null, false, 1, 100);
        var userBInvoices = await userBService.ListAsync(null, false, 1, 100);

        userAInvoices.Should().HaveCount(2);
        userBInvoices.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListAsync_ShouldFilterBySearch_OnRecipientName()
    {
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "Müller GmbH" });
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "Müller Consulting GmbH" });
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "ACME Corp" });

        var result = await _sut.ListAsync(null, false, 1, 100, search: "Müller");

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(i => i.RecipientName.Should().Contain("Müller"));
    }

    [Fact]
    public async Task ListAsync_ShouldFilterBySearch_OnInvoiceNumber()
    {
        await SeedCompleteUser();
        foreach (var _ in Enumerable.Range(0, 3))
        {
            var inv = await _sut.CreateAsync(BuildRequest());
            await _sut.FinalizeAsync(inv.Id);
        }

        var year = Today.Year;
        var result = await _sut.ListAsync(null, false, 1, 100, search: $"{year}-002");

        result.Should().HaveCount(1);
        result[0].Number.Should().Be($"{year}-002");
    }

    [Fact]
    public async Task ListAsync_SearchShouldBeCaseInsensitive()
    {
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "Müller GmbH" });
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "ACME Corp" });

        var result = await _sut.ListAsync(null, false, 1, 100, search: "müller");

        result.Should().HaveCount(1);
        result[0].RecipientName.Should().Be("Müller GmbH");
    }

    // ── Finalize: numbering ─────────────────────────────────────────────────

    [Fact]
    public async Task FinalizeAsync_ShouldAssignSequentialNumbers()
    {
        await SeedCompleteUser();
        var year = Today.Year;

        var first = await _sut.CreateAsync(BuildRequest());
        var second = await _sut.CreateAsync(BuildRequest());

        (await _sut.FinalizeAsync(first.Id)).Number.Should().Be($"{year}-001");
        (await _sut.FinalizeAsync(second.Id)).Number.Should().Be($"{year}-002");
    }

    [Fact]
    public async Task FinalizeAsync_ShouldResetCounterPerYear()
    {
        await SeedCompleteUser();
        var year = LocalToday.Year;

        var lastYear = await _sut.CreateAsync(BuildRequest() with
        {
            IssueDate = new DateOnly(year - 1, 12, 1),
            DueDate = new DateOnly(year - 1, 12, 15),
        });
        var older = await _sut.FinalizeAsync(lastYear.Id, issueDate: new DateOnly(year - 1, 12, 20));

        var thisYear = await _sut.CreateAsync(BuildRequest());
        var finalized = await _sut.FinalizeAsync(thisYear.Id);

        older.Number.Should().Be($"{year - 1}-001");
        finalized.Number.Should().Be($"{year}-001");
    }

    [Fact]
    public async Task FinalizeAsync_ShouldScopeNumbersPerUser()
    {
        await SeedCompleteUser();
        var userB = Guid.NewGuid();
        await SeedCompleteUser(userB);
        var userBService = ServiceFor(userB);
        var year = Today.Year;

        var a = await _sut.CreateAsync(BuildRequest());
        var b = await userBService.CreateAsync(BuildRequest());

        (await _sut.FinalizeAsync(a.Id)).Number.Should().Be($"{year}-001");
        (await userBService.FinalizeAsync(b.Id)).Number.Should().Be($"{year}-001");
    }

    [Fact]
    public async Task FinalizeAsync_ConcurrentFinalizations_ProduceUniqueSequentialNumbers()
    {
        // Shared store + separate contexts = real concurrency on the sequence row.
        var root = new InMemoryDatabaseRoot();
        var dbName = Guid.NewGuid().ToString();
        AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName, root).Options);

        var userId = Guid.NewGuid();
        List<Guid> draftIds;
        await using (var setup = NewContext())
        {
            setup.Users.Add(CompleteUser(userId));
            await setup.SaveChangesAsync();
            var service = new InvoiceService(setup, new FakeCurrentUserService(userId), new FakePdfService(), new FakeEInvoiceService());
            draftIds = new List<Guid>();
            foreach (var _ in Enumerable.Range(0, 5))
                draftIds.Add((await service.CreateAsync(BuildRequest())).Id);
        }

        var results = await Task.WhenAll(draftIds.Select(async id =>
        {
            await using var ctx = NewContext();
            var service = new InvoiceService(ctx, new FakeCurrentUserService(userId), new FakePdfService(), new FakeEInvoiceService());
            return await service.FinalizeAsync(id);
        }));

        var numbers = results.Select(r => r.Number).ToList();
        numbers.Should().OnlyHaveUniqueItems();
        numbers.Should().BeEquivalentTo(
            Enumerable.Range(1, 5).Select(n => $"{Today.Year}-{n:D3}"));
    }

    // ── Finalize: preconditions ─────────────────────────────────────────────

    [Fact]
    public async Task FinalizeAsync_ShouldThrowConflict_WhenSenderProfileIncomplete()
    {
        await SeedCompleteUser(configure: u => { u.TaxNumber = null; u.VatId = null; });
        var draft = await _sut.CreateAsync(BuildRequest());

        var act = () => _sut.FinalizeAsync(draft.Id);

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("tax number");
    }

    [Fact]
    public async Task FinalizeAsync_ShouldThrowConflict_WhenAddressIncomplete()
    {
        await SeedCompleteUser(configure: u => u.City = null);
        var draft = await _sut.CreateAsync(BuildRequest());

        var act = () => _sut.FinalizeAsync(draft.Id);

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("city");
    }

    [Fact]
    public async Task FinalizeAsync_ShouldThrowConflict_WhenServiceDateMissing()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest() with { ServiceDate = null });

        var act = () => _sut.FinalizeAsync(draft.Id);

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("service date");
    }

    [Fact]
    public async Task FinalizeAsync_ShouldThrowConflict_WhenNotDraft()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());
        await _sut.FinalizeAsync(draft.Id);

        var act = () => _sut.FinalizeAsync(draft.Id);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task FinalizeAsync_ShouldArchivePdf()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());

        await _sut.FinalizeAsync(draft.Id);

        var archived = await _db.InvoicePdfs.FindAsync(draft.Id);
        archived.Should().NotBeNull();
        archived!.Data.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FinalizeAsync_ShouldArchiveXml()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());

        await _sut.FinalizeAsync(draft.Id);

        var archived = await _db.InvoiceXmls.FindAsync(draft.Id);
        archived.Should().NotBeNull();
        archived!.Data.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReopenAsync_ShouldDiscardArchivedXml()
    {
        var id = await CreateFinalized();
        (await _db.InvoiceXmls.FindAsync(id)).Should().NotBeNull();

        await _sut.ReopenAsync(id);

        (await _db.InvoiceXmls.FindAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task FinalizeAsync_ShouldThrowConflict_WhenRecipientDataIncomplete()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest() with { RecipientEmail = null });

        var act = () => _sut.FinalizeAsync(draft.Id);

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("recipient email");
    }

    // ── Finalize: Kleinunternehmer vs. Regelbesteuerung ─────────────────────

    [Fact]
    public async Task FinalizeAsync_SmallBusiness_ForcesZeroVat()
    {
        await SeedCompleteUser(configure: u => u.IsSmallBusiness = true);
        var draft = await _sut.CreateAsync(BuildRequest() with { TaxRate = 0.19m });

        var finalized = await _sut.FinalizeAsync(draft.Id);

        finalized.IsSmallBusiness.Should().BeTrue();
        finalized.TaxRate.Should().Be(0m);
        finalized.TaxAmount.Should().Be(0m);
        finalized.Total.Should().Be(finalized.Subtotal);

        var stored = await _db.Invoices.AsNoTracking().FirstAsync(i => i.Id == draft.Id);
        stored.TotalAmount.Should().Be(210m);
    }

    [Fact]
    public async Task FinalizeAsync_RegularTaxation_KeepsVat()
    {
        await SeedCompleteUser(configure: u => u.IsSmallBusiness = false);
        var draft = await _sut.CreateAsync(BuildRequest() with { TaxRate = 0.19m });

        var finalized = await _sut.FinalizeAsync(draft.Id);

        finalized.IsSmallBusiness.Should().BeFalse();
        finalized.TaxRate.Should().Be(0.19m);
        finalized.Subtotal.Should().Be(210m);
        finalized.TaxAmount.Should().Be(39.90m);
        finalized.Total.Should().Be(249.90m);
    }

    // ── Finalize: Ausstellungsdatum ─────────────────────────────────────────

    [Fact]
    public async Task FinalizeAsync_ShouldSetIssueDateToToday_ByDefault()
    {
        await SeedCompleteUser();
        // Stale draft: created two weeks ago, never finalized
        var draft = await _sut.CreateAsync(BuildRequest() with
        {
            IssueDate = LocalToday.AddDays(-14),
            DueDate = LocalToday,
        });

        var finalized = await _sut.FinalizeAsync(draft.Id);

        finalized.IssueDate.Should().Be(LocalToday);
    }

    [Fact]
    public async Task FinalizeAsync_ShouldRespectExplicitIssueDate()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());
        var explicitDate = LocalToday.AddDays(-3);

        var finalized = await _sut.FinalizeAsync(draft.Id, issueDate: explicitDate);

        finalized.IssueDate.Should().Be(explicitDate);
    }

    [Fact]
    public async Task FinalizeAsync_ShouldRejectFutureIssueDate()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());

        var act = () => _sut.FinalizeAsync(draft.Id, issueDate: LocalToday.AddDays(1));

        (await act.Should().ThrowAsync<ValidationException>())
            .Which.Message.Should().Contain("future");
    }

    [Fact]
    public async Task FinalizeAsync_ShouldShiftDueDate_PreservingPaymentTermSpan()
    {
        await SeedCompleteUser();
        // 14-day payment terms on a two-week-old draft: due today if left as-is
        var draft = await _sut.CreateAsync(BuildRequest() with
        {
            IssueDate = LocalToday.AddDays(-14),
            DueDate = LocalToday,
        });

        var finalized = await _sut.FinalizeAsync(draft.Id);

        finalized.IssueDate.Should().Be(LocalToday);
        finalized.DueDate.Should().Be(LocalToday.AddDays(14));
    }

    [Fact]
    public async Task FinalizeAsync_ShouldClampDueDate_WhenDraftDueDateBeforeIssueDate()
    {
        await SeedCompleteUser();
        // DueDate edited to an absolute date before the draft's IssueDate → span clamps to 0
        var draft = await _sut.CreateAsync(BuildRequest() with
        {
            IssueDate = LocalToday.AddDays(-2),
            DueDate = LocalToday.AddDays(-5),
        });

        var finalized = await _sut.FinalizeAsync(draft.Id);

        finalized.IssueDate.Should().Be(LocalToday);
        finalized.DueDate.Should().Be(LocalToday);
    }

    [Fact]
    public async Task FinalizeAsync_NumberYear_ShouldFollowFinalIssueDate()
    {
        await SeedCompleteUser();
        // Draft from December, finalized in January → number belongs to the new year
        var december = new DateOnly(LocalToday.Year - 1, 12, 15);
        var draft = await _sut.CreateAsync(BuildRequest() with
        {
            IssueDate = december,
            DueDate = december.AddDays(14),
        });

        var january = new DateOnly(LocalToday.Year, 1, 1);
        var finalized = await _sut.FinalizeAsync(draft.Id, issueDate: january);

        finalized.Number.Should().Be($"{LocalToday.Year}-001");
        finalized.IssueDate.Should().Be(january);
        finalized.DueDate.Should().Be(january.AddDays(14));
    }

    // ── Status workflow ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateStatusAsync_FinalizedToPaid_SetsPaidAt()
    {
        var id = await CreateFinalized();

        var updated = await _sut.UpdateStatusAsync(id, InvoiceStatus.Paid);

        updated.Status.Should().Be(InvoiceStatus.Paid);
        updated.PaidAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldPreservePaidAt_OnPaidFinalizedPaidRoundTrip()
    {
        var id = await CreateFinalized();
        var paid = await _sut.UpdateStatusAsync(id, InvoiceStatus.Paid);

        var unpaid = await _sut.UpdateStatusAsync(id, InvoiceStatus.Finalized);
        var paidAgain = await _sut.UpdateStatusAsync(id, InvoiceStatus.Paid);

        paid.PaidAt.Should().NotBeNull();
        unpaid.PaidAt.Should().Be(paid.PaidAt);
        paidAgain.PaidAt.Should().Be(paid.PaidAt);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldRejectDraftFinalization_ViaPatch()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());

        var act = () => _sut.UpdateStatusAsync(draft.Id, InvoiceStatus.Finalized);

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("finalize");
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldRejectCancellation_ViaPatch()
    {
        var id = await CreateFinalized();

        var act = () => _sut.UpdateStatusAsync(id, InvoiceStatus.Cancelled);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldRejectDraftToPaid()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());

        var act = () => _sut.UpdateStatusAsync(draft.Id, InvoiceStatus.Paid);

        await act.Should().ThrowAsync<ConflictException>();
    }

    // ── Immutability ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ShouldThrowConflict_WhenFinalized()
    {
        var id = await CreateFinalized();

        var act = () => _sut.UpdateAsync(id, BuildRequest());

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrowConflict_WhenFinalized()
    {
        var id = await CreateFinalized();

        var act = () => _sut.DeleteAsync(id);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveDraft()
    {
        var created = await _sut.CreateAsync(BuildRequest());

        await _sut.DeleteAsync(created.Id);

        var act = () => _sut.GetAsync(created.Id);
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrow_WhenDeletingOtherUsersInvoice()
    {
        var invoice = await _sut.CreateAsync(BuildRequest());

        var userBService = ServiceFor(Guid.NewGuid());
        var act = () => userBService.DeleteAsync(invoice.Id);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── Storno ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_ShouldIssueStornoAndCancelOriginal()
    {
        var id = await CreateFinalized();
        var year = Today.Year;

        var storno = await _sut.CancelAsync(id);

        storno.Type.Should().Be(InvoiceType.Cancellation);
        storno.Status.Should().Be(InvoiceStatus.Finalized);
        storno.Number.Should().Be($"{year}-002");
        storno.CancellationOfNumber.Should().Be($"{year}-001");
        storno.Subtotal.Should().Be(-210m);
        storno.Total.Should().Be(-210m); // small-business demo user → no VAT

        var original = await _sut.GetAsync(id);
        original.Status.Should().Be(InvoiceStatus.Cancelled);
        original.CancelledByNumber.Should().Be(storno.Number);
    }

    [Fact]
    public async Task CancelAsync_ShouldArchiveStornoPdf()
    {
        var id = await CreateFinalized();

        var storno = await _sut.CancelAsync(id);

        (await _db.InvoicePdfs.FindAsync(storno.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task CancelAsync_ShouldNegateVat_ForRegularTaxation()
    {
        await SeedCompleteUser(configure: u => u.IsSmallBusiness = false);
        var draft = await _sut.CreateAsync(BuildRequest() with { TaxRate = 0.19m });
        await _sut.FinalizeAsync(draft.Id);

        var storno = await _sut.CancelAsync(draft.Id);

        storno.Subtotal.Should().Be(-210m);
        storno.TaxAmount.Should().Be(-39.90m);
        storno.Total.Should().Be(-249.90m);
    }

    [Fact]
    public async Task CancelAsync_ShouldThrowConflict_ForDraft()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());

        var act = () => _sut.CancelAsync(draft.Id);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task CancelAsync_ShouldThrowConflict_ForPaid()
    {
        var id = await CreateFinalized();
        await _sut.UpdateStatusAsync(id, InvoiceStatus.Paid);

        var act = () => _sut.CancelAsync(id);

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("Paid");
    }

    [Fact]
    public async Task CancelAsync_ShouldThrowConflict_ForAlreadyCancelled()
    {
        var id = await CreateFinalized();
        await _sut.CancelAsync(id);

        var act = () => _sut.CancelAsync(id);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task CancelAsync_ShouldThrowConflict_ForCancellationInvoice()
    {
        var id = await CreateFinalized();
        var storno = await _sut.CancelAsync(id);

        var act = () => _sut.CancelAsync(storno.Id);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task CancelAsync_ShouldClearPaidAt_OnOriginal()
    {
        var id = await CreateFinalized();
        await _sut.UpdateStatusAsync(id, InvoiceStatus.Paid);
        await _sut.UpdateStatusAsync(id, InvoiceStatus.Finalized); // PaidAt survives

        await _sut.CancelAsync(id);

        var original = await _sut.GetAsync(id);
        original.PaidAt.Should().BeNull();
    }

    // ── Update drafts ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ShouldUpdateDraftInvoice()
    {
        var created = await _sut.CreateAsync(BuildRequest());

        var updated = await _sut.UpdateAsync(created.Id, BuildRequest() with { RecipientName = "New Recipient" });

        updated.RecipientName.Should().Be("New Recipient");
        updated.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReplaceLineItems()
    {
        var created = await _sut.CreateAsync(BuildRequest()); // 2 line items

        var updated = await _sut.UpdateAsync(created.Id, BuildRequest() with
        {
            LineItems = [new() { Description = "Single Item", Quantity = 1, UnitPrice = 100m, Unit = "flat" }]
        });

        updated.LineItems.Should().HaveCount(1);
        updated.LineItems[0].Description.Should().Be("Single Item");

        var dbLineItems = await _db.LineItems.Where(li => li.InvoiceId == created.Id).ToListAsync();
        dbLineItems.Should().HaveCount(1);
    }

    [Fact]
    public async Task UpdateAsync_ShouldRecomputeTotals()
    {
        var created = await _sut.CreateAsync(BuildRequest()); // subtotal 210, tax 39.90, total 249.90

        var updated = await _sut.UpdateAsync(created.Id, BuildRequest() with
        {
            TaxRate = 0.07m,
            LineItems = [new() { Description = "Item", Quantity = 1, UnitPrice = 200m, Unit = "flat" }]
        });

        updated.Subtotal.Should().Be(200m);
        updated.TaxAmount.Should().Be(14m);  // 200 * 0.07
        updated.Total.Should().Be(214m);
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdateServicePeriod()
    {
        var created = await _sut.CreateAsync(BuildRequest());

        var updated = await _sut.UpdateAsync(created.Id, BuildRequest() with
        {
            ServiceDate = null,
            ServicePeriodStart = Today.AddDays(-30),
            ServicePeriodEnd = Today.AddDays(-1),
        });

        updated.ServiceDate.Should().BeNull();
        updated.ServicePeriodStart.Should().Be(Today.AddDays(-30));
        updated.ServicePeriodEnd.Should().Be(Today.AddDays(-1));
    }

    [Fact]
    public async Task UpdateAsync_ShouldThrow_WhenInvoiceBelongsToOtherUser()
    {
        var invoice = await _sut.CreateAsync(BuildRequest());

        var userBService = ServiceFor(Guid.NewGuid());
        var act = () => userBService.UpdateAsync(invoice.Id, BuildRequest());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task UpdateAsync_ShouldNotChangeCreatedAt()
    {
        var created = await _sut.CreateAsync(BuildRequest());
        var originalCreatedAt = created.CreatedAt;

        var updated = await _sut.UpdateAsync(created.Id, BuildRequest() with { RecipientName = "Changed" });

        updated.CreatedAt.Should().Be(originalCreatedAt);
        updated.UpdatedAt.Should().BeAfter(originalCreatedAt);
    }

    // ── Line item order ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ShouldPreserveLineItemOrder_RoundTrip()
    {
        // Deliberately not alphabetically or price-sorted input
        var created = await _sut.CreateAsync(BuildRequest() with
        {
            LineItems =
            [
                new() { Description = "Zebra", Quantity = 1, UnitPrice = 10m, Unit = "h" },
                new() { Description = "Apfel", Quantity = 2, UnitPrice = 300m, Unit = "flat" },
                new() { Description = "Mango", Quantity = 3, UnitPrice = 5m, Unit = "day" },
            ]
        });

        var fetched = await _sut.GetAsync(created.Id);

        fetched.LineItems.Select(li => li.Description)
            .Should().ContainInOrder("Zebra", "Apfel", "Mango");

        var stored = await _db.LineItems.Where(li => li.InvoiceId == created.Id).ToListAsync();
        stored.OrderBy(li => li.Position).Select(li => li.Description)
            .Should().ContainInOrder("Zebra", "Apfel", "Mango");
        stored.Select(li => li.Position).Should().BeEquivalentTo([0, 1, 2]);
    }

    [Fact]
    public async Task GetAsync_ShouldSortLineItemsByPosition_NotInsertionOrder()
    {
        var created = await _sut.CreateAsync(BuildRequest() with
        {
            LineItems = [new() { Description = "Only", Quantity = 1, UnitPrice = 10m, Unit = "h" }]
        });

        // Simulate arbitrary DB return order: insert extra items with positions
        // that contradict insertion order.
        _db.LineItems.AddRange(
            new LineItem { InvoiceId = created.Id, Description = "Third", Quantity = 1, UnitPrice = 1m, Unit = "h", Position = 2 },
            new LineItem { InvoiceId = created.Id, Description = "Second", Quantity = 1, UnitPrice = 1m, Unit = "h", Position = 1 });
        await _db.SaveChangesAsync();

        var fetched = await _sut.GetAsync(created.Id);

        fetched.LineItems.Select(li => li.Description)
            .Should().ContainInOrder("Only", "Second", "Third");
    }

    [Fact]
    public async Task UpdateAsync_ShouldPreserveLineItemOrder()
    {
        var created = await _sut.CreateAsync(BuildRequest());

        await _sut.UpdateAsync(created.Id, BuildRequest() with
        {
            LineItems =
            [
                new() { Description = "Neu C", Quantity = 1, UnitPrice = 30m, Unit = "h" },
                new() { Description = "Neu A", Quantity = 1, UnitPrice = 10m, Unit = "h" },
                new() { Description = "Neu B", Quantity = 1, UnitPrice = 20m, Unit = "h" },
            ]
        });

        var fetched = await _sut.GetAsync(created.Id);

        fetched.LineItems.Select(li => li.Description)
            .Should().ContainInOrder("Neu C", "Neu A", "Neu B");
    }

    [Fact]
    public async Task CancelAsync_ShouldPreserveLineItemOrder_OnStorno()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest() with
        {
            LineItems =
            [
                new() { Description = "Zebra", Quantity = 1, UnitPrice = 10m, Unit = "h" },
                new() { Description = "Apfel", Quantity = 2, UnitPrice = 300m, Unit = "flat" },
            ]
        });
        await _sut.FinalizeAsync(draft.Id);

        var storno = await _sut.CancelAsync(draft.Id);

        storno.LineItems.Select(li => li.Description)
            .Should().ContainInOrder("Zebra", "Apfel");
    }

    // ── Display mode (Pauschal) ─────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ShouldPersistDisplayMode_RoundTrip()
    {
        var created = await _sut.CreateAsync(BuildRequest() with
        {
            LineItems =
            [
                new() { Description = "Detail", Quantity = 8, UnitPrice = 90m, Unit = "h" },
                new() { Description = "Pauschal", Quantity = 8, UnitPrice = 90m, Unit = "h", DisplayMode = LineItemDisplayMode.FlatRate },
            ]
        });

        var fetched = await _sut.GetAsync(created.Id);

        fetched.LineItems[0].DisplayMode.Should().Be(LineItemDisplayMode.AsEntered);
        fetched.LineItems[1].DisplayMode.Should().Be(LineItemDisplayMode.FlatRate);
        // display-only: the stored math is untouched
        fetched.LineItems[1].Quantity.Should().Be(8);
        fetched.LineItems[1].UnitPrice.Should().Be(90m);
        fetched.LineItems[1].Total.Should().Be(720m);
    }

    [Fact]
    public async Task UpdateAsync_ShouldPersistDisplayMode()
    {
        var created = await _sut.CreateAsync(BuildRequest());

        var updated = await _sut.UpdateAsync(created.Id, BuildRequest() with
        {
            LineItems = [new() { Description = "Jetzt pauschal", Quantity = 2, UnitPrice = 100m, Unit = "h", DisplayMode = LineItemDisplayMode.FlatRate }]
        });

        updated.LineItems[0].DisplayMode.Should().Be(LineItemDisplayMode.FlatRate);
    }

    [Fact]
    public async Task CancelAsync_ShouldCopyDisplayMode_OnStorno()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest() with
        {
            LineItems = [new() { Description = "Pauschal", Quantity = 8, UnitPrice = 90m, Unit = "h", DisplayMode = LineItemDisplayMode.FlatRate }]
        });
        await _sut.FinalizeAsync(draft.Id);

        var storno = await _sut.CancelAsync(draft.Id);

        storno.LineItems[0].DisplayMode.Should().Be(LineItemDisplayMode.FlatRate);
        storno.LineItems[0].Total.Should().Be(-720m);
    }

    // ── Reopen (ADR 0003) ───────────────────────────────────────────────────

    [Fact]
    public async Task ReopenAsync_ShouldResetToDraft_KeepingNumberAndSequence()
    {
        var id = await CreateFinalized();
        var numberBefore = (await _sut.GetAsync(id)).Number;
        var counterBefore = await SequenceCounter();

        var reopened = await _sut.ReopenAsync(id);

        reopened.Status.Should().Be(InvoiceStatus.Draft);
        reopened.Number.Should().Be(numberBefore);
        (await SequenceCounter()).Should().Be(counterBefore);
    }

    [Fact]
    public async Task ReopenAsync_ShouldDiscardArchivedPdf()
    {
        var id = await CreateFinalized();
        (await _db.InvoicePdfs.FindAsync(id)).Should().NotBeNull();

        await _sut.ReopenAsync(id);

        (await _db.InvoicePdfs.FindAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task ReopenAsync_ShouldWriteAuditEntry()
    {
        var id = await CreateFinalized();

        await _sut.ReopenAsync(id);

        var entries = await _db.InvoiceAuditEntries.Where(e => e.InvoiceId == id).ToListAsync();
        entries.Should().ContainSingle();
        entries[0].Action.Should().Be(InvoiceAuditActions.Reopened);
        entries[0].UserId.Should().Be(_userId);
        entries[0].Note.Should().Contain($"{Today.Year}-001");
    }

    [Fact]
    public async Task ReopenAsync_ShouldAllowEditingAfterwards()
    {
        var id = await CreateFinalized();
        await _sut.ReopenAsync(id);

        var updated = await _sut.UpdateAsync(id, BuildRequest() with { RecipientName = "Korrigiert GmbH" });

        updated.RecipientName.Should().Be("Korrigiert GmbH");
    }

    [Fact]
    public async Task ReopenAsync_ShouldThrowValidation_ForDraft()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());

        var act = () => _sut.ReopenAsync(draft.Id);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ReopenAsync_ShouldThrowConflict_ForPaid()
    {
        var id = await CreateFinalized();
        await _sut.UpdateStatusAsync(id, InvoiceStatus.Paid);

        var act = () => _sut.ReopenAsync(id);

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("Storno");
    }

    [Fact]
    public async Task ReopenAsync_ShouldThrowConflict_ForCancelled()
    {
        var id = await CreateFinalized();
        await _sut.CancelAsync(id);

        var act = () => _sut.ReopenAsync(id);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task ReopenAsync_ShouldThrowConflict_ForCancellationInvoice()
    {
        var id = await CreateFinalized();
        var storno = await _sut.CancelAsync(id);

        var act = () => _sut.ReopenAsync(storno.Id);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task ReopenAsync_ShouldThrowNotFound_ForOtherUsersInvoice()
    {
        var id = await CreateFinalized();

        var userBService = ServiceFor(Guid.NewGuid());
        var act = () => userBService.ReopenAsync(id);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task FinalizeAsync_AfterReopen_ShouldReuseNumber_WithoutSequenceIncrement()
    {
        var id = await CreateFinalized();
        var numberBefore = (await _sut.GetAsync(id)).Number;
        var counterBefore = await SequenceCounter();

        await _sut.ReopenAsync(id);
        var refinalized = await _sut.FinalizeAsync(id);

        refinalized.Status.Should().Be(InvoiceStatus.Finalized);
        refinalized.Number.Should().Be(numberBefore);
        (await SequenceCounter()).Should().Be(counterBefore);

        // The next fresh invoice continues the sequence without gap or collision
        var next = await _sut.CreateAsync(BuildRequest());
        (await _sut.FinalizeAsync(next.Id)).Number.Should().Be($"{Today.Year}-002");
    }

    [Fact]
    public async Task FinalizeAsync_AfterReopen_ShouldArchiveFreshPdf()
    {
        var id = await CreateFinalized();
        await _sut.ReopenAsync(id);
        (await _db.InvoicePdfs.FindAsync(id)).Should().BeNull();

        await _sut.FinalizeAsync(id);

        var archived = await _db.InvoicePdfs.FindAsync(id);
        archived.Should().NotBeNull();
        archived!.Data.Should().NotBeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrowConflict_ForReopenedDraftWithNumber()
    {
        var id = await CreateFinalized();
        await _sut.ReopenAsync(id);

        var act = () => _sut.DeleteAsync(id);

        (await act.Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain("number");
    }

    // ── Serialization ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ReturnsStatusAsString()
    {
        var id = await CreateFinalized();
        await _sut.UpdateStatusAsync(id, InvoiceStatus.Paid);

        var result = await _sut.GetAsync(id);

        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var json = System.Text.Json.JsonSerializer.Serialize(result, opts);

        json.Should().Contain("\"status\":\"Paid\"");
        json.Should().NotMatchRegex("\"status\":\\d");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // FinalizeAsync stamps dates with the local calendar day (DateTime.Today)
    private static DateOnly LocalToday => DateOnly.FromDateTime(DateTime.Today);

    private InvoiceService ServiceFor(Guid userId)
        => new(_db, new FakeCurrentUserService(userId), new FakePdfService(), new FakeEInvoiceService());

    private async Task<int> SequenceCounter()
        => (await _db.InvoiceNumberSequences.FindAsync(_userId, Today.Year))?.Counter ?? 0;

    private async Task<Guid> CreateFinalized()
    {
        await SeedCompleteUser();
        var draft = await _sut.CreateAsync(BuildRequest());
        await _sut.FinalizeAsync(draft.Id);
        return draft.Id;
    }

    private async Task SeedCompleteUser(Guid? userId = null, Action<User>? configure = null)
    {
        var user = CompleteUser(userId ?? _userId);
        configure?.Invoke(user);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
    }

    private static User CompleteUser(Guid userId) => new()
    {
        Id = userId,
        Email = $"{userId:N}@test.local",
        PasswordHash = "hash",
        Name = "Test User",
        TaxNumber = "143/262/12345",
        VatId = null,
        IsSmallBusiness = true,
        Street = "Musterstraße 1",
        PostalCode = "80331",
        City = "München",
        Country = "Deutschland",
        Phone = "+49 89 1234567",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static CreateInvoiceRequest BuildRequest() => new()
    {
        SenderName = "Tobias Dev",
        SenderAddress = "Musterstraße 1, 80331 München",
        RecipientName = "ACME GmbH",
        RecipientAddress = "Testweg 5, 10115 Berlin",
        RecipientStreet = "Testweg 5",
        RecipientPostalCode = "10115",
        RecipientCity = "Berlin",
        RecipientCountryCode = "DE",
        RecipientEmail = "rechnung@acme.example",
        TaxRate = 0.19m,
        ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3),
        LineItems =
        [
            new() { Description = "Web Development", Quantity = 2, UnitPrice = 80m, Unit = "h" },
            new() { Description = "Project Setup", Quantity = 1, UnitPrice = 50m, Unit = "flat" }
        ]
    };

    public void Dispose() => _db.Dispose();
}

internal class FakeCurrentUserService(Guid userId) : ICurrentUserService
{
    public Guid CurrentUserId => userId;
}

internal class FakePdfService : IPdfService
{
    public byte[] Generate(Invoice invoice, User user) => [0x25, 0x50, 0x44, 0x46]; // "%PDF"
}

internal class FakeEInvoiceService : IEInvoiceService
{
    // Minimal well-formed XML — enough for archiving tests without pulling in the
    // real XRechnung generator (exercised separately in EInvoiceServiceTests).
    public byte[] Generate(Invoice invoice, User user)
        => System.Text.Encoding.UTF8.GetBytes($"<Invoice><Number>{invoice.Number}</Number></Invoice>");
}
