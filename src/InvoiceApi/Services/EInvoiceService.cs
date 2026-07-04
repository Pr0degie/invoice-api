using InvoiceApi.Models;
using s2industries.ZUGFeRD;
using ModelInvoiceType = InvoiceApi.Models.InvoiceType;
using CiiInvoiceType = s2industries.ZUGFeRD.InvoiceType;

namespace InvoiceApi.Services;

public interface IEInvoiceService
{
    // Produces the legally binding XRechnung (EN 16931, CII syntax, pure XML) for a
    // finalized invoice. Seller data is taken from the User profile, buyer data from
    // the invoice's structured recipient fields — both must be complete (enforced by
    // the finalize preconditions in InvoiceService).
    byte[] Generate(Invoice invoice, User user);
}

// XRechnung 3.0 (EN 16931) generator built on ZUGFeRD-csharp. Emits pure CII XML —
// the QuestPDF document stays the human-readable companion. Kept deliberately in
// lockstep with PdfService's amount logic (§ 19 → 0 % / category E, effective tax
// rate) so the structured and visual parts of the same invoice never disagree.
public class EInvoiceService : IEInvoiceService
{
    private const string SmallBusinessExemptionReason =
        "Gemäß § 19 UStG wird keine Umsatzsteuer berechnet.";

