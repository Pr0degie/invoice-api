# Deployment — Coolify on Hetzner

Deploy target: a Hetzner CX32 running [Coolify](https://coolify.io). Three
services: **invoice-api** (this repo, Dockerfile), **invoiceflow** (frontend
repo, Dockerfile), **PostgreSQL 16** (Coolify-managed database resource).

This file is the environment-variable checklist for the first staging deploy.
One line per variable: what it does, and whether you must set it.

## invoice-api

### Required — the API refuses to start without these

| Variable | What it does |
|---|---|
| `ConnectionStrings__Default` | Npgsql connection string to Postgres, e.g. `Host=<db-host>;Port=5432;Database=invoiceapi;Username=<user>;Password=<pass>`. Alternatively set `DATABASE_URL` (`postgres://user:pass@host:port/db`); if both are set, `DATABASE_URL` wins. |
| `Jwt__SigningKey` | HMAC key for access tokens, **min. 32 characters** — generate with `openssl rand -base64 48`. Startup fails on the placeholder or a short key. |
| `Email__Provider` | `Smtp` (real delivery) or `Log` (log-only — mails are **not** delivered; demo instances only). Production has no default: leaving it unset aborts startup. |
| `FRONTEND_BASE_URL` | Public frontend URL used in verify/reset mail links, e.g. `https://app.example.com`. With `Email__Provider=Smtp`, production startup rejects localhost or a missing value. |
| `ASPNETCORE_ENVIRONMENT` | `Production` — selects `appsettings.Production.json` (JSON logging, seed off, no baked-in mail provider). |

### Required when `Email__Provider=Smtp`

| Variable | What it does |
|---|---|
| `Email__Smtp__Host` | SMTP server hostname. Startup fails if missing. |
| `Email__Smtp__Port` | SMTP port, default `587`. Validated at startup (1–65535). |
| `Email__Smtp__User` | SMTP username. Must be set together with the password (or both omitted for an unauthenticated relay). |
| `Email__Smtp__Password` | SMTP password. Must be set together with the user. |
| `Email__Smtp__UseStartTls` | `true` (default) forces STARTTLS; `false` lets MailKit negotiate. |
| `Email__FromAddress` | Sender address, e.g. `no-reply@example.com`. Required at startup (default exists in base appsettings, but set it explicitly to your domain). |
| `Email__FromName` | Sender display name, default `InvoiceFlow`. |

### Recommended / situational

| Variable | What it does |
|---|---|
| `Cors__AllowedOrigins__0` | Exact browser origin allowed for direct API calls, e.g. `https://app.example.com`. The frontend proxies API calls server-side, so this only matters for direct browser access (e.g. Swagger tooling), but set it to the frontend URL anyway. |
| `Cors__PreviewOriginSuffix` | Optional host suffix for preview deployments (empty = disabled). |
| `Database__TrustServerCertificate` | Default `false` (TLS certs are validated). Set `true` only for a Postgres that presents an unverifiable cert — not needed for a Coolify-internal database. |
| `Seed__Enabled` | `true` seeds the demo account (`demo@invoiceflow.app`) once on an empty database. Default in Production is `false`; enable only on the demo instance. |
| `Jwt__Issuer` / `Jwt__Audience` | Token issuer/audience, defaults `invoice-api` / `invoiceflow`. Leave as-is unless you run multiple instances. |
| `Jwt__AccessTokenMinutes` / `Jwt__RefreshTokenDays` | Token lifetimes, defaults `15` / `30`. |
| `ASPNETCORE_URLS` | Listen address inside the container, `http://0.0.0.0:8080`. The Dockerfile already exposes 8080; TLS terminates at Coolify's proxy. |

Health check endpoint for Coolify: `GET /health` (returns 503 while the
database is unreachable).

## invoiceflow (frontend)

| Variable | What it does |
|---|---|
| `AUTH_SECRET` | **Required.** NextAuth session-JWT encryption key — generate with `openssl rand -base64 32`. The server refuses auth operations without it. |
| `API_BASE_URL` | **Required.** URL the Next.js *server* uses to reach invoice-api — the browser never calls it directly (all browser traffic goes through the `/api/backend/*` proxy). Inside Coolify, the internal service URL works, e.g. `http://<api-service>:8080`. Read at runtime — deliberately not `NEXT_PUBLIC_`-prefixed, because those values are frozen into the bundle at image build time (`NEXT_PUBLIC_API_BASE_URL` remains as the local-dev fallback only). |
| `NEXTAUTH_URL` | Public URL of the frontend, e.g. `https://app.example.com`. `trustHost` is enabled so NextAuth can also derive it from proxy headers, but set it explicitly to be deterministic. |
| `NODE_ENV` | `production` — set automatically by the Dockerfile; do not override. |

The frontend image is built from `invoiceflow/Dockerfile` (standalone Next.js
build, listens on port 3000 as non-root).

## PostgreSQL

| Variable | What it does |
|---|---|
| `POSTGRES_DB` | Database name, e.g. `invoiceapi`. |
| `POSTGRES_USER` | Database user the API connects as. |
| `POSTGRES_PASSWORD` | That user's password — referenced by `ConnectionStrings__Default`. |

Migrations run automatically at API startup (`Database.MigrateAsync`), so no
manual schema step is needed. Attach a persistent volume to the Postgres
service — invoices/PDF blobs live in the database (GoBD retention).

## Startup fail-fast checks (what a misconfiguration looks like)

The API aborts the boot with a descriptive `InvalidOperationException` when:

- `Jwt__SigningKey` is missing, shorter than 32 chars, or the placeholder;
- `Email__Provider` is unset in Production, or set to an unknown value;
- `Email__Provider=Smtp` with a missing `Email__Smtp__Host`/`Email__FromAddress`,
  an invalid port, or only one half of user/password;
- the mail-link base URL is not an absolute http(s) URL, or (Production)
  resolves to localhost.

If the container crash-loops in Coolify, read the first log lines — the
exception message names the exact variable to fix.
