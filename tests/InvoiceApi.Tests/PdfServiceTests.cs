using FluentAssertions;
using InvoiceApi.Models;
using InvoiceApi.Services;
using UglyToad.PdfPig;

namespace InvoiceApi.Tests;

// Renders real PDFs (no fake) and reads them back per page with PdfPig.
public class PdfServiceTests
{
    private readonly PdfService _sut = new();

    [Fact]
    public void Generate_ShouldNeverSplitALineItemAcrossPages()
    {
        // 14 positions of varying height span several pages, so some position
        // ends up at a page break. Each carries a start and an end marker.
        var descriptions = Enumerable.Range(1, 14)
            .Select(k => $"START{k:D2} {Filler(20 + k * 7 % 40)} END{k:D2}")
            .ToList();

        var pages = ReadPages(_sut.Generate(NewInvoice(descriptions), NewUser()));

        pages.Should().HaveCountGreaterThan(1);
        for (var k = 1; k <= descriptions.Count; k++)
        {
            PageOf(pages, $"START{k:D2}").Should().Be(PageOf(pages, $"END{k:D2}"),
                $"position {k} must start and end on the same page");
        }
    }

    [Fact]
    public void Generate_ShouldStillRenderAPositionTallerThanAPage()
    {
        // Keeping a position together is impossible when it alone exceeds a page —
        // it must then split rather than fail the whole PDF (finalization archives it).
        var descriptions = new[] { "kurz", $"START99 {Filler(900)} END99", "auch kurz" };

        var pages = ReadPages(_sut.Generate(NewInvoice(descriptions), NewUser()));

        pages.Should().HaveCountGreaterThan(1);
        PageOf(pages, "END99").Should().BeGreaterThan(PageOf(pages, "START99"));
    }

    [Fact]
    public void Generate_ShouldKeepTheClosingBlockOnOnePage()
    {
        // Across invoice lengths the closing block (total, § 19 notice, payment
        // terms) eventually meets a page break — it must then move as a whole
        // instead of leaving e.g. the payment terms alone on the last page.
        for (var count = 1; count <= 24; count++)
        {
            var descriptions = Enumerable.Repeat(Filler(25), count).ToList();

            var pages = ReadPages(_sut.Generate(NewInvoice(descriptions), NewUser()));

            PageOf(pages, "Gesamtbetrag").Should().Be(PageOf(pages, "Abzug"), $"with {count} positions");
        }
    }

    private static string Filler(int words) => string.Join(" ", Enumerable.Repeat("Leistungsbeschreibung", words));

    private static List<string> ReadPages(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetPages().Select(p => p.Text).ToList();
    }

    private static int PageOf(List<string> pages, string marker)
    {
        var index = pages.FindIndex(text => text.Contains(marker));
        index.Should().BeGreaterThanOrEqualTo(0, $"marker {marker} must be rendered");
        return index;
    }

    private static User NewUser() => new()
    {
        Email = "test@example.com",
        PasswordHash = "x",
        Name = "Test User",
        TaxNumber = "123/456/78901",
        Street = "Musterweg 1",
        PostalCode = "12345",
        City = "Musterstadt",
        Country = "Deutschland",
    };

    private static Invoice NewInvoice(IReadOnlyList<string> descriptions) => new()
    {
        SenderName = "Test User",
        SenderAddress = "Musterweg 1\n12345 Musterstadt",
        RecipientName = "Kunde GmbH",
        RecipientAddress = "Kundenstraße 2\n54321 Kundenstadt",
        RecipientStreet = "Kundenstraße 2",
        RecipientPostalCode = "54321",
        RecipientCity = "Kundenstadt",
        IssueDate = new DateOnly(2026, 9, 27),
        DueDate = new DateOnly(2026, 10, 11),
        ServicePeriodStart = new DateOnly(2026, 8, 31),
        ServicePeriodEnd = new DateOnly(2026, 9, 27),
        TaxRate = 0m,
        LineItems = descriptions
            .Select((d, i) => new LineItem { Description = d, Quantity = 1, UnitPrice = 10, Position = i })
            .ToList(),
    };
}
