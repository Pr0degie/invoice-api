# invoice-api

REST API for creating invoices and exporting them as PDF plus German E-Rechnung (XRechnung / EN 16931) — built because every client eventually needs this and the existing solutions are either overpriced SaaS or a mess.

![CI](https://github.com/Pr0degie/invoice-api/actions/workflows/ci.yml/badge.svg)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791?logo=postgresql&logoColor=white)

---

## What it does

- Create invoices with line items (hourly, flat, per piece, per day)
- Assigns sequential invoice numbers at finalization (`2026-001`) — per user, per calendar year, never reused; drafts have no number
- Exports invoices as properly formatted A4 PDFs
- Generates a legally binding German **E-Rechnung** (XRechnung 3.0 / EN 16931, CII XML) per finalized invoice, archived immutably alongside the PDF (§ 19 → tax category E; Storno → type 384)
- Track status: Draft → Finalized → Paid, plus Storno (cancellation) invoices — overdue is derived from the due date, not stored
- Pagination, filtering by status
- Swagger UI out of the box

## Stack

| Layer | Tech |
|---|---|
| Runtime | .NET 10 LTS / ASP.NET Core |
| Database | PostgreSQL + EF Core |
| PDF | QuestPDF |
| E-Rechnung | ZUGFeRD-csharp (XRechnung CII) |
| Logging | Serilog |
| Tests | xunit + FluentAssertions |
| Deploy | Docker + Coolify (Hetzner) |

---

## Getting started

**Prerequisites:** Docker, .NET 10 SDK

```bash
git clone https://github.com/Pr0degie/invoice-api
cd invoice-api

# spin up the API + Postgres
docker compose up

# or run locally against a local DB
dotnet run --project src/InvoiceApi
```

Swagger UI: [http://localhost:8080/swagger](http://localhost:8080/swagger)

---

## API overview

### Auth endpoints (public)

```
POST   /api/auth/register              — create account (unverified) → 201 { message }; NO session
POST   /api/auth/login                 — sign in → { token, refreshToken, expiresAt, user }; 403 email_not_verified if unverified
POST   /api/auth/refresh               — rotate tokens → { token, refreshToken, expiresAt, user }
POST   /api/auth/logout                — revoke refresh token
POST   /api/auth/verify-email          — redeem the e-mailed verification link (24 h) → 204
POST   /api/auth/resend-verification   — re-send verification link → 200 { message } (always, generic)
POST   /api/auth/forgot-password       — request a reset link → 200 { message } (always, generic)
POST   /api/auth/reset-password        — set a new password via token (1 h) → 204 (revokes all refresh tokens)
POST   /api/auth/change-password       — change password when signed in → 204  [requires Bearer token]
GET    /api/auth/me                     — current user info  [requires Bearer token]
PATCH  /api/auth/me                     — update profile / tax data (tax IDs, § 19 flag, address, bank details)  [requires Bearer token]
DELETE /api/auth/me                     — delete the account  [requires Bearer token]
```

Registration does **not** log the user in: it creates an unverified account and
e-mails a verification link; login stays blocked (`403 email_not_verified`) until
`verify-email` is redeemed. `forgot-password` / `resend-verification` always return
the same generic `200` regardless of whether the address exists. In Development,
mails (incl. the link) are written to the log — no SMTP needed. See
[`docs/adr/0006`](docs/adr/0006-password-reset-and-email-verification.md).

`register`, `forgot-password` and `resend-verification` accept an optional
`locale` (`"de"` | `"en"`; anything else or absent → `de`). It localizes the mail
(subject + body) and is embedded as an explicit path segment in the link
(`{FRONTEND_BASE_URL}/{locale}/verify-email?token=…`, likewise `/reset-password`)
so next-intl (`localePrefix: "as-needed"`) lands the user on their own language.
The value is normalized against the allowlist before it touches the URL — never a
free string.

### Invoice endpoints (require Bearer token)

All `/api/invoices/*` endpoints return 401 without a valid JWT.
Invoices are isolated per user — each user only sees their own.

```
POST   /api/invoices                — create invoice (as Draft, no number yet)
GET    /api/invoices                — list (filter: ?status=Paid&page=1&pageSize=25 — also accepts the virtual ?status=Overdue)
GET    /api/invoices/stats          — dashboard KPIs (?from=<iso>&to=<iso>)
GET    /api/invoices/{id}           — get single invoice
PUT    /api/invoices/{id}           — edit invoice (drafts only, 409 otherwise)
POST   /api/invoices/{id}/finalize  — stamp issue date (today, or optional past { issueDate }), assign number, snapshot tax data, archive PDF + E-Rechnung XML (drafts only; requires seller phone + structured recipient address & email)
POST   /api/invoices/{id}/reopen    — reset Finalized → Draft for pre-dispatch corrections (audited, number retained; re-finalize reuses it)
POST   /api/invoices/{id}/cancel    — create a Stornorechnung, original becomes Cancelled (finalized only)
PATCH  /api/invoices/{id}/status    — mark paid / undo (Finalized ⇄ Paid only)
GET    /api/invoices/{id}/pdf       — download PDF (archived copy once finalized)
GET    /api/invoices/{id}/xml       — download E-Rechnung XML (XRechnung; finalized only, 409 for drafts)
DELETE /api/invoices/{id}           — delete draft (drafts only, 409 otherwise)
```

Full spec available via Swagger when running locally.

### Example: register, verify, create invoice

```http
# 1. Register — creates an UNVERIFIED account and e-mails a verification link
POST /api/auth/register
Content-Type: application/json

{ "email": "tobias@example.com", "password": "supersecret", "name": "Tobias Dev" }

# Response: 201 { "message": "Registrierung erfolgreich. Bitte bestätige ..." }

# 2. Redeem the link from the e-mail (in Dev: printed to the log), then log in
POST /api/auth/verify-email   { "token": "<from link>" }        # → 204
POST /api/auth/login          { "email": "...", "password": "..." }
# Response: { "token": "<jwt>", "refreshToken": "...", ... }

# 3. Create invoice (use token from step 2)
POST /api/invoices
Authorization: Bearer <jwt>
Content-Type: application/json

{
  "senderName": "Tobias Dev",
  "senderAddress": "Musterstraße 1, 80331 München",
  "recipientName": "ACME GmbH",
  "recipientAddress": "Testweg 5, 10115 Berlin",
  "taxRate": 0.19,
  "currency": "EUR",
  "lineItems": [
    { "description": "Web Development", "quantity": 8, "unitPrice": 90, "unit": "h" },
    { "description": "Project Setup", "quantity": 1, "unitPrice": 150, "unit": "flat" }
  ]
}
```

```json
{
  "id": "3fa85f64-...",
  "number": null,
  "subtotal": 870.00,
  "taxAmount": 165.30,
  "total": 1035.30,
  "currency": "EUR",
  "status": "Draft",
  ...
}
```

---

## Demo

A demo user is seeded automatically on the first startup when `Seed:Enabled=true` (default in Production):

| | |
|---|---|
| **Email** | `demo@invoiceflow.app` |
| **Password** | `DemoPass123!` |

The demo account includes 15 invoices across 6 recipients, various statuses (Draft, Finalized — shown as "Open", some of them overdue — Paid, Cancelled), and 11 months of history — enough to make the dashboard stats meaningful.

---

## Running tests

```bash
dotnet test
```

197 unit tests covering service logic, totals, line-item ordering, number generation, finalize/cancel/reopen lifecycle (incl. issue-date stamping and number reuse after reopen), PDF + E-Rechnung XML archiving, XRechnung generation (Kleinunternehmer / Regelbesteuerung / Storno golden cases), audit trail, user isolation, stats aggregation, auth flows (incl. e-mail verification, password reset, and anti-enumeration), the refresh-token cleanup rule, and the fail-fast e-mail/SMTP startup validation.

---

## Deployment

Deploy target is [Coolify](https://coolify.io) on a Hetzner VPS: point a Coolify application at this repo, it builds the `Dockerfile` and serves the container behind its proxy. The complete environment-variable checklist (API, frontend, Postgres) lives in [`docs/deploy.md`](docs/deploy.md).

The `Dockerfile` produces a minimal ASP.NET runtime image (~100MB), so it'll run anywhere that speaks Docker.

---

## Configuration

`appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Default": "Host=...;Database=invoiceapi;Username=...;Password=..."
  }
}
```

Or via environment variable: `ConnectionStrings__Default`.

For the reset/verification e-mails set `FRONTEND_BASE_URL` (link host) and, to send
real mail, `Email__Provider=Smtp` + the `Email__Smtp*` vars — see [`.env.example`](.env.example).
Without them, Development uses a log-only mail sender. DB migrations run automatically on startup.

---

## Notes

Invoice numbers are assigned at finalization — sequential per user and calendar year (`2026-001`), never reused for another invoice. The sequence lives in the `InvoiceNumberSequences` table with the counter as an EF concurrency token, plus a unique `(UserId, Number)` index as a backstop. Drafts carry no number until finalized; a reopened invoice keeps its number and re-finalizing reuses it without touching the sequence (ADR 0003).

QuestPDF is used under the Community License — free for open source projects and commercial use below $1M annual revenue.

---

## License

MIT
