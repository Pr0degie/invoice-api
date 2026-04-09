using InvoiceApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<LineItem> LineItems => Set<LineItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Invoice>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Number).IsRequired().HasMaxLength(50);
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.TaxRate).HasPrecision(5, 4);
            e.HasMany(x => x.LineItems)
             .WithOne()
             .HasForeignKey(li => li.InvoiceId)
             .OnDelete(DeleteBehavior.Cascade);

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
    }
}
