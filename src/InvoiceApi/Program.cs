using System.Text;
using InvoiceApi.Data;
using InvoiceApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QuestPDF.Infrastructure;
using Serilog;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// Database
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IPdfService, PdfService>();

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

builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(p =>
        p.WithOrigins(
            builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:3000"])
         .AllowAnyMethod()
         .AllowAnyHeader()
         .AllowCredentials()));

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
}

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
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
}).AllowAnonymous();
app.Run();

// Expose for integration tests
public partial class Program { }
