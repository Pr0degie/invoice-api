# ADR 0006 — Password reset & e-mail verification

Status: accepted · Date: 2026-07-05 · Scope: invoice-api (Prompt 18a)

## Context

Going live surfaced two gaps in the auth surface:

1. **No password reset.** A user who forgets their password is permanently
   locked out — there was no self-service recovery.
2. **No e-mail verification.** Registration trusted the submitted address
   blindly, so anyone could sign up under someone else's e-mail.

Both need transactional mail. This ADR records the token design, the decision to
block login for unverified accounts, and the one deliberate deviation from our
otherwise strict anti-enumeration stance.

## Decisions

### Token design — mirrors refresh tokens (ADR 0001)

Reset and verification tokens follow the same hardening as refresh tokens:

- **Random & opaque.** 32 bytes from `RandomNumberGenerator`, URL-safe base64
  (`SecureToken.Generate`). The raw value travels once, inside the e-mailed link.
- **Hashed at rest.** Only the SHA-256 hash (`RefreshTokenHasher.Hash`) is stored
  in `UserTokens.TokenHash` (unique index). A DB leak yields no usable link.
- **Single-use.** `ConsumedAt` is stamped on redemption; a consumed token is
  rejected. Consumption and its effect (password change / verification) commit in
  one `SaveChanges`, so they're atomic.
- **TTL-bound.** Password reset **1 h**, e-mail verification **24 h**. Expired
  tokens are rejected and swept: creating a new token deletes the user's prior
  tokens of that type plus any globally-expired rows (`RemoveRange` + the caller's
  `SaveChanges` — not `ExecuteDelete`, so the InMemory test provider stays
  supported, consistent with `LoginAsync`'s refresh-token housekeeping). Requesting
  a fresh link therefore invalidates the previous one.

One `UserTokens` table with a `Type` discriminator (`PasswordReset` |
`EmailVerification`) rather than two tables — identical shape, and lookups already
filter by type so a reset token can never redeem a verification (and vice-versa).

A successful **password reset revokes all of the user's refresh tokens** — the old
password is presumed compromised, so no pre-reset session may survive. (Same
policy as change-password.)

### Login is blocked until the address is verified

Registration creates the user with `EmailVerifiedAt = null` and issues **no
session** — the response is a generic message, not tokens. The account must
redeem the verification link before it can log in. Rationale: if registration
returned tokens, an unverified user would hold a valid session (refreshable
indefinitely) and verification would be pointless.

`POST /auth/verify-email` stamps `EmailVerifiedAt`, unblocking login.

### The one deliberate enumeration exception — 403 on unverified login

Our baseline is strict anti-enumeration: `login`, `forgot-password`, and
`resend-verification` must not reveal whether an address is registered.

- **Login** runs a BCrypt verify in *both* branches — against a dummy hash when
  the account is unknown — so timing doesn't leak existence; failure is a generic
  `401 Invalid credentials`.
- **forgot-password / resend-verification** always return `200` with an identical
  generic message and do comparable work on the miss path (a throwaway token
  generation), so neither body nor timing distinguishes hit from miss.

The **exception:** an unverified-but-correct login returns **`403` with
`{ "error": "email_not_verified" }`**, not the generic 401. This is intentional:

- The user *must* understand why they can't get in, or the product looks broken.
- Enumeration is moot here — whoever just supplied the correct password already
  knows the account exists; and an attacker who could register the address learns
  nothing new. There is no security gain in hiding it, only a UX cost.

`register` still returns `409` on a duplicate e-mail (pre-existing behaviour);
that remains the one place existence is observable, which is standard and
unchanged by this work.

### Grandfathering existing accounts

The migration backfills `EmailVerifiedAt = CreatedAt` for every pre-existing user
(`UPDATE … WHERE EmailVerifiedAt IS NULL`, a no-op on an empty DB). Accounts that
predate verification are treated as verified — otherwise the migration would lock
out the entire existing user base. Seed users are created pre-verified for the
same reason.

### Mail abstraction

`IEmailSender` with two implementations: `SmtpEmailSender` (MailKit, configured
via `Email:Smtp*`) and `LogEmailSender` (writes the message + link to the log).
Selection is by `Email:Provider` — the log-only sender is the default in
Development and anywhere SMTP isn't explicitly configured. Plain-text, German
mails; no template framework.

**Delivery is decoupled from the request path.** Services enqueue via
`IEmailQueue` (non-blocking, in-process unbounded `System.Threading.Channels`);
`EmailBackgroundService` drains the queue and calls `IEmailSender`, logging
failures instead of propagating them. This was originally inline, but with real
SMTP the hit path blocked on the round-trip while the miss path didn't — making
response time an enumeration oracle on `forgot-password` / `resend-verification`
— and a mail outage failed an otherwise-fine register/reset. Queueing removes
both: every path just enqueues (or skips) and returns.

## Consequences

- Users can recover access and we no longer trust unverified addresses.
- The frontend register flow changes: registration no longer logs the user in;
  it must route to a "check your inbox" state and offer resend. Login must handle
  the new `403 email_not_verified`. See `docs/api-contract.md`.
- `UserTokens` grows one short-lived row per outstanding reset/verify request;
  each creation self-prunes expired and superseded rows, so it stays small
  without a separate cleanup job.
- Anonymised accounts (Prompt 15 / ADR 0005) are **not yet implemented** on this
  branch. When they land, `forgot-password` and `login` should additionally skip
  accounts flagged deleted so a placeholder address can't be driven — a one-line
  guard, noted here so it isn't missed.
- The e-mail queue is **in-process, with no retry/backoff or persistence** — an
  unbounded channel drained by a single background worker, right-sized for the
  current auth-flow mail volume. A process crash drops undelivered messages
  (acceptable: the user simply re-requests the link). **Follow-up:** if
  transactional or high-value mail is ever added, replace this with a durable
  **outbox pattern** — persist the message in the same DB transaction as its
  triggering change, then have the worker deliver and mark it sent, with retry.
  That buys at-least-once delivery and survives restarts; deferred until a
  concrete need justifies the schema + machinery.
