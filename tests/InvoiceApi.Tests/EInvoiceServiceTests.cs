using FluentAssertions;
using InvoiceApi.Models;
using InvoiceApi.Services;
using s2industries.ZUGFeRD;
using CiiInvoiceType = s2industries.ZUGFeRD.InvoiceType;
using ModelInvoiceType = InvoiceApi.Models.InvoiceType;

namespace InvoiceApi.Tests;

// Verifies the generated XRechnung (EN 16931, CII) by re-loading it via
// InvoiceDescriptor.Load and asserting the resulting BT fields — robust to XML
// formatting/attribute ordering across library versions. Final legal validation is
// manual via the KoSIT validator / ELSTER viewer (see ADR 0004); these tests guard
// the mapping (tax category, document type, references, amount consistency).
public class EInvoiceServiceTests
{
    private readonly EInvoiceService _sut = new();

    private static InvoiceDescriptor Roundtrip(byte[] xml)
    {
        using var stream = new MemoryStream(xml);
        return InvoiceDescriptor.Load(stream);
    }

    [Fact]
    public void Kleinunternehmer_UsesCategoryE_ZeroVat_WithExemptionText()
    {
        var (invoice, user) = Build(smallBusiness: true, taxRate: 0m);

        var descriptor = Roundtrip(_sut.Generate(invoice, user));

        descriptor.Type.Should().Be(CiiInvoiceType.Invoice); // BT-3 = 380
        descriptor.InvoiceNo.Should().Be("2026-001");

        var tax = descriptor.Taxes.Should().ContainSingle().Subject;
        tax.CategoryCode.Should().Be(TaxCategoryCodes.E);
        tax.Percent.Should().Be(0m);
        tax.TaxAmount.Should().Be(0m);
        tax.ExemptionReason.Should().Contain("§ 19 UStG");

        // § 19: no VAT, grand total == net subtotal.
        descriptor.LineTotalAmount.Should().Be(210m);
        descriptor.TaxTotalAmount.Should().Be(0m);
        descriptor.GrandTotalAmount.Should().Be(210m);
    }

    [Fact]
    public void Regelbesteuerung_UsesCategoryS_WithVatBreakdown()
    {
        var (invoice, user) = Build(smallBusiness: false, taxRate: 0.19m);

        var descriptor = Roundtrip(_sut.Generate(invoice, user));

        descriptor.Type.Should().Be(CiiInvoiceType.Invoice);

        var tax = descriptor.Taxes.Should().ContainSingle().Subject;
        tax.CategoryCode.Should().Be(TaxCategoryCodes.S);
        tax.Percent.Should().Be(19m);
        tax.TaxAmount.Should().Be(39.90m);

        descriptor.LineTotalAmount.Should().Be(210m);
        descriptor.TaxTotalAmount.Should().Be(39.90m);
        descriptor.GrandTotalAmount.Should().Be(249.90m);

        // Amounts equal the invoice's own computation (PDF ↔ XML consistency).
        descriptor.LineTotalAmount.Should().Be(invoice.Subtotal);
        descriptor.TaxTotalAmount.Should().Be(invoice.TaxAmount);
        descriptor.GrandTotalAmount.Should().Be(invoice.Total);
    }

    [Fact]
    public void Storno_IsType384_ReferencesOriginal_NegativeTotals_PositiveUnitPrices()
    {
        var (invoice, user) = Build(smallBusiness: false, taxRate: 0.19m, storno: true);

        var descriptor = Roundtrip(_sut.Generate(invoice, user));

        // BT-3 = 384 (Corrected invoice), per KoSIT #23.
        descriptor.Type.Should().Be(CiiInvoiceType.Correction);

        // BT-25 preceding invoice reference points at the cancelled original.
        descriptor._InvoiceReferencedDocuments.Should()
            .ContainSingle(d => d.ID == "2026-000");

        // Document total is negative (quantities negated) …
        descriptor.GrandTotalAmount.Should().BeLessThan(0m);
        descriptor.LineTotalAmount.Should().Be(-210m);
        descriptor.TaxTotalAmount.Should().Be(-39.90m);

        // … but no unit price is negative (BR-27): only the quantity carries the sign.
        descriptor.TradeLineItems.Should().OnlyContain(li => li.NetUnitPrice >= 0m);
        descriptor.TradeLineItems.Should().Contain(li => li.BilledQuantity < 0m);
    }

    [Fact]
    public void Generate_MapsSellerAndBuyerElectronicAddresses_AndBuyerReference()
    {
        var (invoice, user) = Build(smallBusiness: false, taxRate: 0.19m);
        invoice.BuyerReference = "PO-4711";

        var descriptor = Roundtrip(_sut.Generate(invoice, user));

        descriptor.ReferenceOrderNo.Should().Be("PO-4711"); // BT-10
        descriptor.SellerElectronicAddress.Address.Should().Be(user.Email);
        descriptor.BuyerElectronicAddress.Address.Should().Be(invoice.RecipientEmail);
    }

    // Deterministic fixture: fixed number + dates so amounts/refs are stable.
    private static (Invoice, User) Build(bool smallBusiness, decimal taxRate, bool storno = false)
    {
        var sign = storno ? -1m : 1m;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "tobias@invoiceflow.example",
            PasswordHash = "hash",
            Name = "Tobias Dev",
            TaxNumber = "143/262/12345",
            VatId = "DE123456789",
            IsSmallBusiness = smallBusiness,
            Street = "Musterstraße 1",
            PostalCode = "80331",
            City = "München",
            Country = "Deutschland",
            Phone = "+49 89 1234567",
            Iban = "DE89370400440532013000",
            Bic = "COBADEFFXXX",
            BankName = "Commerzbank",
        };

        var invoice = new Invoice
        {
            Number = storno ? "2026-002" : "2026-001",
            Type = storno ? ModelInvoiceType.Cancellation : ModelInvoiceType.Invoice,
            Status = InvoiceStatus.Finalized,
            SenderName = "Tobias Dev",
            SenderAddress = "Musterstraße 1\n80331 München",
            RecipientName = "ACME GmbH",
            RecipientAddress = "Testweg 5\n10115 Berlin",
            RecipientStreet = "Testweg 5",
            RecipientPostalCode = "10115",
            RecipientCity = "Berlin",
            RecipientCountryCode = "DE",
            RecipientEmail = "rechnung@acme.example",
            BuyerReference = "-",
            IssueDate = new DateOnly(2026, 3, 1),
            DueDate = new DateOnly(2026, 3, 15),
            ServiceDate = new DateOnly(2026, 2, 25),
            TaxRate = smallBusiness ? 0m : taxRate,
            IsSmallBusiness = smallBusiness,
            Currency = "EUR",
            CancellationOfNumber = storno ? "2026-000" : null,
            LineItems =
            [
                new() { Description = "Web Development", Quantity = 2m * sign, UnitPrice = 80m, Unit = "h", Position = 0 },
                new() { Description = "Project Setup", Quantity = 1m * sign, UnitPrice = 50m, Unit = "flat", Position = 1 },
            ],
        };

        return (invoice, user);
    }
}
