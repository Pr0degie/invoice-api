using System.ComponentModel.DataAnnotations;
using System.Reflection;
using FluentAssertions;
using InvoiceApi.Models;
using InvoiceApi.Models.Dtos;

namespace InvoiceApi.Tests;

// DTO limits are the only size bound on user input: the text columns are unbounded
// in Postgres, and a draft's PDF is rendered on every download — without limits one
// self-registered account can pin the CPU/RAM of the shared box.
public class RequestValidationTests
{
    [Fact]
    public void Invoice_WithinAllLimits_IsValid()
    {
        var request = Invoice() with
        {
            SenderName = new string('a', 200),
            SenderAddress = new string('a', 500),
            RecipientName = new string('a', 200),
            RecipientAddress = new string('a', 500),
            RecipientStreet = new string('a', 200),
            RecipientPostalCode = new string('1', 20),
            RecipientCity = new string('a', 100),
            RecipientVatId = new string('a', 20),
            BuyerReference = new string('a', 50),
            Notes = new string('a', 4000),
            LineItems = Enumerable.Range(0, 200).Select(_ => LineItem()).ToList(),
        };

        InvalidMembers(request).Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(CreateInvoiceRequest.SenderName), 201)]
    [InlineData(nameof(CreateInvoiceRequest.SenderAddress), 501)]
    [InlineData(nameof(CreateInvoiceRequest.RecipientName), 201)]
    [InlineData(nameof(CreateInvoiceRequest.RecipientAddress), 501)]
    [InlineData(nameof(CreateInvoiceRequest.RecipientStreet), 201)]
    [InlineData(nameof(CreateInvoiceRequest.RecipientPostalCode), 21)]
    [InlineData(nameof(CreateInvoiceRequest.RecipientCity), 101)]
    [InlineData(nameof(CreateInvoiceRequest.RecipientCountryCode), 3)]
    [InlineData(nameof(CreateInvoiceRequest.RecipientVatId), 21)]
    [InlineData(nameof(CreateInvoiceRequest.BuyerReference), 51)]
    [InlineData(nameof(CreateInvoiceRequest.Currency), 4)]
    [InlineData(nameof(CreateInvoiceRequest.Notes), 4001)]
    public void Invoice_TextFieldOverLimit_IsRejected(string member, int length)
    {
        var request = Invoice();
        typeof(CreateInvoiceRequest).GetProperty(member)!.SetValue(request, new string('A', length));

        InvalidMembers(request).Should().Contain(member);
    }

    [Fact]
    public void Invoice_RecipientEmailOverLimit_IsRejected()
    {
        var request = Invoice() with { RecipientEmail = new string('a', 245) + "@example.com" };

        InvalidMembers(request).Should().Contain(nameof(CreateInvoiceRequest.RecipientEmail));
    }

    [Fact]
    public void Invoice_MoreThan200LineItems_IsRejected()
    {
        var request = Invoice() with { LineItems = Enumerable.Range(0, 201).Select(_ => LineItem()).ToList() };

        InvalidMembers(request).Should().Contain(nameof(CreateInvoiceRequest.LineItems));
    }

    [Fact]
    public void LineItem_WithinAllLimits_IsValid()
    {
        var item = LineItem() with
        {
            Description = new string('a', 2000),
            Unit = new string('a', 20),
            Quantity = 1_000_000m,
            UnitPrice = 10_000_000m,
        };

        InvalidMembers(item).Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(CreateLineItemRequest.Description), 2001)]
    [InlineData(nameof(CreateLineItemRequest.Unit), 21)]
    public void LineItem_TextFieldOverLimit_IsRejected(string member, int length)
    {
        var item = LineItem();
        typeof(CreateLineItemRequest).GetProperty(member)!.SetValue(item, new string('A', length));

        InvalidMembers(item).Should().Contain(member);
    }

    [Fact]
    public void LineItem_QuantityAboveOneMillion_IsRejected()
    {
        InvalidMembers(LineItem() with { Quantity = 1_000_000.001m })
            .Should().Contain(nameof(CreateLineItemRequest.Quantity));
    }

    [Fact]
    public void LineItem_UnitPriceAboveTenMillion_IsRejected()
    {
        // decimal.MaxValue used to pass [Range(0, double.MaxValue)] and overflowed in Quantity * UnitPrice
        InvalidMembers(LineItem() with { UnitPrice = decimal.MaxValue })
            .Should().Contain(nameof(CreateLineItemRequest.UnitPrice));
    }

    [Fact]
    public void LineItem_FractionalQuantity_IsValid()
    {
        // Guards the invariant-culture parsing of the decimal bounds ("0.001" is 1 in de-DE)
        InvalidMembers(LineItem() with { Quantity = 0.5m }).Should().BeEmpty();
    }

    [Fact]
    public void Register_NameOrEmailOverLimit_IsRejected()
    {
        var dto = new RegisterDto(new string('a', 245) + "@example.com", "Password123!", new string('a', 201));

        InvalidMembers(dto).Should().Contain([nameof(RegisterDto.Email), nameof(RegisterDto.Name)]);
    }

    [Fact]
    public void Login_OversizedEmailOrPassword_IsRejected()
    {
        var dto = new LoginDto(new string('a', 245) + "@example.com", new string('a', 129));

        InvalidMembers(dto).Should().Contain([nameof(LoginDto.Email), nameof(LoginDto.Password)]);
    }

    [Fact]
    public void TokenDtos_OversizedToken_IsRejected()
    {
        var token = new string('a', 257);

        InvalidMembers(new RefreshRequestDto(token)).Should().Contain(nameof(RefreshRequestDto.RefreshToken));
        InvalidMembers(new VerifyEmailDto(token)).Should().Contain(nameof(VerifyEmailDto.Token));
        InvalidMembers(new ResetPasswordDto(token, "Password123!")).Should().Contain(nameof(ResetPasswordDto.Token));
    }

    private static CreateInvoiceRequest Invoice() => new()
    {
        SenderName = "Tobias Dev",
        SenderAddress = "Musterstraße 1, 80331 München",
        RecipientName = "ACME GmbH",
        RecipientEmail = "rechnung@acme.example",
        LineItems = [LineItem()],
    };

    private static CreateLineItemRequest LineItem() => new()
    {
        Description = "Web Development",
        Quantity = 2,
        UnitPrice = 80m,
        Unit = "h",
    };

    // Mirrors MVC model validation for one object. MVC also descends into LineItems
    // (Validator.TryValidateObject doesn't, so line items are validated on their own),
    // and for positional records it reads the attributes off the constructor
    // parameters, which Validator.TryValidateObject never sees.
    private static List<string> InvalidMembers(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        var invalid = results.SelectMany(r => r.MemberNames).ToList();

        var type = dto.GetType();
        var primaryCtor = type.GetConstructors().MaxBy(c => c.GetParameters().Length)!;
        foreach (var parameter in primaryCtor.GetParameters())
        {
            var attributes = parameter.GetCustomAttributes<ValidationAttribute>();
            var value = type.GetProperty(parameter.Name!)!.GetValue(dto);
            var context = new ValidationContext(dto) { MemberName = parameter.Name };
            if (!Validator.TryValidateValue(value, context, [], attributes))
                invalid.Add(parameter.Name!);
        }

        return invalid;
    }
}
