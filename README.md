# invoice-api

REST API for creating invoices and exporting them as PDF — built because every client eventually needs this and the existing solutions are either overpriced SaaS or a mess.

![CI](https://github.com/Pr0degie/invoice-api/actions/workflows/ci.yml/badge.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791?logo=postgresql&logoColor=white)

---

## What it does

- Create invoices with line items (hourly, flat, per piece, per day)
- Assigns sequential invoice numbers at finalization (`2026-001`) — per user, per calendar year, never reused; drafts have no number
- Exports invoices as properly formatted A4 PDFs
- Track status: Draft → Finalized → Paid, plus Storno (cancellation) invoices — overdue is derived from the due date, not stored
- Pagination, filtering by status
- Swagger UI out of the box

## Stack

| Layer | Tech |
|---|---|
| Runtime | .NET 8 / ASP.NET Core |
| Database | PostgreSQL + EF Core |
| PDF | QuestPDF |
| Logging | Serilog |
| Tests | xunit + FluentAssertions |
| Deploy | Docker + Railway |

---

## Getting started

**Prerequisites:** Docker, .NET 8 SDK

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
POST   /api/auth/register   — create account → { token, refreshToken, expiresAt, user }
POST   /api/auth/login      — sign in        → { token, refreshToken, expiresAt, user }
POST   /api/auth/refresh    — rotate tokens  → { token, refreshToken, expiresAt, user }
POST   /api/auth/logout     — revoke refresh token
GET    /api/auth/me         — current user info  [requires Bearer token]
PATCH  /api/auth/me         — update profile / tax data (tax IDs, § 19 flag, address, bank details)  [requires Bearer token]
```

### Invoice endpoints (require Bearer token)

All `/api/invoices/*` endpoints return 401 without a valid JWT.
Invoices are isolated per user — each user only sees their own.

```
POST   /api/invoices                — create invoice (as Draft, no number yet)
GET    /api/invoices                — list (filter: ?status=Paid&page=1&pageSize=25 — also accepts the virtual ?status=Overdue)
GET    /api/invoices/stats          — dashboard KPIs (?from=<iso>&to=<iso>)
GET    /api/invoices/{id}           — get single invoice
PUT    /api/invoices/{id}           — edit invoice (drafts only, 409 otherwise)
POST   /api/invoices/{id}/finalize  — stamp issue date (today, or optional past { issueDate }), assign number, snapshot tax data, archive PDF (drafts only)
POST   /api/invoices/{id}/cancel    — create a Stornorechnung, original becomes Cancelled (finalized only)
PATCH  /api/invoices/{id}/status    — mark paid / undo (Finalized ⇄ Paid only)
GET    /api/invoices/{id}/pdf       — download PDF (archived copy once finalized)
DELETE /api/invoices/{id}           — delete draft (drafts only, 409 otherwise)
```

Full spec available via Swagger when running locally.

### Example: register + create invoice

```http
# 1. Register
POST /api/auth/register
Content-Type: application/json

{ "email": "tobias@example.com", "password": "supersecret", "name": "Tobias Dev" }

# Response: { "token": "<jwt>", "refreshToken": "...", ... }

# 2. Create invoice (use token from step 1)
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

95 unit tests covering service logic, totals, number generation, finalize/cancel lifecycle (incl. issue-date stamping), PDF archiving, user isolation, stats aggregation, and auth flows.

---

## Deployment

The repo ships with a `deploy.yml` workflow that deploys to [Railway](https://railway.app) on every merge to `main`. Set `RAILWAY_TOKEN` in your repo secrets and you're done.

```bash
# manual deploy
railway up
```

For other platforms: the `Dockerfile` produces a minimal ASP.NET runtime image (~100MB), so it'll run anywhere that speaks Docker.

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

DB migrations run automatically on startup.

---

## Notes

Invoice numbers are assigned at finalization — sequential per user and calendar year (`2026-001`), never reused. The sequence lives in the `InvoiceNumberSequences` table with the counter as an EF concurrency token, plus a unique `(UserId, Number)` index as a backstop. Drafts carry no number until finalized.

QuestPDF is used under the Community License — free for open source projects and commercial use below $1M annual revenue.

---

## License

MIT
