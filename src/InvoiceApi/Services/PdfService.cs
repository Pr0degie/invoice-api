using System.Globalization;
using InvoiceApi.Models;
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

    // Program.cs sets this too; repeating it here keeps direct construction
    // (unit tests, seeding) license-safe.
    static PdfService() => QuestPDF.Settings.License = LicenseType.Community;

    public byte[] Generate(Invoice invoice, User user)
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
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily("Arial"));

                if (isDraft)
                    page.Foreground().AlignCenter().AlignMiddle()
                        .Text("ENTWURF").FontSize(96).Bold().FontColor("#e5e7eb");

                page.Header().Element(c => ComposeHeader(c, invoice));
                page.Content().Element(c => ComposeContent(c, invoice, user, smallBusiness, taxRate));
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

    private static void ComposeContent(IContainer container, Invoice invoice, User user, bool smallBusiness, decimal taxRate)
    {
        container.PaddingTop(10).Column(col =>
        {
            col.Spacing(16);

            // Absenderzeile + recipient block, info column right
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    // DIN 5008 Absenderzeile: separated from the recipient by a thin
                    // rule (same stroke as the footer) instead of a font underline,
                    // which sat directly on the recipient name.
                    c.Item().Text(SenderLine(invoice)).FontSize(7).FontColor(MutedColor);
                    c.Item().PaddingTop(2).PaddingBottom(8)
                        .LineHorizontal(0.5f).LineColor("#d1d5db");
                    c.Item().Text(invoice.RecipientName).Bold();
                    c.Item().Text(invoice.RecipientAddress).FontSize(9);
                });

                row.ConstantItem(220).Column(c =>
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

            // line items
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(28);
                    c.RelativeColumn(4);
                    c.RelativeColumn(1);
                    c.RelativeColumn(1);
                    c.RelativeColumn(1.5f);
                    c.RelativeColumn(1.5f);
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

                foreach (var (item, index) in invoice.LineItems.OrderBy(li => li.Position).Select((x, i) => (x, i)))
                {
                    var bg = index % 2 == 0 ? "#ffffff" : "#f8f9fa";

                    table.Cell().Background(bg).Padding(6).Text($"{index + 1}").FontColor(MutedColor);
                    table.Cell().Background(bg).Padding(6).Text(item.Description);
                    table.Cell().Background(bg).Padding(6).AlignRight()
                        .Text(item.Quantity.ToString("0.##", De));
                    table.Cell().Background(bg).Padding(6).Text(UnitLabel(item.Unit)).FontColor(MutedColor);
                    table.Cell().Background(bg).Padding(6).AlignRight()
                        .Text(Amount(item.UnitPrice, invoice.Currency));
                    table.Cell().Background(bg).Padding(6).AlignRight()
                        .Text(Amount(item.Total, invoice.Currency));
                }
            });

            // totals — § 19: no VAT line at all, total = net
            var net = invoice.Subtotal;
            var vat = Math.Round(net * taxRate, 2);

            col.Item().AlignRight().Column(totals =>
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
                col.Item().Text("Gemäß § 19 UStG wird keine Umsatzsteuer berechnet.").FontSize(9);

            if (invoice.Type == InvoiceType.Cancellation)
                col.Item().Text("Der Betrag wird entsprechend verrechnet bzw. erstattet.")
                    .FontSize(9).FontColor(MutedColor);
            else
                col.Item().Text($"Zahlbar ohne Abzug bis {Date(invoice.DueDate)}.").FontSize(9);

            if (!string.IsNullOrWhiteSpace(invoice.Notes))
            {
                col.Item().Column(n =>
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
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(FooterAddress(user)).FontSize(7.5f).FontColor(MutedColor);
                    c.Item().Text(FooterTaxIds(user)).FontSize(7.5f).FontColor(MutedColor);
                });

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

    // Absenderzeile above the recipient window — from the invoice's own snapshot,
    // never live settings, so archived PDFs stay self-consistent.
    private static string SenderLine(Invoice invoice)
    {
        var parts = invoice.SenderAddress
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Prepend(invoice.SenderName);
        return string.Join(" · ", parts);
    }

    private static string FooterAddress(User user)
    {
        var parts = new[] { user.Street, $"{user.PostalCode} {user.City}".Trim(), user.Country }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(", ", parts);
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
