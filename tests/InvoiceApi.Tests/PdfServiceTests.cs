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
        // ends up at a page break. Each carries a start and an end marker. Each
        // one fits on a page (the filler wraps one word per line) — a taller one
        // is the fallback case below.
        var descriptions = Enumerable.Range(1, 14)
            .Select(k => $"START{k:D2} {Filler(20 + k * 7 % 30)} END{k:D2}")
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

    [Fact]
    public void Generate_ShouldNeverLeaveTheTotalAloneOnAPage()
    {
        // Across invoice lengths the page break eventually falls between the last
        // position and the closing block — the last position must then move along
        // so the total never stands alone on the final page.
        for (var count = 1; count <= 24; count++)
        {
            var descriptions = Enumerable.Range(1, count).Select(k => $"POS{k:D2} {Filler(25)}").ToList();

            var pages = ReadPages(_sut.Generate(NewInvoice(descriptions), NewUser()));

            PageOf(pages, "Gesamtbetrag").Should().Be(PageOf(pages, $"POS{count:D2}"), $"with {count} positions");
        }
    }

    [Fact]
    public void Generate_ShouldPrintTheSenderAddressOnlyInLetterheadAndWindow()
    {
        // The letterhead (from the invoice snapshot) carries the address § 14 Abs. 4 /
        // § 34a UStDV require on every page; page 1 adds the Rücksendeangabe for the
        // envelope window. Never repeated in the footer.
        var descriptions = Enumerable.Repeat(Filler(40), 14).ToList();

        var pages = ReadPages(_sut.Generate(NewInvoice(descriptions), NewUser()));

        pages.Should().HaveCountGreaterThan(1);
        for (var page = 0; page < pages.Count; page++)
            CountOf(pages[page], "Musterweg").Should().Be(page == 0 ? 2 : 1, $"page {page + 1}");
    }

    [Fact]
    public void Generate_ShouldPlaceTheRecipientInTheDin5008FormBAddressZone()
    {
        // DL window envelopes show the DIN 5008 Form B address field: 20 mm from the
        // left, 85 mm wide; its Anschriftzone runs 62.7–90 mm from the top.
        var words = WordsMm(_sut.Generate(NewInvoice(new[] { "kurz" }), NewUser()), 1);

        foreach (var marker in new[] { "Kunde", "GmbH", "Kundenstraße", "54321", "Kundenstadt" })
        {
            var word = words.Single(w => w.Text == marker);
            word.Left.Should().BeGreaterThanOrEqualTo(20 - Tolerance, marker);
            word.Right.Should().BeLessThanOrEqualTo(105, marker);
            word.Top.Should().BeGreaterThanOrEqualTo(62.7 - Tolerance, marker);
            word.Bottom.Should().BeLessThanOrEqualTo(90, marker);
        }
    }

    [Fact]
    public void Generate_ShouldPrintTheReturnAddressInTheDin5008NoteZone()
    {
        // The Rücksendeangabe belongs in the Zusatz- und Vermerkzone (45–62.7 mm),
        // so the post office sees it through the window as well.
        var words = WordsMm(_sut.Generate(NewInvoice(new[] { "kurz" }), NewUser()), 1);

        words.Should().Contain(w => w.Text == "Musterweg"
            && w.Left >= 20 - Tolerance && w.Right <= 105 && w.Top >= 45 - Tolerance && w.Bottom <= 62.7);
    }

    [Fact]
    public void Generate_ShouldPrintFoldMarksOnTheFirstPageOnly()
    {
        // DIN 5008 Form B: fold marks at 105 and 210 mm at the left edge, outside the
        // text area. No punch mark (148.5 mm) — a third line read as a third fold.
        var pdf = _sut.Generate(NewInvoice(Enumerable.Repeat(Filler(40), 14).ToList()), NewUser());

        var marks = MarkPositionsMm(pdf, 1);
        marks.Should().HaveCount(2);
        marks[0].Should().BeApproximately(105, 0.5);
        marks[1].Should().BeApproximately(210, 0.5);
        MarkPositionsMm(pdf, 2).Should().BeEmpty();
    }

    private const double PointsPerMm = 72 / 25.4;
    private const double Tolerance = 0.01; // mm — float rounding between QuestPDF and PdfPig

    // Words of one page with their box in mm, measured from the top-left corner.
    private static List<(string Text, double Left, double Top, double Right, double Bottom)> WordsMm(byte[] pdf, int pageNumber)
    {
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(pageNumber);
        return page.GetWords()
            .Select(w => (w.Text,
                w.BoundingBox.Left / PointsPerMm,
                (page.Height - w.BoundingBox.Top) / PointsPerMm,
                w.BoundingBox.Right / PointsPerMm,
                (page.Height - w.BoundingBox.Bottom) / PointsPerMm))
            .ToList();
    }

    // Vertical positions (mm from the top) of drawn paths left of the text area.
    private static List<double> MarkPositionsMm(byte[] pdf, int pageNumber)
    {
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(pageNumber);
        return page.Paths
            .Select(p => p.GetBoundingRectangle())
            .OfType<UglyToad.PdfPig.Core.PdfRectangle>()
            .Where(box => box.Right / PointsPerMm < 15)
            .Select(box => Math.Round((page.Height - box.Top) / PointsPerMm, 1))
            .Distinct()
            .OrderBy(y => y)
            .ToList();
    }

    private static int CountOf(string text, string marker) =>
        (text.Length - text.Replace(marker, "").Length) / marker.Length;

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
