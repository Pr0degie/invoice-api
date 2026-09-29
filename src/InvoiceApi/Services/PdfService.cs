using System.Globalization;
using InvoiceApi.Models;
using QuestPDF.Drawing.Exceptions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace InvoiceApi.Services;

public interface IPdfService
{
    byte[] Generate(Invoice invoice, User user);
}

// German invoice layout carrying every § 14 Abs. 4 UStG Pflichtangabe:
// sender name + full address, recipient, Steuernummer/USt-IdNr., issue date,
// Leistungsdatum/-zeitraum, invoice number, line items with quantity and unit
// price, net total — plus either the VAT breakdown or the verbatim § 19 UStG
// notice for Kleinunternehmer. Invoice PDFs are German regardless of UI locale.
public class PdfService : IPdfService
{
    private static readonly string PrimaryColor = "#1a1a2e";
    private static readonly string MutedColor = "#6b7280";

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    // Line-item columns: fixed "Pos." column, then Beschreibung, Menge, Einheit,
    // Einzelpreis, Gesamt (relative widths).
    private const float PosColumnWidth = 28;
    private static readonly float[] ItemColumnWidths = [4, 1, 1, 1.5f, 1.5f];

    // Page geometry in points. The left margin is DIN 5008's 20 mm so the address
    // field lines up with a DL window envelope (DIN 680).
    private const float PointsPerMm = 72f / 25.4f;
    private const float PageMargin = 40;
    private const float PageMarginLeft = 20 * PointsPerMm;
    private const float ContentGap = 10;

    // DIN 5008 Form B, measured from the top edge of page 1.
    private const float AddressFieldTop = 45 * PointsPerMm;
    private const float AddressFieldWidth = 85 * PointsPerMm;
    private const float NoteZoneHeight = 17.7f * PointsPerMm;     // Zusatz- und Vermerkzone
    private const float AddressZoneHeight = 27.3f * PointsPerMm;  // Anschriftzone
    private const float InfoBlockTop = 50 * PointsPerMm;
    // Falzmarken only — the optional Lochmarke (148.5 mm) read as a third fold.
    private static readonly float[] FoldMarks = [105 * PointsPerMm, 210 * PointsPerMm];
    private const float FoldMarkLength = 5 * PointsPerMm;

