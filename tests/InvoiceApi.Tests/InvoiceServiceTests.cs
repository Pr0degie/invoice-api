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
    private readonly Guid _userId = Guid.NewGuid();

    public InvoiceServiceTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(opts);
        _sut = new InvoiceService(_db, new FakeCurrentUserService(_userId));
    }

    [Fact]
    public async Task CreateAsync_ShouldPersistInvoice()
    {
        var result = await _sut.CreateAsync(BuildRequest());

        result.Should().NotBeNull();
        result.Number.Should().StartWith("INV-");
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

    [Fact]
    public async Task GetAsync_ShouldNotReturnOtherUsersInvoice()
    {
        // Arrange: create invoice as user A
        var invoiceA = await _sut.CreateAsync(BuildRequest());

        // Act: try to access it as user B
        var userBService = new InvoiceService(_db, new FakeCurrentUserService(Guid.NewGuid()));
        var act = () => userBService.GetAsync(invoiceA.Id);

        // Assert: 404 (not 403 — don't leak existence)
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ListAsync_ShouldIsolateInvoicesPerUser()
    {
        // Arrange: create two invoices for user A, one for user B
        await _sut.CreateAsync(BuildRequest());
        await _sut.CreateAsync(BuildRequest());

        var userBService = new InvoiceService(_db, new FakeCurrentUserService(Guid.NewGuid()));
        await userBService.CreateAsync(BuildRequest());

        // Act
        var userAInvoices = await _sut.ListAsync(null, 1, 100);
        var userBInvoices = await userBService.ListAsync(null, 1, 100);

        // Assert
        userAInvoices.Should().HaveCount(2);
        userBInvoices.Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteAsync_ShouldThrow_WhenDeletingOtherUsersInvoice()
    {
        var invoice = await _sut.CreateAsync(BuildRequest());

        var userBService = new InvoiceService(_db, new FakeCurrentUserService(Guid.NewGuid()));
        var act = () => userBService.DeleteAsync(invoice.Id);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetAsync_ReturnsStatusAsString()
    {
        var created = await _sut.CreateAsync(BuildRequest());
        await _sut.UpdateStatusAsync(created.Id, InvoiceStatus.Paid);

        var result = await _sut.GetAsync(created.Id);

        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var json = System.Text.Json.JsonSerializer.Serialize(result, opts);

        json.Should().Contain("\"status\":\"Paid\"");
        json.Should().NotMatchRegex("\"status\":\\d");
    }

    [Fact]
    public async Task GetAsync_IncludesSenderAndRecipientAddress()
    {
        var created = await _sut.CreateAsync(BuildRequest());

        var result = await _sut.GetAsync(created.Id);

        result.SenderAddress.Should().NotBeNullOrWhiteSpace();
        result.RecipientAddress.Should().NotBeNullOrWhiteSpace();
    }

    // ---

    [Fact]
    public async Task ListAsync_ShouldFilterBySearch_OnRecipientName()
    {
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "Müller GmbH" });
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "Müller Consulting GmbH" });
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "ACME Corp" });

        var result = await _sut.ListAsync(null, 1, 100, search: "Müller");

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(i => i.RecipientName.Should().Contain("Müller"));
    }

    [Fact]
    public async Task ListAsync_ShouldFilterBySearch_OnInvoiceNumber()
    {
        await _sut.CreateAsync(BuildRequest()); // INV-YYYY-0001
        await _sut.CreateAsync(BuildRequest()); // INV-YYYY-0002
        await _sut.CreateAsync(BuildRequest()); // INV-YYYY-0003

        var year = DateTime.UtcNow.Year;
        var result = await _sut.ListAsync(null, 1, 100, search: $"{year}-0002");

        result.Should().HaveCount(1);
        result[0].Number.Should().Be($"INV-{year}-0002");
    }

    [Fact]
    public async Task ListAsync_SearchShouldBeCaseInsensitive()
    {
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "Müller GmbH" });
        await _sut.CreateAsync(BuildRequest() with { RecipientName = "ACME Corp" });

        var result = await _sut.ListAsync(null, 1, 100, search: "müller");

        result.Should().HaveCount(1);
        result[0].RecipientName.Should().Be("Müller GmbH");
    }

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

internal class FakeCurrentUserService(Guid userId) : ICurrentUserService
{
    public Guid CurrentUserId => userId;
}
