using FluentAssertions;
using InvoiceApi.Services;

namespace InvoiceApi.Tests;

public class LogRedactionTests
{
    [Theory]
    [InlineData("tobias@example.com", "t***@example.com")]
    [InlineData("a@example.com", "a***@example.com")]
    [InlineData("Max.Mustermann@firma.de", "M***@firma.de")]
    public void MaskEmail_ShouldKeepOnlyFirstCharacterAndDomain(string email, string expected)
        => LogRedaction.MaskEmail(email).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("@example.com")]
    public void MaskEmail_ShouldNotEchoInputItCannotParse(string input)
        => LogRedaction.MaskEmail(input).Should().Be("***");
}
