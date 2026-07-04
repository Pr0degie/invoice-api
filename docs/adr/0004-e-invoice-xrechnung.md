# ADR 0004 — E-Rechnung (XRechnung / EN 16931) generation

Status: accepted · Date: 2026-07-04 · Scope: invoice-api + invoiceflow (Prompt 13)

## Context

German B2B/B2G invoicing law now requires a structured **E-Rechnung** (EN 16931)
alongside — or instead of — the human-readable PDF. InvoiceFlow already archives
an immutable PDF per finalized invoice (ADR 0002); it must additionally emit a
**legally valid German E-Rechnung** for every finalized invoice, archived the
same way (GoBD / § 14b — the structured part preserved unaltered for the
retention period).

## Decisions

### Format: XRechnung, pure CII XML (no hybrid ZUGFeRD PDF/A-3)

We emit **XRechnung 3.0 (EN 16931, CII syntax) as pure XML** via the
`ZUGFeRD-csharp` NuGet package (v18), not a hybrid ZUGFeRD PDF/A-3. Reasons:

- QuestPDF (our PDF engine, 2024.3.0) does not produce PDF/A-3, which a hybrid
  container requires. Retrofitting a second PDF pipeline just to embed the XML
  is disproportionate.
- Plain XRechnung XML is fully compliant on its own. The PDF stays the
  human-readable companion; the XML is the legally binding structured document.
- A hybrid PDF/A-3 (visual + embedded XML in one file) remains a clean future
  step if a recipient ever demands it — the XML generator is already in place.

The XML is generated **once at finalization** and stored in a dedicated
`InvoiceXml` table (InvoiceId PK, `byte[] Data`, `CreatedAt`) that mirrors
`InvoicePdf`. It is never re-rendered from live data; reopening (ADR 0003)
discards it, and re-finalization re-archives it.

### § 19 Kleinunternehmer → tax category `E` + free-text exemption reason

Small-business invoices carry VAT category **`E` (Exempt)**, rate 0, and the
BT-120 free-text exemption reason **"Gemäß § 19 UStG wird keine Umsatzsteuer
berechnet."** (the same wording as the PDF). There is no VATEX code for § 19, so
`exemptionReasonCode` is left null; EN 16931 rule BR-E-10 is satisfied by the
free text. Backed by KoSIT issue #32.

### Storno → document type 384 (Corrected invoice) + negative amounts

A cancellation (Storno) is emitted as BT-3 type **384 (Corrected invoice)** with
**negative amounts** (quantities negated, unit prices kept ≥ 0), and references
the cancelled original in **BT-25** (preceding invoice reference). Backed by
KoSIT issue #23 (cancelling a *specific* invoice ⇒ 384, not 381) and the
e-rechnung-bund FAQ.

- Rejected alternative: **381 (Credit note) + positive amounts**. It breaks the
  sign consistency between the PDF (which shows negatives) and the XML.
- No KoSIT sign rule forbids negative document totals (validator-config #71/#58),
  so the negatives pass. BR-27 forbids only a negative *unit price* — we negate
  the quantity, not the price, so it holds.

### Buyer electronic address (BT-49) is a hard finalization precondition

An invoice cannot be finalized without a **recipient email (BT-49)** and a
structured recipient postal address (street + postal code + city, BR-DE-8/9).
The seller profile must additionally carry a **phone (BT-42)** for the mandatory
seller contact (BR-DE-2..7). Missing data → 409 naming exactly what's missing.
`BuyerReference` (BT-10, BR-DE-15) defaults to `"-"` when empty; buyer country
defaults to `DE`.

### Seller data from the User profile, buyer data on the invoice

Seller party (name, structured address, USt-IdNr./Steuernummer, IBAN, contact)
comes from the `User` profile at finalization and is snapshotted into the
archived XML. Structured recipient data lives on the invoice (new nullable
columns), composed into the legacy free-text `RecipientAddress` for the PDF.

## Consequences

- Legacy invoices finalized before this feature have no structured recipient
  data. `GET /api/invoices/{id}/xml` backfills on demand **only** when the
  structured data is present; otherwise it returns 409 naming the gap.
- All new columns are nullable (append-only migration
  `AddEInvoiceXRechnungSupport`); existing rows are unaffected.

## Validation

Automated tests (`EInvoiceServiceTests`) re-load the generated XML via
`InvoiceDescriptor.Load` and assert the BT fields (type code, tax category,
exemption text, BT-25 reference, amount consistency) — robust to XML formatting.

**Final legal validation is manual**: run each case (Kleinunternehmer,
Regelbesteuerung, Storno) through the **KoSIT validator** or the **ELSTER
E-Rechnung viewer** (https://e-rechnung.elster.de) — must pass without errors,
with the § 19 exemption visible and totals matching the PDF.

## Citations

- KoSIT #23 (384 vs 381 vs 389): https://projekte.kosit.org/xrechnung/xrechnung/-/issues/23
- KoSIT #32 (§ 19 category E): https://projekte.kosit.org/xrechnung/xrechnung/-/issues/32
- KoSIT validator-config #71/#58 (no sign rule): https://projekte.kosit.org/xrechnung/xrechnung/-/issues/71
- e-rechnung-bund FAQ (Gutschriften/Korrekturen): https://e-rechnung-bund.de/faq/wie-sind-gutschriften-und-rechnungskorrekturen-anzugeben/
- ZUGFeRD-csharp: https://github.com/stephanstapel/ZUGFeRD-csharp
