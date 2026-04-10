# invoice-api

REST API for creating invoices and exporting them as PDF — built because every client eventually needs this and the existing solutions are either overpriced SaaS or a mess.

![CI](https://github.com/Pr0degie/invoice-api/actions/workflows/ci.yml/badge.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791?logo=postgresql&logoColor=white)

---

## What it does

- Create invoices with line items (hourly, flat, per piece, per day)
- Auto-generates invoice numbers (`INV-2024-0001`)
- Exports invoices as properly formatted A4 PDFs
- Track status: Draft → Sent → Paid / Overdue
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

```
POST   /api/invoices              — create invoice
GET    /api/invoices              — list (filter: ?status=Paid&page=1&pageSize=25)
GET    /api/invoices/{id}         — get single invoice
PATCH  /api/invoices/{id}/status  — update status
GET    /api/invoices/{id}/pdf     — download as PDF
DELETE /api/invoices/{id}         — delete draft
GET    /health                    — health check
```

Full spec available via Swagger when running locally.

### Example request

```http
POST /api/invoices
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
  "number": "INV-2024-0001",
  "subtotal": 870.00,
  "taxAmount": 165.30,
  "total": 1035.30,
  "currency": "EUR",
  "status": "Draft",
  ...
}
```

---

## Running tests

```bash
dotnet test
```

8 unit tests covering service logic, total calculations, number generation, and error cases.

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

Invoice numbers are sequential per calendar year. If you need a different format (e.g. `2024/001` or customer-specific prefixes), that's a 5-minute change in `InvoiceService.GenerateNumberAsync`.

QuestPDF is used under the Community License — free for open source projects and commercial use below $1M annual revenue.

---

## License

MIT