    public byte[] Generate(Invoice invoice, User user)
    {
        // Finalized invoices carry an immutable § 19 snapshot; drives 0 % vs regular VAT.
        var smallBusiness = invoice.IsSmallBusiness;
        var taxRate = smallBusiness ? 0m : invoice.TaxRate;
        var taxPercent = Math.Round(taxRate * 100m, 2);
        var taxCategory = smallBusiness ? TaxCategoryCodes.E : TaxCategoryCodes.S;

        var net = invoice.Subtotal;
        var tax = Math.Round(net * taxRate, 2);
        var total = net + tax;

        var descriptor = InvoiceDescriptor.CreateInvoice(
            invoice.Number ?? throw new InvalidOperationException("Invoice has no number — cannot emit E-Rechnung for a draft."),
            invoice.IssueDate.ToDateTime(TimeOnly.MinValue),
            CurrencyCodes.EUR);

        // BT-3 document type: 384 (Corrected invoice) for Storno, else 380 (Invoice).
        descriptor.Type = invoice.Type == ModelInvoiceType.Cancellation
            ? CiiInvoiceType.Correction
            : CiiInvoiceType.Invoice;

        // BT-10 buyer reference is mandatory in XRechnung (BR-DE-15); finalize defaults it to "-".
        descriptor.ReferenceOrderNo = string.IsNullOrWhiteSpace(invoice.BuyerReference)
            ? "-"
            : invoice.BuyerReference;

        // BT-25 preceding invoice reference — a Storno points at the original it reverses.
        if (invoice.Type == ModelInvoiceType.Cancellation && !string.IsNullOrWhiteSpace(invoice.CancellationOfNumber))
            descriptor.AddInvoiceReferencedDocument(invoice.CancellationOfNumber!);

        // --- Seller (from the User profile) ---
        descriptor.SetSeller(
            name: invoice.SenderName,
            postcode: user.PostalCode ?? "",
            city: user.City ?? "",
            street: user.Street ?? "",
            country: MapCountry(user.Country));

        // BR-DE-16: at least one of USt-IdNr. (VA) or Steuernummer (FC).
        if (!string.IsNullOrWhiteSpace(user.VatId))
            descriptor.AddSellerTaxRegistration(user.VatId, TaxRegistrationSchemeID.VA);
        if (!string.IsNullOrWhiteSpace(user.TaxNumber))
            descriptor.AddSellerTaxRegistration(user.TaxNumber, TaxRegistrationSchemeID.FC);

        // BT-34 seller electronic address + BG-6 seller contact (BR-DE-2..7).
        descriptor.SetSellerElectronicAddress(user.Email, ElectronicAddressSchemeIdentifiers.ElectronicMailSmtp);
        descriptor.SetSellerContact(name: user.Name, phoneno: user.Phone, emailAddress: user.Email);

        // --- Buyer (structured recipient fields on the invoice) ---
        descriptor.SetBuyer(
            name: invoice.RecipientName,
            postcode: invoice.RecipientPostalCode ?? "",
            city: invoice.RecipientCity ?? "",
            street: invoice.RecipientStreet ?? "",
            country: MapCountry(invoice.RecipientCountryCode));

        // BT-49 buyer electronic address (mandatory; finalize precondition).
        descriptor.SetBuyerElectronicAddress(invoice.RecipientEmail ?? "", ElectronicAddressSchemeIdentifiers.ElectronicMailSmtp);
        if (!string.IsNullOrWhiteSpace(invoice.RecipientVatId))
            descriptor.AddBuyerTaxRegistration(invoice.RecipientVatId, TaxRegistrationSchemeID.VA);

        // --- Line items ---
        // FlatRate is a display-only concern (PDF). The XML always carries the real
        // stored quantity/unit/price. Storno keeps negative quantity, positive unit
        // price (BR-27 forbids only a negative unit price).
        foreach (var item in invoice.LineItems.OrderBy(li => li.Position))
        {
            descriptor.AddTradeLineItem(
                name: item.Description,
                netUnitPrice: item.UnitPrice,
                unitCode: MapUnit(item.Unit),
                billedQuantity: item.Quantity,
                taxType: TaxTypes.VAT,
                categoryCode: taxCategory,
                taxPercent: taxPercent);
        }

        // --- Tax breakdown (BG-23) ---
        descriptor.AddApplicableTradeTax(
            basisAmount: net,
            percent: taxPercent,
            taxAmount: tax,
            typeCode: TaxTypes.VAT,
            categoryCode: taxCategory,
            exemptionReason: smallBusiness ? SmallBusinessExemptionReason : null);

        // --- Payment (BG-16 / BR-DE-1) ---
        var paymentText = invoice.Type == ModelInvoiceType.Cancellation
            ? "Der Betrag wird entsprechend verrechnet bzw. erstattet."
            : $"Zahlbar ohne Abzug bis {invoice.DueDate:dd.MM.yyyy}.";
        descriptor.AddTradePaymentTerms(paymentText, invoice.DueDate.ToDateTime(TimeOnly.MinValue));

        if (!string.IsNullOrWhiteSpace(user.Iban))
        {
            descriptor.SetPaymentMeans(PaymentMeansTypeCodes.SEPACreditTransfer);
            descriptor.AddCreditorFinancialAccount(user.Iban, user.Bic ?? "", null, null, user.BankName, invoice.SenderName);
        }
        else
        {
            // No IBAN on file — undefined credit-transfer instrument. IBAN is
            // recommended for full validity (see ADR 0004 / manual KoSIT check).
            descriptor.SetPaymentMeans(PaymentMeansTypeCodes.CreditTransferNonSEPA);
        }

        // --- Totals (BG-22) --- Storno amounts are already negative (negated quantities).
        descriptor.SetTotals(
            lineTotalAmount: net,
            taxBasisAmount: net,
            taxTotalAmount: tax,
            grandTotalAmount: total,
            duePayableAmount: total);

        using var stream = new MemoryStream();
        descriptor.Save(stream, ZUGFeRDVersion.Version23, Profile.XRechnung, ZUGFeRDFormats.CII);
        return stream.ToArray();
    }

    // Internal Unit codes → UN/ECE Rec 20 quantity codes.
    private static QuantityCodes MapUnit(string unit) => unit switch
    {
        "h" => QuantityCodes.HUR,
        "day" => QuantityCodes.DAY,
        "piece" => QuantityCodes.H87,
        "flat" => QuantityCodes.C62,
        _ => QuantityCodes.C62,
    };

    // Free-text / ISO country → CountryCodes; defaults to DE (DACH freelancer focus).
    private static CountryCodes MapCountry(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return CountryCodes.DE;

        switch (country.Trim().ToUpperInvariant())
        {
            case "DE":
            case "DEUTSCHLAND":
            case "GERMANY":
                return CountryCodes.DE;
            case "AT":
            case "ÖSTERREICH":
            case "OESTERREICH":
            case "AUSTRIA":
                return CountryCodes.AT;
            case "CH":
            case "SCHWEIZ":
            case "SWITZERLAND":
                return CountryCodes.CH;
        }

        return Enum.TryParse<CountryCodes>(country.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : CountryCodes.DE;
    }
}
