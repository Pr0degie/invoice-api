# invoice-api/CLAUDE.md — Refactor + Updates

Slim, single-file CLAUDE.md for the backend. Backend is small enough that a docs/ split adds overhead, not value. Goal: ~120 lines, everything an agent needs in one read.

---

## Step 1 — Read the existing file

Read the current `CLAUDE.md` in this repo. Identify:
- Any project-specific conventions worth keeping (naming, folder layout, test patterns).
- Anything outdated (mentions of OAuth-only auth, COUNT-based numbering, hardcoded CORS, missing `Cancelled` status).
- Anything redundant with what the code already enforces (e.g. .editorconfig rules).

Print a short audit table (KEEP / UPDATE / DROP / MISSING) before rewriting.

---

## Step 2 — Replace the file with this structure

Adapt placeholders marked `<…>` based on the audit. Keep wording terse — every line must earn its place.

````markdown
# CLAUDE.md — invoice-api

ASP.NET Core 8 backend for InvoiceFlow. Frontend (Next.js) lives in `../invoiceflow/`.
Read top-to-bottom before touching code. §3 and §4 are non-negotiable.

---

## §1 Project

REST API for invoice management. Single-tenant per user (no orgs/teams).
PostgreSQL, JWT auth, QuestPDF for PDF generation, Serilog for structured logs.
Deployed via Coolify (Docker) on a Hetzner VPS. Frontend authenticates via credentials and gets a JWT + refresh token.

### Key facts

- **Single user owns their data.** Every query MUST filter by `UserId` from the JWT.
- **Invoice numbers are per-user scoped** — unique on `(UserId, Number)`, `NULL` while Draft.
  Assigned atomically at finalization: `{year}-{NNN}`, counter resets per year. See ADR 0002.
- **Enums serialize as strings** via `JsonStringEnumConverter`. Statuses: `Draft | Finalized | Paid | Cancelled`
  (`Overdue` is derived, `isOverdue`). Finalize/Storno/Reopen have dedicated POST endpoints, not status PATCHes.
- **Reopen (ADR 0003):** `POST /{id}/reopen` resets `Finalized → Draft` for pre-dispatch corrections —
  audited (`InvoiceAuditEntries`, append-only), number retained, re-finalize reuses it without touching the sequence.
- **Line items carry a `Position` column** (zero-based input order). Every read path sorts by it — the PK is a Guid,
  so unsorted `Include`s return arbitrary order.
- **`GET /api/invoices` returns a flat array.** No pagination. Filter via query params only.

---

## §2 Tech & layout

| | |
|---|---|
| Framework | ASP.NET Core 8 (Minimal hosting + Controllers) |
| ORM | EF Core 8 + Npgsql |
| DB | PostgreSQL (Docker locally, Coolify-managed in prod) |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` + custom refresh-token store |
| PDF | QuestPDF |
| Logging | Serilog (JSON structured to stdout) |
| Testing | xUnit + EF Core InMemory (service-level, no WebApplicationFactory), 141 tests |

````
src/InvoiceApi/
  Controllers/      AuthController, InvoicesController
  Services/         InvoiceService, AuthService, RefreshTokenService, SeedService
  Data/             InvoiceApiDbContext, Migrations/
  Entities/         User, Invoice, LineItem, RefreshToken, InvoiceNumberSequence, InvoicePdf
  Dtos/             *Request, *Response (no entity leaks across the boundary)
  Program.cs        DI, middleware pipeline, CORS, JWT setup
tests/InvoiceApi.Tests/
````

---

## §3 Commands

```bash
docker compose up                    # postgres + api on :8080 (Development env, Swagger enabled)
dotnet build                         # local build
dotnet test                          # full xUnit suite (191 tests must stay green)
dotnet ef migrations add <Name> --project src/InvoiceApi
dotnet ef database update --project src/InvoiceApi
```

Health: `GET /health` → 200. Swagger UI: `http://localhost:8080/swagger` (Development only).

---

## §4 Non-negotiable rules

- **User-isolation in every query.** `_db.Invoices.Where(i => i.UserId == userId)…`. No exceptions. Code review reject otherwise.
- **DTOs in, DTOs out.** Never accept or return EF entities directly. Mapping in the service layer.
- **Computed fields are server-authoritative.** `subtotal`, `taxAmount`, `total` for invoices and `total` for line items are computed in `InvoiceService` — ignore any value the client sends.
- **Status transitions are validated.** Only `Draft` invoices may be `PUT` or `DELETE`d (409 otherwise) — and a reopened draft (one that owns a `Number`) may never be deleted, only re-finalized (gap-free numbering). The `PATCH /status` endpoint enforces the legal transition set.
- **Per-user invoice numbers via `InvoiceNumberSequences`, not MAX/COUNT.** One sequence row per `(UserId, Year)`; the counter is an EF concurrency token, unique index `(UserId, Number)` as backstop. Assigned only at finalization — never reused. Don't refactor back to MAX or COUNT.
- **CORS via configuration.** `Cors:AllowedOrigins` array in appsettings, env override `Cors__AllowedOrigins__0` in prod. Optional preview-deploy origins via `Cors:PreviewOriginSuffix` (`SetIsOriginAllowed`).
- **Migrations are append-only.** Never edit a committed migration. Add a new one.
- **Tests stay green.** PR doesn't ship if `dotnet test` is red.

