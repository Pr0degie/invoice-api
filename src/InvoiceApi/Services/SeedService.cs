using InvoiceApi.Data;
using InvoiceApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Services;

public class SeedService(AppDbContext db, IPasswordHasher hasher, IPdfService pdf, ILogger<SeedService> logger)
{
    public const string DemoEmail = "demo@invoiceflow.app";
    public const string DemoPassword = "DemoPass123!";

    public async Task SeedAsync()
    {
        if (await db.Users.AnyAsync())
            return;

        logger.LogInformation("Seeding demo user and invoices");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = DemoEmail,
            PasswordHash = hasher.Hash(DemoPassword),
            Name = "Demo User",
            DefaultSenderName = "Demo User",
            DefaultSenderAddress = "Musterstraße 42\n80331 München\nDeutschland",
            // Complete sender tax profile — Kleinunternehmer (§ 19 UStG), like the
            // product's primary persona. Finalization works out of the box.
            TaxNumber = "143/262/12345",
            VatId = null,
            IsSmallBusiness = true,
            Street = "Musterstraße 42",
            PostalCode = "80331",
            City = "München",
            Country = "Deutschland",
            Iban = "DE89370400440532013000",
            Bic = "COBADEFFXXX",
            BankName = "Commerzbank München",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var invoices = BuildInvoices(user.Id, today);

        AssignNumbers(invoices, user.Id);
        db.Invoices.AddRange(invoices);

        foreach (var invoice in invoices.Where(i => i.Status != InvoiceStatus.Draft))
            db.InvoicePdfs.Add(new InvoicePdf { InvoiceId = invoice.Id, Data = pdf.Generate(invoice, user) });

        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Count} demo invoices for {Email}", invoices.Count, DemoEmail);
    }

    // Sequential numbers per year in issue-date order ({year}-{counter:000}, drafts
    // stay null), plus the matching InvoiceNumberSequence rows.
    private void AssignNumbers(List<Invoice> invoices, Guid userId)
    {
        var counters = new Dictionary<int, int>();

        foreach (var invoice in invoices
            .Where(i => i.Status != InvoiceStatus.Draft)
            .OrderBy(i => i.IssueDate)
            .ThenBy(i => i.CreatedAt))
        {
            var year = invoice.IssueDate.Year;
            counters[year] = counters.GetValueOrDefault(year) + 1;
            invoice.Number = $"{year}-{counters[year]:D3}";
        }

        // Stornos snapshot the number of the invoice they reverse
        foreach (var storno in invoices.Where(i => i.CancellationOfId is not null))
            storno.CancellationOfNumber = invoices.First(i => i.Id == storno.CancellationOfId).Number;

        foreach (var (year, counter) in counters)
            db.InvoiceNumberSequences.Add(new InvoiceNumberSequence { UserId = userId, Year = year, Counter = counter });
    }

    private static List<Invoice> BuildInvoices(Guid userId, DateOnly today)
    {
        const string senderAddress = "Musterstraße 42\n80331 München\nDeutschland";

        // One canonical address per recipient — consistent across all invoices
        var recipientAddresses = new Dictionary<string, string>
        {
            ["TechStart GmbH"]       = "Leopoldstraße 1\n80802 München\nDeutschland",
            ["Steinberg & Partner"]  = "Marienplatz 1\n80331 München\nDeutschland",
            ["Media Design AG"]      = "Sendlinger Str. 7\n80331 München\nDeutschland",
            ["Freelance Hub e.V."]   = "Rosenthaler Str. 40\n10178 Berlin\nDeutschland",
            ["Müller Consulting GmbH"] = "Friedrichstraße 60\n10117 Berlin\nDeutschland",
            ["Nextwave Digital"]     = "Unter den Linden 10\n10117 Berlin\nDeutschland",
            ["Kranich Software AG"]  = "Am Hauptbahnhof 2\n60329 Frankfurt am Main\nDeutschland",
        };

        Invoice Make(
            int monthsAgo,
            int daysAgo,
            string recipient,
            InvoiceStatus status,
            List<(string Desc, decimal Qty, decimal Price, string Unit)> items,
            int dueDays = 14)
        {
            var issue = today.AddMonths(-monthsAgo).AddDays(-daysAgo);
            var lineItems = items.Select(i => new LineItem
            {
                Id = Guid.NewGuid(),
                Description = i.Desc,
                Quantity = i.Qty,
                UnitPrice = i.Price,
                Unit = i.Unit
            }).ToList();

            // Kleinunternehmer demo: no VAT anywhere (§ 19 UStG)
            var subtotal = lineItems.Sum(li => li.Total);

            return new Invoice
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Number = null, // assigned by AssignNumbers for non-drafts
                SenderName = "Demo User",
                SenderAddress = senderAddress,
                RecipientName = recipient,
                RecipientAddress = recipientAddresses[recipient],
                IssueDate = issue,
                DueDate = issue.AddDays(dueDays),
                // single-position invoices carry a service date, multi-position a period
                ServiceDate = lineItems.Count == 1 ? issue.AddDays(-2) : null,
                ServicePeriodStart = lineItems.Count > 1 ? issue.AddDays(-21) : null,
                ServicePeriodEnd = lineItems.Count > 1 ? issue.AddDays(-1) : null,
                TaxRate = 0m,
                Currency = "EUR",
                Status = status,
                IsSmallBusiness = status != InvoiceStatus.Draft,
                TotalAmount = subtotal,
                PaidAt = status == InvoiceStatus.Paid ? issue.AddDays(dueDays - 3) : null,
                LineItems = lineItems,
                CreatedAt = new DateTimeOffset(issue.Year, issue.Month, issue.Day, 9, 0, 0, TimeSpan.Zero),
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        var invoices = new List<Invoice>
        {
            // ── Current month ────────────────────────────────────────────────
            Make(0, 5, "TechStart GmbH", InvoiceStatus.Finalized,
                new() { ("Frontend Development", 20m, 95m, "h"), ("Code Review", 3m, 95m, "h") }),

            Make(0, 2, "Steinberg & Partner", InvoiceStatus.Draft,
                new() { ("Consulting – UX Audit", 1m, 1800m, "flat"), ("Report & Dokumentation", 4m, 95m, "h") }),

            // ── 1 month ago ──────────────────────────────────────────────────
            Make(1, 8, "Media Design AG", InvoiceStatus.Paid,
                new() { ("Logo Redesign", 1m, 2400m, "flat"), ("Brand Guidelines", 6m, 90m, "h") }),

            Make(1, 12, "TechStart GmbH", InvoiceStatus.Paid,
                new() { ("React Component Library", 30m, 95m, "h"), ("Unit Tests", 8m, 85m, "h") }),

            // due 7 days after an issue date >1 month back → shows up as overdue
            Make(1, 3, "Freelance Hub e.V.", InvoiceStatus.Finalized,
                new() { ("Workshop Facilitation", 2m, 650m, "day"), ("Materials & Prep", 1m, 200m, "flat") },
                dueDays: 7),

            // ── 2 months ago ─────────────────────────────────────────────────
            Make(2, 10, "Müller Consulting GmbH", InvoiceStatus.Paid,
                new() { ("API Development", 40m, 95m, "h"), ("Documentation", 5m, 80m, "h"), ("Deployment & CI Setup", 1m, 350m, "flat") }),

            Make(2, 4, "Steinberg & Partner", InvoiceStatus.Paid,
                new() { ("SEO Audit", 1m, 900m, "flat"), ("Keyword Research", 8m, 75m, "h") }),

            Make(2, 18, "Nextwave Digital", InvoiceStatus.Finalized,
                new() { ("E-Commerce Integration", 25m, 95m, "h") }, dueDays: 90),

            // ── 3 months ago ─────────────────────────────────────────────────
            Make(3, 5, "TechStart GmbH", InvoiceStatus.Paid,
                new() { ("Performance Audit", 1m, 800m, "flat"), ("Optimization", 12m, 95m, "h") }),

            Make(3, 14, "Freelance Hub e.V.", InvoiceStatus.Paid,
                new() { ("Annual Report Design", 1m, 1600m, "flat"), ("Print-Ready Export", 2m, 90m, "h") }),

            Make(3, 2, "Kranich Software AG", InvoiceStatus.Cancelled,
                new() { ("Project Discovery Phase", 1m, 500m, "flat") }),

            // ── 5 months ago ─────────────────────────────────────────────────
            Make(5, 7, "Media Design AG", InvoiceStatus.Paid,
                new() { ("Mobile App UI Design", 1m, 3800m, "flat"), ("Prototype (Figma)", 10m, 90m, "h") }),

            Make(5, 20, "Müller Consulting GmbH", InvoiceStatus.Paid,
                new() { ("Backend Refactoring", 35m, 95m, "h"), ("Load Testing", 4m, 80m, "h") }),

            // ── 8 months ago ─────────────────────────────────────────────────
            Make(8, 3, "Nextwave Digital", InvoiceStatus.Paid,
                new() { ("CMS Migration", 28m, 90m, "h"), ("Data Import Scripts", 6m, 85m, "h"), ("Training Session", 1m, 600m, "flat") }),

            // ── 11 months ago ────────────────────────────────────────────────
            Make(11, 5, "Steinberg & Partner", InvoiceStatus.Paid,
                new() { ("Full-Stack MVP Build", 80m, 95m, "h"), ("Hosting Setup", 1m, 250m, "flat") })
        };

        // Stornorechnung for the cancelled Kranich invoice — reverses it with
        // negative amounts, issued 5 days after the original.
        var cancelled = invoices.Single(i => i.Status == InvoiceStatus.Cancelled);
        var stornoIssue = cancelled.IssueDate.AddDays(5);
        var storno = new Invoice
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = InvoiceType.Cancellation,
            Status = InvoiceStatus.Finalized,
            SenderName = cancelled.SenderName,
            SenderAddress = cancelled.SenderAddress,
            RecipientName = cancelled.RecipientName,
            RecipientAddress = cancelled.RecipientAddress,
            IssueDate = stornoIssue,
            DueDate = stornoIssue,
            ServiceDate = cancelled.ServiceDate,
            ServicePeriodStart = cancelled.ServicePeriodStart,
            ServicePeriodEnd = cancelled.ServicePeriodEnd,
            TaxRate = 0m,
            Currency = "EUR",
            IsSmallBusiness = true,
            CancellationOfId = cancelled.Id,
            LineItems = cancelled.LineItems.Select(li => new LineItem
            {
                Id = Guid.NewGuid(),
                Description = li.Description,
                Quantity = -li.Quantity,
                UnitPrice = li.UnitPrice,
                Unit = li.Unit
            }).ToList(),
            TotalAmount = -cancelled.TotalAmount,
            CreatedAt = new DateTimeOffset(stornoIssue.Year, stornoIssue.Month, stornoIssue.Day, 9, 0, 0, TimeSpan.Zero),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        invoices.Add(storno);

        return invoices;
    }
}