    // Program.cs sets this too; repeating it here keeps direct construction
    // (unit tests, seeding) license-safe.
    static PdfService() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] Generate(Invoice invoice, User user)
    {
        try
        {
            return Render(invoice, user, keepTogether: true);
        }
        catch (DocumentLayoutException)
        {
            // A block taller than a whole page (one huge position, very long notes)
            // can't be kept together — fall back to normal paging rather than failing
            // the PDF (finalization archives it).
            return Render(invoice, user, keepTogether: false);
        }
    }

    private static byte[] Render(Invoice invoice, User user, bool keepTogether)
    {
        var isDraft = invoice.Status == InvoiceStatus.Draft;
        // Drafts preview what finalization will produce; finalized invoices render
        // their immutable snapshot.
        var smallBusiness = isDraft ? user.IsSmallBusiness : invoice.IsSmallBusiness;
        var taxRate = smallBusiness ? 0m : invoice.TaxRate;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(PageMargin);
                page.MarginLeft(PageMarginLeft);
                // Liberation Serif explicitly (not "Arial"): the original invoices
                // rendered serif because the font-less base image fell back to a serif
                // default. Once the Dockerfile added fontconfig, "Arial" started
                // resolving to Liberation *Sans*, which widened the header so "Pos."
                // wrapped and the black bar grew taller. Pin the serif to keep the look.
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily("Liberation Serif"));

                if (isDraft)
                    page.Foreground().AlignCenter().AlignMiddle()
                        .Text("ENTWURF").FontSize(96).Bold().FontColor("#e5e7eb");

                // Fold marks only on the sheet that gets folded into the envelope.
                page.Background().ShowOnce().Element(ComposeFoldMarks);

                page.Header().Column(header =>
                {
                    // Page 1: the letterhead reserves the space above the DIN 5008 address
                    // field, so the content starts exactly at 45 mm. MinHeight, not Height:
                    // an unusually tall letterhead pushes the field down instead of failing.
                    header.Item().ShowOnce().MinHeight(AddressFieldTop - PageMargin - ContentGap)
                        .Element(c => ComposeHeader(c, invoice));
                    header.Item().SkipOnce().Element(c => ComposeHeader(c, invoice));
                });
                page.Content().Element(c => ComposeContent(c, invoice, user, smallBusiness, taxRate, keepTogether));
                page.Footer().Element(c => ComposeFooter(c, user));
            });
        }).GeneratePdf();
    }

    private static string Title(Invoice invoice) => invoice.Type == InvoiceType.Cancellation
        ? "Stornorechnung"
        : "Rechnung";

    private static void ComposeHeader(IContainer container, Invoice invoice)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(invoice.SenderName)
                    .FontSize(18).Bold().FontColor(PrimaryColor);
                col.Item().Text(invoice.SenderAddress)
                    .FontSize(9).FontColor(MutedColor);
            });

            row.ConstantItem(220).Column(col =>
            {
                col.Item().AlignRight().Text(Title(invoice).ToUpperInvariant())
                    .FontSize(invoice.Type == InvoiceType.Cancellation ? 16 : 20)
                    .Bold().FontColor(PrimaryColor);
                col.Item().AlignRight().Text($"Nr. {invoice.Number ?? "Entwurf"}")
                    .FontSize(10).FontColor(MutedColor);
            });
        });
    }

    private static void ComposeContent(IContainer container, Invoice invoice, User user, bool smallBusiness, decimal taxRate, bool keepTogether)
    {
        container.PaddingTop(ContentGap).Column(col =>
        {
            col.Spacing(16);

            // DIN 5008 Form B address field (85 × 45 mm at 20 mm / 45 mm) — what a DL
            // window envelope shows — with the Informationsblock to its right. MinHeight,
            // not Height: an unusually long address grows instead of failing the PDF.
            col.Item().PaddingBottom(10).Row(row =>
            {
                row.ConstantItem(AddressFieldWidth).Column(field =>
                {
                    // Zusatz- und Vermerkzone: Rücksendeangabe in its bottom line. The
                    // thin rule (same stroke as the footer) ends with the text (Shrink).
                    field.Item().MinHeight(NoteZoneHeight).AlignBottom().PaddingBottom(4).Shrink().Column(sender =>
                    {
                        sender.Item().Text(SenderLine(invoice)).FontSize(7).FontColor(MutedColor);
                        sender.Item().PaddingTop(2).LineHorizontal(0.5f).LineColor("#d1d5db");
                    });

                    // Anschriftzone
                    field.Item().MinHeight(AddressZoneHeight).Column(address =>
                    {
                        address.Item().Text(invoice.RecipientName).FontSize(12).Bold();
                        address.Item().Text(RecipientAddress(invoice)).FontSize(10);
                    });
                });

                row.RelativeItem();

                row.ConstantItem(220).PaddingTop(InfoBlockTop - AddressFieldTop).Column(c =>
                {
                    InfoRow(c, "Rechnungsnummer", invoice.Number ?? "Entwurf");
                    InfoRow(c, "Rechnungsdatum", Date(invoice.IssueDate));

                    if (invoice.ServiceDate is { } serviceDate)
                        InfoRow(c, "Leistungsdatum", Date(serviceDate));
                    else if (invoice.ServicePeriodStart is { } start && invoice.ServicePeriodEnd is { } end)
                        InfoRow(c, "Leistungszeitraum", $"{Date(start)} – {Date(end)}");

                    if (invoice.Type != InvoiceType.Cancellation)
                        InfoRow(c, "Zahlbar bis", Date(invoice.DueDate));

                    if (!string.IsNullOrWhiteSpace(user.TaxNumber))
                        InfoRow(c, "Steuernummer", user.TaxNumber!);
                    if (!string.IsNullOrWhiteSpace(user.VatId))
                        InfoRow(c, "USt-IdNr.", user.VatId!);
                });
            });

            if (invoice.Type == InvoiceType.Cancellation && invoice.CancellationOfNumber is not null)
                col.Item().Text($"Stornorechnung zu Rechnung {invoice.CancellationOfNumber}")
                    .Bold().FontColor(PrimaryColor);

            var items = invoice.LineItems.OrderBy(li => li.Position).ToList();

            // line items
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(PosColumnWidth);
                    foreach (var width in ItemColumnWidths)
                        c.RelativeColumn(width);
                });

                table.Header(h =>
                {
                    foreach (var (label, right) in new[]
                    {
                        ("Pos.", false), ("Beschreibung", false), ("Menge", true),
                        ("Einheit", false), ("Einzelpreis", true), ("Gesamt", true)
                    })
                    {
                        var cell = h.Cell().Background(PrimaryColor).Padding(6);
                        (right ? cell.AlignRight() : cell)
                            .Text(label).FontSize(9).Bold().FontColor(Colors.White);
                    }
                });

                for (var index = 0; index < items.Count; index++)
                {
                    var item = items[index];
                    var bg = index % 2 == 0 ? "#ffffff" : "#f8f9fa";

                    // FlatRate: display-only collapse to 1 × pauschal × line total.
                    // The stored quantity/unit/price stay untouched (and keep the
                    // math intact — 1 × total == total). Storno items carry a
                    // negative quantity, so the flat "1" keeps its sign.
                    var flat = item.DisplayMode == LineItemDisplayMode.FlatRate;
                    var quantity = flat ? (item.Quantity < 0 ? -1m : 1m) : item.Quantity;
                    var unit = flat ? "flat" : item.Unit;
                    var unitPrice = flat ? Math.Abs(item.Total) : item.UnitPrice;

                    // cell(column) hands out the six containers in column order
                    void ItemCells(Func<int, IContainer> cell)
                    {
                        cell(0).Text($"{index + 1}").FontColor(MutedColor);
                        cell(1).Text(item.Description);
                        cell(2).AlignRight().Text(quantity.ToString("0.##", De));
                        cell(3).Text(UnitLabel(unit)).FontColor(MutedColor);
                        cell(4).AlignRight().Text(Amount(unitPrice, invoice.Currency));
                        cell(5).AlignRight().Text(Amount(item.Total, invoice.Currency));
                    }

                    if (index < items.Count - 1)
                    {
                        // ShowEntire on every cell: a position that doesn't fit the rest of
                        // the page moves to the next one as a whole instead of splitting.
                        ItemCells(_ =>
                        {
                            IContainer cell = table.Cell();
                            if (keepTogether)
                                cell = cell.ShowEntire();
                            return cell.Background(bg).Padding(6);
                        });
                        continue;
                    }

                    // The last position shares one full-width cell with the closing block,
                    // so no page break can fall between them: the total never stands alone
                    // on a page. Staying inside the table keeps the repeated header when
                    // both move to the next page. The row mirrors the column definition.
                    IContainer last = table.Cell().ColumnSpan(1 + (uint)ItemColumnWidths.Length);
                    if (keepTogether)
                        last = last.ShowEntire();

                    last.Column(block =>
                    {
                        block.Item().Row(row => ItemCells(column =>
                            (column == 0 ? row.ConstantItem(PosColumnWidth) : row.RelativeItem(ItemColumnWidths[column - 1]))
                                .Background(bg).Padding(6)));
                        block.Item().PaddingTop(16).Element(c => ComposeClosing(c, invoice, smallBusiness, taxRate));
                    });
                }
            });

            if (items.Count == 0)
                col.Item().Element(c => ComposeClosing(c, invoice, smallBusiness, taxRate));
        });
    }

    // closing block — total, legal notices, payment terms and notes
    private static void ComposeClosing(IContainer container, Invoice invoice, bool smallBusiness, decimal taxRate)
    {
        container.Column(end =>
        {
            end.Spacing(16);

            // totals — § 19: no VAT line at all, total = net
            var net = invoice.Subtotal;
            var vat = Math.Round(net * taxRate, 2);

            end.Item().AlignRight().Column(totals =>
            {
                totals.Spacing(4);

                if (!smallBusiness)
                {
                    TotalRow(totals, "Nettobetrag", Amount(net, invoice.Currency));
                    TotalRow(totals, $"zzgl. {taxRate.ToString("P0", De)} USt.", Amount(vat, invoice.Currency));
                }

                totals.Item().LineHorizontal(1).LineColor(PrimaryColor);

                totals.Item().Row(r =>
                {
                    r.ConstantItem(120).Text("Gesamtbetrag").Bold().FontSize(12);
                    r.ConstantItem(120).AlignRight()
                        .Text(Amount(net + vat, invoice.Currency))
                        .Bold().FontSize(12).FontColor(PrimaryColor);
                });
            });

            if (smallBusiness)
                end.Item().Text("Gemäß § 19 UStG wird keine Umsatzsteuer berechnet.").FontSize(9);

            if (invoice.Type == InvoiceType.Cancellation)
                end.Item().Text("Der Betrag wird entsprechend verrechnet bzw. erstattet.")
                    .FontSize(9).FontColor(MutedColor);
            else
                end.Item().Text($"Zahlbar ohne Abzug bis {Date(invoice.DueDate)}.").FontSize(9);

            if (!string.IsNullOrWhiteSpace(invoice.Notes))
            {
                end.Item().Column(n =>
                {
                    n.Item().Text("Anmerkungen").Bold().FontSize(9).FontColor(MutedColor);
                    n.Item().Text(invoice.Notes).FontSize(9);
                });
            }
        });
    }

    private static void InfoRow(ColumnDescriptor col, string label, string value)
    {
        col.Item().Row(r =>
        {
            r.RelativeItem().Text(label).FontColor(MutedColor).FontSize(9);
            r.RelativeItem().AlignRight().Text(value).FontSize(9);
        });
    }

    private static void TotalRow(ColumnDescriptor col, string label, string value)
    {
        col.Item().Row(r =>
        {
            r.ConstantItem(120).Text(label).FontColor(MutedColor);
            r.ConstantItem(120).AlignRight().Text(value);
        });
    }

    private static void ComposeFooter(IContainer container, User user)
    {
        container.Column(col =>
        {
            col.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor("#d1d5db");

            col.Item().Row(row =>
            {
                // no address here — the letterhead repeats on every page
                row.RelativeItem().Text(FooterTaxIds(user)).FontSize(7.5f).FontColor(MutedColor);

                if (!string.IsNullOrWhiteSpace(user.Iban))
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().AlignRight().Text("Bankverbindung").FontSize(7.5f).Bold().FontColor(MutedColor);
                        if (!string.IsNullOrWhiteSpace(user.BankName))
                            c.Item().AlignRight().Text(user.BankName).FontSize(7.5f).FontColor(MutedColor);
                        c.Item().AlignRight().Text($"IBAN: {user.Iban}").FontSize(7.5f).FontColor(MutedColor);
                        if (!string.IsNullOrWhiteSpace(user.Bic))
                            c.Item().AlignRight().Text($"BIC: {user.Bic}").FontSize(7.5f).FontColor(MutedColor);
                    });
                }
            });

            col.Item().AlignCenter().Text(t =>
            {
                t.Span("Seite ").FontSize(7.5f).FontColor(MutedColor);
                t.CurrentPageNumber().FontSize(7.5f).FontColor(MutedColor);
                t.Span(" von ").FontSize(7.5f).FontColor(MutedColor);
                t.TotalPages().FontSize(7.5f).FontColor(MutedColor);
            });
        });
    }

    // DIN 5008 fold marks (105 / 210 mm), 5 mm in from the left edge — outside the
    // 20 mm text margin, inside a printer's printable area.
    private static void ComposeFoldMarks(IContainer container)
    {
        container.Layers(layers =>
        {
            layers.PrimaryLayer();
            foreach (var top in FoldMarks)
                layers.Layer().PaddingTop(top).PaddingLeft(5 * PointsPerMm).Width(FoldMarkLength)
                    .LineHorizontal(0.5f).LineColor(MutedColor);
        });
    }

    // Rücksendeangabe for the envelope window — from the invoice's own snapshot,
    // never live settings, so archived PDFs stay self-consistent.
    private static string SenderLine(Invoice invoice)
    {
        var parts = invoice.SenderAddress
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Prepend(invoice.SenderName);
        return string.Join(" · ", parts);
    }

    // Recipient block: prefer the structured fields (street / "postal city" /
    // country) so the PDF matches the E-Rechnung XML; fall back to the legacy
    // free-text RecipientAddress for pre-E-Rechnung invoices.
    private static string RecipientAddress(Invoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.RecipientStreet)
            && string.IsNullOrWhiteSpace(invoice.RecipientPostalCode)
            && string.IsNullOrWhiteSpace(invoice.RecipientCity))
            return invoice.RecipientAddress;

        var cityLine = $"{invoice.RecipientPostalCode} {invoice.RecipientCity}".Trim();
        var lines = new[] { invoice.RecipientStreet, cityLine, invoice.RecipientCountryCode }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join("\n", lines);
    }

    private static string FooterTaxIds(User user)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(user.TaxNumber)) parts.Add($"Steuernummer: {user.TaxNumber}");
        if (!string.IsNullOrWhiteSpace(user.VatId)) parts.Add($"USt-IdNr.: {user.VatId}");
        return string.Join(" · ", parts);
    }

    private static string UnitLabel(string unit) => unit switch
    {
        "h" => "Std.",
        "day" => "Tage",
        "piece" => "Stück",
        "flat" => "pauschal",
        _ => unit,
    };

    private static string Date(DateOnly d) => d.ToString("dd.MM.yyyy", De);

    private static string Amount(decimal amount, string currency)
        => currency == "EUR"
            ? amount.ToString("N2", De) + " €"
            : amount.ToString("N2", De) + " " + currency;
}
