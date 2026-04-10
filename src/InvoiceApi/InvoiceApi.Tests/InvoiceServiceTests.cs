using FluentAssertions;
using InvoiceApi.Data;
using InvoiceApi.Exceptions;
using InvoiceApi.Models;
using InvoiceApi.Services;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Tests;

public class InvoiceServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly InvoiceService _sut;

    public InvoiceServiceTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(opts);
        _sut = new InvoiceService(_db);
    }

    [Fact]
    public async Task CreateAsync_ShouldPersistInvoice()
    {
        var request = BuildRequest();

        var result = await _sut.CreateAsync(request);

        result.Should().NotBeNull();
        result.Number.Should().StartWith("INV-");
        result.LineItems.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateAsync_ShouldCalculateTotalsCorrectly()
    {
        // 2h à 80 + 1 flat à 50 = 210 net, 19% = 39.90, total = 249.90
        var request = BuildRequest();

        var result = await _sut.CreateAsync(request);

        result.Subtotal.Should().Be(210m);
        result.TaxAmount.Should().Be(39.90m);
        result.Total.Should().Be(249.90m);
    }

    [Fact]
    public async Task CreateAsync_ShouldAutoIncrementInvoiceNumbers()
    {
        await _sut.CreateAsync(BuildRequest());
        await _sut.CreateAsync(BuildRequest());
        var third = await _sut.CreateAsync(BuildRequest());

        var year = DateTime.UtcNow.Year;
        third.Number.Should().Be($"INV-{year}-0003");
    }

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
    public async Task UpdateStatusAsync_ShouldUpdateStatus()
    {
        var created = await _sut.CreateAsync(BuildRequest());

        var updated = await _sut.UpdateStatusAsync(created.Id, InvoiceStatus.Paid);

        updated.Status.Should().Be(InvoiceStatus.Paid);
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByStatus()
    {
        var inv = await _sut.CreateAsync(BuildRequest());
        await _sut.UpdateStatusAsync(inv.Id, InvoiceStatus.Paid);
        await _sut.CreateAsync(BuildRequest()); // Draft

        var paid = await _sut.ListAsync(InvoiceStatus.Paid, 1, 10);
        var drafts = await _sut.ListAsync(InvoiceStatus.Draft, 1, 10);

        paid.Should().HaveCount(1);
        drafts.Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveInvoice()
    {
        var created = await _sut.CreateAsync(BuildRequest());

        await _sut.DeleteAsync(created.Id);

        var act = () => _sut.GetAsync(created.Id);
        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ---

    private static CreateInvoiceRequest BuildRequest() => new()
    {
        SenderName = "Tobias Dev",
        SenderAddress = "Musterstraße 1, 80331 München",
        RecipientName = "ACME GmbH",
        RecipientAddress = "Testweg 5, 10115 Berlin",
        TaxRate = 0.19m,
        LineItems =
        [
            new() { Description = "Web Development", Quantity = 2, UnitPrice = 80m, Unit = "h" },
            new() { Description = "Project Setup", Quantity = 1, UnitPrice = 50m, Unit = "flat" }
        ]
    };

    public void Dispose() => _db.Dispose();
}