---

## §5 Auth flow

````
POST /api/auth/register              { name, email, password }        → 201 { message }  (NO session)
POST /api/auth/login                 { email, password }              → AuthResponse | 403 email_not_verified
POST /api/auth/refresh               { refreshToken }                 → AuthResponse
POST /api/auth/logout                { refreshToken }                 → 204  (revokes the refresh token)
POST /api/auth/forgot-password       { email }                        → 200 { message }  (always, generic)
POST /api/auth/reset-password        { token, newPassword }           → 204  (revokes all refresh tokens)
POST /api/auth/verify-email          { token }                        → 204
POST /api/auth/resend-verification   { email }                        → 200 { message }  (always, generic)
GET   /api/auth/me                                                    → UserDto
PATCH /api/auth/me       { tax profile fields }                       → UserDto  (null = unchanged, "" = clear)
````

`AuthResponse = { token, refreshToken, expiresAt, user }`.
Access tokens: short-lived (15 min). Refresh tokens: 30 days, single-use, rotated on every refresh.
Refresh tokens stored in DB (`RefreshTokens` table) with `RevokedAt`. **Cleanup job for expired tokens is a TODO.**

- **E-mail verification (ADR 0006):** register creates the user `EmailVerifiedAt=null` and issues
  **no session** — it mails a 24 h verify link; login stays blocked (`403 email_not_verified`) until
  `verify-email` redeems it. Seed/existing users are verified (migration backfills `CreatedAt`).
- **Password reset (ADR 0006):** `forgot-password` / `resend-verification` are anti-enumeration
  (always generic 200, dummy work on miss). Reset/verify tokens live in `UserTokens` — SHA-256-hashed,
  single-use, TTL (reset 1 h / verify 24 h), `Type` discriminator; same hardening as refresh tokens.
- **Mail:** `IEmailSender` — `SmtpEmailSender` (MailKit, `Email__Smtp*`) or `LogEmailSender`
  (default in Dev / when SMTP unset). Links use `FRONTEND_BASE_URL`. **Delivery is decoupled
  from the request path:** services `IEmailQueue.Enqueue` (non-blocking); `EmailBackgroundService`
  (a `BackgroundService` draining a `System.Threading.Channels` queue) sends and logs failures
  instead of failing the request — so SMTP latency isn't an enumeration oracle and a mail outage
  can't 500 a register/reset. Tests assert over the queue double, not the sender.

---

## §6 API contract

The frontend's `docs/api-contract.md` is the canonical spec. **When changing endpoints or DTOs, update both:**
1. The backend code.
2. `../invoiceflow/docs/api-contract.md`.
3. The frontend regenerates types via `npm run api:types` against the running backend's swagger.

Never let the two drift.

---

## §7 Open follow-ups

These are tracked here so the next agent picks the right one:

- [ ] **Coolify staging deploy** — first production push to the Hetzner VPS. Env-var checklist: `docs/deploy.md`.
- [ ] **Swagger in Production** — currently disabled. Enable behind a flag (`Swagger:Enabled` config) for portfolio visibility.
- [ ] **Refresh-token cleanup job** — background `IHostedService` to delete tokens where `ExpiresAt < UtcNow - 7 days`.
- [ ] **Rate limiting** — currently global. Per-user limits (after auth middleware) would be safer.
- [ ] **E-mail outbox** — delivery is queued in-process (`IEmailQueue`, no retry/persistence); a crash drops undelivered mail. If transactional/high-value mail is ever added, move to a durable outbox (persist in the triggering DB tx, worker delivers + marks sent, with retry). See ADR 0006 Consequences.

---

## §8 When in doubt — ask

A two-line clarification beats half a day of rework. Stop and ask when:
- A new endpoint would change the response shape of an existing one.
- A migration would touch existing rows (data migration vs schema migration).
- You're tempted to bypass `_db.Invoices.Where(i => i.UserId == userId)` for any reason.
````

---

## Step 3 — Verify

````bash
wc -l CLAUDE.md
dotnet test                          # all 111 must still pass — no code changed, but sanity check
````

Expected: `CLAUDE.md` ≤ 130 lines.

---

## Step 4 — Report

1. Audit table from Step 1 (KEEP / UPDATE / DROP / MISSING).
2. Final line count.
3. Anything from the old file that didn't survive — with one-line reason each.
4. Any conflict between the template and existing project conventions (e.g. the template says "Controllers" but the repo uses Minimal API endpoints) — flag it, don't silently override.

**Commit:**
````
chore(docs): rewrite CLAUDE.md — slim, single-file, reflects per-user
numbering, config-driven CORS, logout body shape, and current TODOs
````