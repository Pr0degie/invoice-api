using InvoiceApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<LineItem> LineItems => Set<LineItem>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<InvoicePdf> InvoicePdfs => Set<InvoicePdf>();
    public DbSet<InvoiceNumberSequence> InvoiceNumberSequences => Set<InvoiceNumberSequence>();
    public DbSet<InvoiceAuditEntry> InvoiceAuditEntries => Set<InvoiceAuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Invoice>(e =>
        {
            e.HasKey(x => x.Id);
            // Number is null while Draft; unique per user once assigned
            // (Postgres treats NULLs as distinct, so many drafts coexist).
            e.Property(x => x.Number).HasMaxLength(50);
            e.HasIndex(x => new { x.UserId, x.Number }).IsUnique()
             .HasDatabaseName("IX_Invoices_UserId_Number");
            e.Property(x => x.CancellationOfNumber).HasMaxLength(50);
            // SetNull so account deletion (user → invoices cascade) never trips over
            // the storno→original link; CancellationOfNumber keeps the reference text.
            e.HasOne<Invoice>()
             .WithMany()
             .HasForeignKey(x => x.CancellationOfId)
             .OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.TaxRate).HasPrecision(5, 4);
            e.HasMany(x => x.LineItems)
             .WithOne()
             .HasForeignKey(li => li.InvoiceId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.User)
             .WithMany(u => u.Invoices)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.Property(x => x.TotalAmount).HasPrecision(18, 2);

            // composite index for stats queries (UserId always first)
            e.HasIndex(x => new { x.UserId, x.Status, x.IssueDate })
             .HasDatabaseName("IX_Invoices_UserId_Status_IssueDate");

            // don't store computed columns
            e.Ignore(x => x.Subtotal);
            e.Ignore(x => x.TaxAmount);
            e.Ignore(x => x.Total);
        });

        b.Entity<LineItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Ignore(x => x.Total);
        });

        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).IsRequired().HasMaxLength(256);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.DefaultSenderName).HasMaxLength(200);
            e.Property(x => x.DefaultSenderAddress).HasMaxLength(200);
            e.Property(x => x.TaxNumber).HasMaxLength(50);
            e.Property(x => x.VatId).HasMaxLength(20);
            e.Property(x => x.IsSmallBusiness).HasDefaultValue(true);
            e.Property(x => x.Street).HasMaxLength(200);
            e.Property(x => x.PostalCode).HasMaxLength(20);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.Country).HasMaxLength(100);
            e.Property(x => x.Iban).HasMaxLength(34);
            e.Property(x => x.Bic).HasMaxLength(11);
            e.Property(x => x.BankName).HasMaxLength(100);
        });

        b.Entity<InvoicePdf>(e =>
        {
            e.HasKey(x => x.InvoiceId);
            e.HasOne(x => x.Invoice)
             .WithOne()
             .HasForeignKey<InvoicePdf>(x => x.InvoiceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InvoiceAuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).IsRequired().HasMaxLength(50);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.InvoiceId, x.Timestamp });
            // No navigation on Invoice — the trail is queried on its own. Cascade is
            // safe: numbered invoices (the only ones with entries) cannot be deleted.
            e.HasOne<Invoice>()
             .WithMany()
             .HasForeignKey(x => x.InvoiceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InvoiceNumberSequence>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Year });
            // Counter is the concurrency token — concurrent finalizations race on
            // the increment and the loser retries with a fresh number.
            e.Property(x => x.Counter).IsConcurrencyToken();
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Token).IsRequired().HasMaxLength(256);
            e.HasIndex(x => x.Token).IsUnique();
            e.Property(x => x.ReplacedByTokenHash).HasMaxLength(256);
            e.HasOne(x => x.User)
             .WithMany(u => u.RefreshTokens)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.IsRevoked);
        });
    }
}
