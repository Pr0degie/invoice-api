# ADR 0003 — Reopening finalized invoices before dispatch

Status: accepted · Date: 2026-07-04 · Scope: invoice-api + invoiceflow (Prompt 13 Part C)

## Context

ADR 0002 fixed the rule: finalized invoices are immutable, corrections go
through Storno + new invoice. In practice a common case sits before that rule
kicks in economically: the freelancer finalizes, immediately spots a typo or a
wrong line item, and the invoice **has not left the house yet**. Forcing a
Storno here pollutes the books with a cancellation pair for a document no
recipient has ever seen, and burns two sequence numbers for one deliverable.

GoBD's Unveränderbarkeit protects documents that are (or may be) in
circulation. A fixation that was never communicated can be lifted, provided
the lift itself is documented and the numbering stays intact.

## Decision

`POST /api/invoices/{id}/reopen` resets a `Finalized` invoice to `Draft` as a
**deliberate, audited exception** — intended solely for corrections before the
invoice is sent. The general immutability rule stands: there is still no
direct editing of finalized invoices, and the endpoint is guarded:

- Only `Finalized` → `Draft`. A draft returns 400; `Paid` and `Cancelled`
  return 409 — a paid or cancelled invoice has demonstrably been in
  circulation, so the only correction path is Storno + new invoice.
  Cancellation invoices (Storno) can never be reopened (409).
- The UI requires an explicit confirmation that the invoice has not been sent
  to the recipient before the action is enabled (checkbox in the dialog),
  and points to Storno for anything already dispatched.

### Numbering: the invoice keeps its number

The assigned `Number` stays on the reopened draft; `InvoiceNumberSequence` is
not touched (no decrement, no give-back). Re-finalizing **reuses the existing
number** instead of drawing a new one. Consequences:

- No gap: the number was issued and remains issued to the same deliverable.
- No double assignment: the unique `(UserId, Number)` slot never frees up.
- The sequence stays strictly monotonic — a reopen/re-finalize cycle is
  invisible to it.
- Edge case, documented on purpose: if a reopened invoice is re-finalized in a
  later **year**, it keeps its original year-prefixed number. The number is an
  identity, not a date claim; the Ausstellungsdatum on the document is what
  carries legal meaning.
- A reopened draft **cannot be deleted** (409) — deleting it would tear a gap
  into the sequence (ADR 0002: numbers are never reused). The only ways
  forward are re-finalizing, or re-finalizing + Storno.

### Archived PDF

The archived `InvoicePdf` is deleted at reopen (it no longer matches a mutable
draft) and re-archived at re-finalization. The archive therefore always shows
the state that was (re-)fixed — never a stale render. The GoBD principle "the
PDF handed out is the PDF stored" is preserved because, by the user's own
confirmation, no PDF was handed out.

### Audit trail

Every reopen writes an `InvoiceAuditEntry` (`InvoiceId`, `UserId`,
`Action = "Reopened"`, `Timestamp`, `Note`) into an **append-only** table —
application code only ever inserts. This is the GoBD record that fixation was
lifted, when, and by whom. Entries survive the invoice's whole life; since
numbered invoices cannot be deleted, the FK cascade is unreachable in
practice.

## Alternatives considered

- **Storno-only (status quo):** clean on paper, but produces meaningless
  cancellation pairs for never-sent documents and doubles sequence
  consumption. Rejected for the pre-dispatch case.
- **Give the number back / decrement the sequence:** creates real gap and
  race hazards (another finalization may already have drawn the next number)
  for zero benefit. Rejected.
- **New number at re-finalization:** leaves a permanent gap at the old number
  and turns the audit story from "same document, corrected before dispatch"
  into "document vanished". Rejected.

## Consequences

- `DeleteAsync` gained a guard: drafts with a `Number` (= reopened) are not
  deletable. This slightly tightens ADR 0002's "only drafts can be deleted".
- `FinalizeAsync` no longer assumes it always draws a number; the
  number-reserving retry loop is skipped when a number exists.
- Frontend: detail view offers "Reopen for editing" on finalized invoices
  behind an AlertDialog with a mandatory "not sent yet" confirmation;
  after reopening it navigates straight to the edit form.
- The trust model is honesty-based: the system cannot verify dispatch. The
  checkbox confirmation plus the audit trail put the responsibility (and the
  evidence) with the user — which is exactly where GoBD places it.
