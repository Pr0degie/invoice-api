using System.Text;
using System.Threading.RateLimiting;
using InvoiceApi.Data;
using InvoiceApi.Middleware;
using InvoiceApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QuestPDF.Infrastructure;
using Serilog;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// DATABASE_URL adapter — managed Postgres providers inject postgres://user:pass@host:port/db
// TLS cert validation is ON by default; Database__TrustServerCertificate=true is an
// explicit opt-out for setups with self-signed certs (e.g. provider-internal networking).
var trustServerCertificate = builder.Configuration.GetValue("Database:TrustServerCertificate", false);
var connectionString = ParseDatabaseUrl(builder.Configuration["DATABASE_URL"], trustServerCertificate)
    ?? builder.Configuration.GetConnectionString("Default");

builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(connectionString));

// Services
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IStatsService, StatsService>();
builder.Services.AddScoped<IPdfService, PdfService>();
builder.Services.AddScoped<IEInvoiceService, EInvoiceService>();
builder.Services.AddScoped<SeedService>();

// E-mail: real SMTP only when explicitly selected (Email:Provider=Smtp), otherwise
// the log-only sender — the default in Development and anywhere SMTP isn't configured.
if (string.Equals(builder.Configuration["Email:Provider"], "Smtp", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
else
    builder.Services.AddScoped<IEmailSender, LogEmailSender>();

// Delivery is decoupled from the request path: services enqueue, a background
// worker drains the queue and sends. Keeps SMTP latency/failures off the auth
// endpoints (no enumeration oracle, no request failure on a mail outage).
builder.Services.AddSingleton<IEmailQueue, ChannelEmailQueue>();
builder.Services.AddHostedService<EmailBackgroundService>();

// JWT auth — key presence/strength is validated at startup below; no fallback here
var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// Behind the reverse proxy (Coolify/Traefik) the socket peer is the proxy, not the client.
// Trust X-Forwarded-For/-Proto so RemoteIpAddress (used by rate-limit partitions) is the real client IP.
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The proxy IPs aren't statically known — clear the loopback-only defaults.
    opts.KnownNetworks.Clear();
    opts.KnownProxies.Clear();
});

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

// CORS — named policy, exact origins + optional preview-deploy suffix.
// No AllowCredentials: the API is Bearer-only; credentials mode is for cookies.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
// e.g. "-tobias-team.vercel.app" (the Vercel team scope). Empty = preview deploys disabled.
var previewOriginSuffix = builder.Configuration["Cors:PreviewOriginSuffix"];

builder.Services.AddCors(opts =>
    opts.AddPolicy("InvoiceFlowFrontend", p =>
        p.SetIsOriginAllowed(origin =>
        {
            if (allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                return true;

            return !string.IsNullOrEmpty(previewOriginSuffix)
                && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && uri.Scheme == "https"
                && uri.Host.EndsWith(previewOriginSuffix, StringComparison.OrdinalIgnoreCase);
        })
        .AllowAnyHeader()
        .AllowAnyMethod()));

builder.Services.AddControllers()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
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

// E-mail config validation — a broken/incomplete SMTP setup must abort the
// boot; delivery happens in the background worker, where a config error would
// otherwise only be logged while the mail is silently lost.
EmailStartupValidation.Validate(app.Configuration, app.Environment.IsProduction());

// Auto-run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    // Demo seeding in Production is an explicit opt-in (Seed__Enabled=true on the demo instance only)
    if (app.Configuration.GetValue<bool>("Seed:Enabled"))
    {
        if (app.Environment.IsProduction())
            app.Logger.LogWarning("Seed:Enabled is true in Production — seeding demo data. " +
                "This should only be the case on the demo instance.");

        var seeder = scope.ServiceProvider.GetRequiredService<SeedService>();
        await seeder.SeedAsync();
    }
}

// Must run before anything that reads the client IP (request logging, rate limiting)
app.UseForwardedHeaders();

app.UseSerilogRequestLogging();

// Domain exceptions → status codes with { error } body
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Security headers — HSTS is handled at the edge (Coolify proxy / CDN)
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
app.UseAuthentication();
app.UseAuthorization();
// After auth so the "api-user" policy can partition on ctx.User (sub claim)
app.UseRateLimiter();
app.MapControllers();
// The DB probe result is cached for ~10 s so the anonymous endpoint can't be
// used to hammer the database with cheap requests.
app.MapGet("/health", async (AppDbContext db, IMemoryCache cache, CancellationToken ct) =>
{
    var canConnect = await cache.GetOrCreateAsync("health:db-up", async entry =>
    {
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10);
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch
        {
            return false;
        }
    });

    return canConnect
        ? Results.Ok(new { status = "healthy", database = "up" })
        : Results.StatusCode(503);
}).AllowAnonymous().DisableRateLimiting();

app.Run();

// Converts a DATABASE_URL (postgres://user:pass@host:port/db) into Npgsql format
static string? ParseDatabaseUrl(string? databaseUrl, bool trustServerCertificate)
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
        var trust = trustServerCertificate ? "true" : "false";
        return $"Host={uri.Host};Port={port};Database={db};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate={trust}";
    }
    catch
    {
        return null;
    }
}

// Expose for integration tests
public partial class Program { }
