using FluentAssertions;
using InvoiceApi.Services;
using Microsoft.Extensions.Configuration;

namespace InvoiceApi.Tests.Auth;

public class EmailStartupValidationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> ValidSmtp(string frontendBaseUrl = "https://app.invoiceflow.test") =>
        new()
        {
            ["Email:Provider"] = "Smtp",
            ["Email:Smtp:Host"] = "smtp.example.com",
            ["Email:FromAddress"] = "no-reply@invoiceflow.app",
            ["FRONTEND_BASE_URL"] = frontendBaseUrl
        };

    // ── Provider selection ────────────────────────────────────────────────────

    [Fact]
    public void CompleteSmtpConfig_Passes()
    {
        var act = () => EmailStartupValidation.Validate(Config(ValidSmtp()), isProduction: true);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Log")]
    [InlineData("")]
    [InlineData(null)]
    public void NonSmtpProvider_OutsideProduction_Passes(string? provider)
    {
        var config = Config(new() { ["Email:Provider"] = provider });
        var act = () => EmailStartupValidation.Validate(config, isProduction: false);
        act.Should().NotThrow();
    }

    [Fact]
    public void ExplicitLogProvider_InProduction_Passes()
    {
        var config = Config(new() { ["Email:Provider"] = "Log" });
        var act = () => EmailStartupValidation.Validate(config, isProduction: true);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void MissingProvider_InProduction_Throws(string? provider)
    {
        var config = Config(new() { ["Email:Provider"] = provider });
        var act = () => EmailStartupValidation.Validate(config, isProduction: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Email__Provider*");
    }

    // A typo would silently select the log-only sender — must refuse to start
    // in every environment.
    [Theory]
    [InlineData("SMPT")]
    [InlineData("sendgrid")]
    public void UnknownProvider_Throws(string provider)
    {
        var config = Config(new() { ["Email:Provider"] = provider });
        var act = () => EmailStartupValidation.Validate(config, isProduction: false);
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{provider}*");
    }

    // ── Required SMTP settings ────────────────────────────────────────────────

    [Fact]
    public void SmtpWithoutHost_Throws()
    {
        var values = ValidSmtp();
        values.Remove("Email:Smtp:Host");
        var act = () => EmailStartupValidation.Validate(Config(values), isProduction: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Email__Smtp__Host*");
    }

    [Fact]
    public void SmtpWithoutFromAddress_Throws()
    {
        var values = ValidSmtp();
        values.Remove("Email:FromAddress");
        var act = () => EmailStartupValidation.Validate(Config(values), isProduction: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Email__FromAddress*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public void SmtpWithInvalidPort_Throws(string port)
    {
        var values = ValidSmtp();
        values["Email:Smtp:Port"] = port;
        var act = () => EmailStartupValidation.Validate(Config(values), isProduction: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Port*");
    }

    [Fact]
    public void SmtpWithUserButNoPassword_Throws()
    {
        var values = ValidSmtp();
        values["Email:Smtp:User"] = "apikey";
        var act = () => EmailStartupValidation.Validate(Config(values), isProduction: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*must be set together*");
    }

    [Fact]
    public void SmtpWithPasswordButNoUser_Throws()
    {
        var values = ValidSmtp();
        values["Email:Smtp:Password"] = "secret";
        var act = () => EmailStartupValidation.Validate(Config(values), isProduction: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*must be set together*");
    }

    [Fact]
    public void SmtpWithCredentialPair_Passes()
    {
        var values = ValidSmtp();
        values["Email:Smtp:User"] = "apikey";
        values["Email:Smtp:Password"] = "secret";
        var act = () => EmailStartupValidation.Validate(Config(values), isProduction: true);
        act.Should().NotThrow();
    }

    // ── Frontend base URL (mail links) ────────────────────────────────────────

    [Fact]
    public void SmtpInProduction_WithDefaultLocalhostFrontendUrl_Throws()
    {
        var values = ValidSmtp();
        values.Remove("FRONTEND_BASE_URL"); // falls back to http://localhost:3000
        var act = () => EmailStartupValidation.Validate(Config(values), isProduction: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*FRONTEND_BASE_URL*");
    }

    [Fact]
    public void SmtpInProduction_LocalhostOnlyRejectedInProduction()
    {
        var values = ValidSmtp("http://localhost:3000");
        var act = () => EmailStartupValidation.Validate(Config(values), isProduction: false);
        act.Should().NotThrow();
    }

    [Fact]
    public void SmtpWithNonHttpFrontendUrl_Throws()
    {
        var values = ValidSmtp("ftp://invoiceflow.app");
        var act = () => EmailStartupValidation.Validate(Config(values), isProduction: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*http(s)*");
    }

    // ── ResolveFrontendBaseUrl precedence ─────────────────────────────────────

    // The env var must beat the App:FrontendBaseUrl default baked into
    // appsettings.json — the old precedence made FRONTEND_BASE_URL dead config.
    [Fact]
    public void ResolveFrontendBaseUrl_EnvVarWinsOverAppSetting()
    {
        var config = Config(new()
        {
            ["App:FrontendBaseUrl"] = "http://localhost:3000",
            ["FRONTEND_BASE_URL"] = "https://app.invoiceflow.example"
        });
        EmailStartupValidation.ResolveFrontendBaseUrl(config)
            .Should().Be("https://app.invoiceflow.example");
    }

    [Fact]
    public void ResolveFrontendBaseUrl_FallsBackToAppSetting_AndTrimsTrailingSlash()
    {
        var config = Config(new() { ["App:FrontendBaseUrl"] = "https://app.invoiceflow.test/" });
        EmailStartupValidation.ResolveFrontendBaseUrl(config)
            .Should().Be("https://app.invoiceflow.test");
    }

    [Fact]
    public void ResolveFrontendBaseUrl_DefaultsToLocalhost()
    {
        EmailStartupValidation.ResolveFrontendBaseUrl(Config([]))
            .Should().Be("http://localhost:3000");
    }
}
