using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InvoiceApi.Data;

// Used by EF Core design-time tools (dotnet ef migrations add) without running Program.cs
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(config.GetConnectionString("Default"))
            .Options;

        return new AppDbContext(opts);
    }
}
