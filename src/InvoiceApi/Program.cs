using System.Text;
using System.Threading.RateLimiting;
using InvoiceApi.Data;
using InvoiceApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QuestPDF.Infrastructure;
using Serilog;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// DATABASE_URL adapter — Railway provides postgres://user:pass@host:port/db
var connectionString = ParseDatabaseUrl(builder.Configuration["DATABASE_URL"])
    ?? builder.Configuration.GetConnectionString("Default");

builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(connectionString));

// Services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IStatsService, StatsService>();
builder.Services.AddScoped<IPdfService, PdfService>();
builder.Services.AddScoped<SeedService>();

// JWT auth
var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = builder.Configuration["Jwt:SigningKey"] ?? string.Empty;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.MapInboundClaims = false;
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(signingKey.Length > 0 ? signingKey : new string('x', 32))),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// Rate limiting — per-IP for auth, per-user for API
builder.Services.AddRateLimiter(opts =>
{
    opts.AddPolicy("auth-ip", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1)
            }));

    opts.AddPolicy("api-user", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.User.FindFirst("sub")?.Value
                          ?? ctx.Connection.RemoteIpAddress?.ToString()
                          ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));

    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// CORS — named policy, exact origins + Vercel preview pattern
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(opts =>
    opts.AddPolicy("InvoiceFlowFrontend", p =>
        p.SetIsOriginAllowed(origin =>
        {
            if (allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                return true;

            // Allow Vercel preview deployments without wildcards in config
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && uri.Scheme == "https"
                && uri.Host.StartsWith("invoiceflow-", StringComparison.OrdinalIgnoreCase)
                && uri.Host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase);
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "InvoiceApi", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT access token"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Startup key validation — fails fast with a clear message
const string keyPlaceholder = "CHANGE_ME_IN_PRODUCTION_min_32_chars_long";
var runtimeKey = app.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(runtimeKey) || runtimeKey.Length < 32 || runtimeKey == keyPlaceholder)
{
    throw new InvalidOperationException(
        "Jwt:SigningKey must be at least 32 characters and not the default placeholder. " +
        "Set the Jwt__SigningKey environment variable.");
}

// Auto-run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    if (app.Configuration.GetValue<bool>("Seed:Enabled"))
    {
        var seeder = scope.ServiceProvider.GetRequiredService<SeedService>();
        await seeder.SeedAsync();
    }
}

app.UseSerilogRequestLogging();

// Security headers — HSTS is handled at the edge (Railway / Cloudflare)
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("X-Frame-Options", "DENY");
    ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("InvoiceFlowFrontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
{
    try
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        return canConnect
            ? Results.Ok(new { status = "healthy", database = "up" })
            : Results.StatusCode(503);
    }
    catch
    {
        return Results.StatusCode(503);
    }
}).AllowAnonymous().DisableRateLimiting();

app.Run();

// Converts Railway's DATABASE_URL (postgres://user:pass@host:port/db) to Npgsql format
static string? ParseDatabaseUrl(string? databaseUrl)
{
    if (string.IsNullOrEmpty(databaseUrl)) return null;
    try
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = userInfo[0];
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var port = uri.Port > 0 ? uri.Port : 5432;
        var db = uri.AbsolutePath.TrimStart('/');
        return $"Host={uri.Host};Port={port};Database={db};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true";
    }
    catch
    {
        return null;
    }
}

// Expose for integration tests
public partial class Program { }
