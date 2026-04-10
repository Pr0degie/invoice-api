using InvoiceApi.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace InvoiceApi.Services;

public interface IPdfService
{
    byte[] Generate(Invoice invoice);
}

public class PdfService : IPdfService
{
    private static readonly string PrimaryColor = "#1a1a2e";
    private static readonly string AccentColor = "#4361ee";
    private static readonly string MutedColor = "#6b7280";

    public byte[] Generate(Invoice invoice)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily("Arial"));

                page.Header().Element(c => ComposeHeader(c, invoice));
                page.Content().Element(c => ComposeContent(c, invoice));
                page.Footer().Element(ComposeFooter);
            });
        }).GeneratePdf();
    }

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

            row.ConstantItem(120).Column(col =>
            {
                col.Item().AlignRight().Text("INVOICE")
                    .FontSize(22).Bold().FontColor(AccentColor);
                col.Item().AlignRight().Text(invoice.Number)
                    .FontSize(10).FontColor(MutedColor);
            });
        });
    }

    private static void ComposeContent(IContainer container, Invoice invoice)
    {
        container.Column(col =>
        {
            col.Spacing(16);

            // recipient + dates
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("Bill To").Bold().FontColor(MutedColor).FontSize(9);
                    c.Item().Text(invoice.RecipientName).Bold();
                    c.Item().Text(invoice.RecipientAddress).FontSize(9).FontColor(MutedColor);
                });

                row.ConstantItem(160).Column(c =>
                {
                    c.Item().Row(r =>
                    {
                        r.RelativeItem().Text("Issue Date").FontColor(MutedColor).FontSize(9);
                        r.RelativeItem().AlignRight().Text(invoice.IssueDate.ToString("dd.MM.yyyy"));
                    });
                    c.Item().Row(r =>
                    {
                        r.RelativeItem().Text("Due Date").FontColor(MutedColor).FontSize(9);
                        r.RelativeItem().AlignRight().Text(invoice.DueDate.ToString("dd.MM.yyyy"));
                    });
                });
            });

            // line items table
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(4);
                    c.RelativeColumn(1);
                    c.RelativeColumn(1);
                    c.RelativeColumn(1.5f);
                });

                // header
                table.Header(h =>
                {
                    foreach (var label in new[] { "Description", "Qty", "Unit", "Amount" })
                    {
                        h.Cell().Background(PrimaryColor).Padding(6)
                            .Text(label).FontSize(9).Bold().FontColor(Colors.White);
                    }
                });

                // rows
                foreach (var (item, index) in invoice.LineItems.Select((x, i) => (x, i)))
                {
                    var bg = index % 2 == 0 ? "#ffffff" : "#f8f9fa";

                    table.Cell().Background(bg).Padding(6).Text(item.Description);
                    table.Cell().Background(bg).Padding(6).AlignRight()
                        .Text($"{item.Quantity:G}");
                    table.Cell().Background(bg).Padding(6).AlignCenter()
                        .Text(item.Unit).FontColor(MutedColor);
                    table.Cell().Background(bg).Padding(6).AlignRight()
                        .Text(FormatAmount(item.Total, invoice.Currency));
                }
            });

            // totals
            col.Item().AlignRight().Column(totals =>
            {
                totals.Spacing(4);
                TotalRow(totals, "Subtotal", FormatAmount(invoice.Subtotal, invoice.Currency));
                TotalRow(totals, $"VAT ({invoice.TaxRate:P0})", FormatAmount(invoice.TaxAmount, invoice.Currency));

                totals.Item().LineHorizontal(1).LineColor(PrimaryColor);

                totals.Item().Row(r =>
                {
                    r.ConstantItem(100).Text("Total").Bold().FontSize(12);
                    r.ConstantItem(120).AlignRight()
                        .Text(FormatAmount(invoice.Total, invoice.Currency))
                        .Bold().FontSize(12).FontColor(AccentColor);
                });
            });

            // notes
            if (!string.IsNullOrWhiteSpace(invoice.Notes))
            {
                col.Item().Column(n =>
                {
                    n.Item().Text("Notes").Bold().FontSize(9).FontColor(MutedColor);
                    n.Item().Text(invoice.Notes).FontSize(9);
                });
            }
        });
    }

    private static void TotalRow(ColumnDescriptor col, string label, string value)
    {
        col.Item().Row(r =>
        {
            r.ConstantItem(100).Text(label).FontColor(MutedColor);
            r.ConstantItem(120).AlignRight().Text(value);
        });
    }

    private static void ComposeFooter(IContainer container)
    {
        container.AlignCenter().Text(t =>
        {
            t.Span("Page ").FontSize(8).FontColor(MutedColor);
            t.CurrentPageNumber().FontSize(8).FontColor(MutedColor);
            t.Span(" of ").FontSize(8).FontColor(MutedColor);
            t.TotalPages().FontSize(8).FontColor(MutedColor);
        });
    }

    private static string FormatAmount(decimal amount, string currency)
        => $"{amount:N2} {currency}";
}
