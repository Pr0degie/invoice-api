# ADR 0002 — Invoice immutability, sequential numbering, and PDF archiving

Status: accepted · Date: 2026-07-02 · Scope: invoice-api (Prompt 12 Part B)

## Context

The owner operates a registered German sole proprietorship under the
Kleinunternehmerregelung (§ 19 UStG). Invoices must carry the Pflichtangaben of
§ 14 Abs. 4 UStG and satisfy basic GoBD expectations: finalized invoices are
immutable, invoice numbers are sequential and unique, and the rendered PDF is
archived as-is. The previous model assigned numbers at creation, allowed a
`Draft → Sent → Paid/Overdue/Cancelled` workflow with a stored `Overdue`
status, and re-rendered PDFs from live data on every download.

## Decisions

### 1. Status model: `Draft → Finalized → Paid`, `Finalized → Cancelled` (Storno only)

- **`Finalized` replaces `Sent`.** The legally meaningful event is fixation
  (number assigned, content frozen), not the act of sending.
- **`Overdue` is no longer stored.** Overdue-ness is a function of the calendar
  (`Finalized ∧ DueDate < today ∧ unpaid`), not a state someone transitions
  into. It is exposed as a derived `isOverdue` flag and a virtual `Overdue`
  list filter. This removes an entire class of stale-state bugs.
- `PATCH /status` only covers `Finalized ⇄ Paid`. Finalization goes through
  `POST /{id}/finalize` (which enforces the legal preconditions); cancellation
  goes through `POST /{id}/cancel` (which issues the Stornorechnung). Neither
  state is reachable by plain status assignment.
- Migration remaps stored ints: `Sent(1) → Finalized(1)`, `Overdue(3) →
  Finalized(1)`, `Cancelled(4) → Cancelled(3)`.

### 2. Numbers assigned at finalization, atomically, per user and year

- `Number` is `NULL` while Draft ("Entwurf" in the UI). Format after
  finalization: `{year}-{counter:000}` (e.g. `2026-001`), counter resets per
  year, scoped per user.
- Source of truth is an `InvoiceNumberSequences (UserId, Year, Counter)` table.
  `Counter` doubles as an EF concurrency token: two concurrent finalizations
  conflict on `SaveChanges`, the loser retries with the next number (max 5
  attempts). The unique index on `(UserId, Number)` remains as a hard backstop.
  This works identically on Postgres and the EF InMemory test provider —
  `SELECT … FOR UPDATE` would not.
- Numbers are never reused; only Drafts (which have no number) can be deleted.
- Pre-existing invoices keep their legacy `INV-{year}-{NNNN}` numbers — a
  number, once issued, never changes. The formats cannot collide.

### 3. Corrections via Stornorechnung, never edits

`POST /{id}/cancel` creates a `Cancellation`-type invoice: own sequential
number, negated line items, snapshot reference to the original number
("Stornorechnung zu Rechnung 2026-003"), immediately `Finalized`. The original
becomes `Cancelled` (terminal). Paid invoices must first be un-marked
(`Paid → Finalized`) — cancelling a paid invoice implies a refund decision the
system should not take implicitly. A correction = Storno + new invoice.

### 4. PDFs archived as DB blobs at finalization

- The PDF is rendered **once**, at finalization (and at Storno creation), and
  stored in `InvoicePdfs (InvoiceId PK, Data bytea)`. `GET /{id}/pdf` serves
  the archived bytes for finalized invoices — never a re-render, so later
  changes to settings, layout code, or fonts cannot alter issued documents
  (GoBD).
- **Why a DB blob and not file storage:** the API deploys to Railway, whose
  container filesystem is ephemeral — files would silently vanish on redeploy.
  *(Update 2026-07-05: deploy target is now Coolify on Hetzner — container
  filesystems are equally ephemeral there, the reasoning is unchanged.)*
  A blob column keeps the archive inside the existing backup/restore unit
  (Postgres), keeps user-isolation trivial, and needs no new infrastructure.
  At one PDF (~40 KB) per finalized invoice of a single-tenant freelancer
  tool, blob size is a non-issue. Revisit (S3-compatible object storage) only
  if multi-tenant scale ever makes the DB the bottleneck.
- The blob lives in its own table so list/detail queries never touch it.
- Drafts render a live preview with an ENTWURF watermark (no archival).
  Invoices finalized before this feature have no archived PDF; the first
  download renders and persists one (backfill).
- § 19 UStG: at finalization the user's `IsSmallBusiness` flag is snapshotted
  onto the invoice, `TaxRate` is forced to 0, and the PDF carries the verbatim
  sentence "Gemäß § 19 UStG wird keine Umsatzsteuer berechnet." instead of a
  VAT line. Switching to Regelbesteuerung later is a settings toggle; already
  finalized invoices keep their snapshot.

## Consequences

- The frontend must treat `number` as nullable, use `finalize`/`cancel`
  endpoints instead of status PATches for those transitions, and derive
  overdue display from `isOverdue`.
- Stats semantics changed: `sentCount → finalizedCount`, `overdueCount` is
  due-date-derived, Cancellation invoices are excluded from aggregates.
- The Docker image must ship fonts (fontconfig + fonts-liberation); without
  them SkiaSharp renders text-less PDFs. Discovered during this work — the
  old on-the-fly PDFs had the same defect in containers.
