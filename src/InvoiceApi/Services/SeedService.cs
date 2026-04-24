using InvoiceApi.Data;
using InvoiceApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Services;

public class SeedService(AppDbContext db, IPasswordHasher hasher, ILogger<SeedService> logger)
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
            DefaultSenderAddress = "Musterstraße 1, 80331 München",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var invoices = BuildInvoices(user.Id, today);
        db.Invoices.AddRange(invoices);

        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Count} demo invoices for {Email}", invoices.Count, DemoEmail);
    }

    private static List<Invoice> BuildInvoices(Guid userId, DateOnly today)
    {
        var counter = 1;
        var year = today.Year;

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

            var subtotal = lineItems.Sum(li => li.Total);
            const decimal taxRate = 0.19m;
            var totalAmount = subtotal + Math.Round(subtotal * taxRate, 2);

            return new Invoice
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Number = $"INV-{year}-{counter++:D4}",
                SenderName = "Demo User",
                SenderAddress = "Musterstraße 1, 80331 München",
                RecipientName = recipient,
                RecipientAddress = "Testweg 5, 10115 Berlin",
                IssueDate = issue,
                DueDate = issue.AddDays(dueDays),
                TaxRate = taxRate,
                Currency = "EUR",
                Status = status,
                TotalAmount = totalAmount,
                PaidAt = status == InvoiceStatus.Paid ? issue.AddDays(dueDays - 3) : null,
                LineItems = lineItems,
                CreatedAt = new DateTimeOffset(issue.Year, issue.Month, issue.Day, 9, 0, 0, TimeSpan.Zero),
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        return new List<Invoice>
        {
            // ── Current month ────────────────────────────────────────────────
            Make(0, 5, "TechStart GmbH", InvoiceStatus.Sent,
                new() { ("Frontend Development", 20m, 95m, "h"), ("Code Review", 3m, 95m, "h") }),

            Make(0, 2, "Steinberg & Partner", InvoiceStatus.Draft,
                new() { ("Consulting – UX Audit", 1m, 1800m, "flat"), ("Report & Dokumentation", 4m, 95m, "h") }),

            // ── 1 month ago ──────────────────────────────────────────────────
            Make(1, 8, "Media Design AG", InvoiceStatus.Paid,
                new() { ("Logo Redesign", 1m, 2400m, "flat"), ("Brand Guidelines", 6m, 90m, "h") }),

            Make(1, 12, "TechStart GmbH", InvoiceStatus.Paid,
                new() { ("React Component Library", 30m, 95m, "h"), ("Unit Tests", 8m, 85m, "h") }),

            Make(1, 3, "Freelance Hub e.V.", InvoiceStatus.Overdue,
                new() { ("Workshop Facilitation", 2m, 650m, "day"), ("Materials & Prep", 1m, 200m, "flat") },
                dueDays: 7),

            // ── 2 months ago ─────────────────────────────────────────────────
            Make(2, 10, "Müller Consulting GmbH", InvoiceStatus.Paid,
                new() { ("API Development", 40m, 95m, "h"), ("Documentation", 5m, 80m, "h"), ("Deployment & CI Setup", 1m, 350m, "flat") }),

            Make(2, 4, "Steinberg & Partner", InvoiceStatus.Paid,
                new() { ("SEO Audit", 1m, 900m, "flat"), ("Keyword Research", 8m, 75m, "h") }),

            Make(2, 18, "Nextwave Digital", InvoiceStatus.Sent,
                new() { ("E-Commerce Integration", 25m, 95m, "h") }),

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
    }
}
