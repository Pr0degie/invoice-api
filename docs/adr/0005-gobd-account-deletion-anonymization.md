# ADR 0005 — GoBD-konforme Account-Löschung: Anonymisierung statt Hard-Delete

Status: accepted · Date: 2026-07-05 · Scope: invoice-api (Prompt 15)

## Context

`DELETE /api/auth/me` deleted the user row and cascaded into **all** invoices —
including finalized ones and their archived PDFs/XMLs. That collides head-on
with § 147 AO: finalized invoices are Buchungsbelege under a statutory
retention duty (8 years since the 2025 reform). Art. 17 Abs. 3 lit. b DSGVO
explicitly exempts data needed to meet a legal obligation from the right to
erasure — the retention duty wins for the documents, but **not** for the rest
of the account (profile, credentials, sessions), which remains fully erasable.

The repo enforces GoBD everywhere else (immutability ADR 0002, reopen audit
trail ADR 0003, archived PDF/XML ADR 0004); account deletion was the last gap.

## Decision

`DeleteAccountAsync` now branches on whether the account owns **numbered**
invoices (`Status != Draft || Number != null`):

- **No numbered invoices** (the common case — test accounts, never finalized
  anything): hard delete exactly as before. FK cascades remove drafts, line
  items and refresh tokens. No Beleg exists, so nothing must be retained.
- **At least one numbered invoice**: the account is **anonymized** instead of
  deleted, inside one transaction:
  - Unnumbered drafts are hard-deleted (no Beleg character).
  - Numbered invoices — Finalized, Paid, Cancelled/Storno, **and reopened
    drafts** (they keep their sequence number, ADR 0003, so deleting them
    would tear a gap) — stay untouched, including archived `InvoicePdf`/
    `InvoiceXml` rows. The sender/recipient snapshot on the invoice is part
    of the document and is deliberately **not** anonymized.
  - All refresh tokens are deleted.
  - The user row is scrubbed: `Email = deleted-{guid:N}@anonym.invalid`
    (GUID keeps the unique index happy; `.invalid` is RFC-2606-reserved, so
    the address can never be routed or re-registered), `PasswordHash` is
    replaced by the hash of a discarded random 256-bit value (stays a valid
    BCrypt hash — no `Verify()` foot-gun — but can never be matched),
    `Name = "Gelöschtes Konto"`, and every profile field (sender defaults,
    tax, address, phone, bank) is nulled.
  - `DeletedAt` (new nullable column on `Users`, migration
    `AddUserDeletedAt`) marks the state explicitly.

### Dead, not a zombie

`DeletedAt != null` means "this account no longer exists" for every
auth-facing path:

- `GET /auth/me`, `PATCH /auth/me`, `POST /auth/change-password`,
  `DELETE /auth/me` (idempotence): 401.
- `POST /auth/login`: takes the unknown-email path (dummy-hash verify for
  timing equalization, then 401) — for the old address the row no longer
  matches anyway, for the placeholder address the guard fires.
- `POST /auth/refresh`: all tokens were deleted; a `DeletedAt` check on the
  token's user is kept as belt-and-braces.
- Invoice endpoints that load the user (finalize, cancel, PDF/XML download)
  treat an anonymized user as not found. Access tokens issued before deletion
  die with their 15-minute lifetime.

### API behavior for the frontend

`DELETE /api/auth/me` still returns **204 in both branches** — the client
cannot and need not distinguish hard delete from anonymization; either way
the account is gone and the client signs out. No frontend change required.

## Alternatives considered

- **Keep hard delete, export invoices first:** shifts the retention duty onto
  the user and destroys the tamper-evident archive. Rejected.
- **Refuse deletion while retained invoices exist:** denies the DSGVO erasure
  right for data that *is* erasable (profile, credentials). Rejected.
- **Also anonymize sender data on retained invoices:** would falsify the
  Beleg — GoBD immutability (ADR 0002) forbids touching fixed documents.
  Rejected.
- **Static placeholder email (`deleted@anonym.invalid`):** breaks on the
  second deleted account (unique index). GUID placeholder instead.

## Consequences

- Anonymized user rows persist indefinitely; a retention-expiry cleanup
  (delete rows + invoices 8 years after `DeletedAt`) is a **deliberately
  deferred** follow-up prompt, not built here.
- `AuthService` gained `GetActiveUserAsync` — every profile-facing method
  treats `DeletedAt != null` as 401.
- New EF migration `AddUserDeletedAt` (single nullable `timestamptz` column,
  safe on empty and populated databases).
- Tests cover: hard-delete path, anonymization path (archive survives, login
  impossible with old and placeholder email, `/me` 401), placeholder
  uniqueness across two deletions, Cancelled and reopened-draft retention.
