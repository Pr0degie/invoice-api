# ADR 0001 — Refresh-token rotation grace window

**Status:** Accepted · 2026-07-02

## Context

Refresh tokens are single-use: `POST /api/auth/refresh` revokes the presented
token and issues a successor. Two legitimate clients can race on the same
token (two tabs, a retried request, NextAuth refreshing in parallel) — the
loser got a 401 and the user was sporadically logged out.

Since tokens are stored only as SHA-256 hashes (ADR-adjacent change in the
same hardening pass), the raw successor token cannot be re-derived from the
database when a losing racer shows up.

## Decision

Two mechanisms, combined:

1. **`RefreshToken.ReplacedByTokenHash`** (persisted): set on rotation to the
   successor's hash. It durably distinguishes *revoked by rotation* from
   *revoked by logout / password change*.
2. **In-memory successor cache** (`IMemoryCache`, 60 s TTL): on rotation, the
   full `AuthResponseDto` (raw successor tokens) is cached keyed by the old
   token's hash. A refresh with a token rotated **less than 60 s ago** replays
   the identical successor response instead of failing.

Reuse of a rotated token **after** the grace window (or on a cache miss with
an aged rotation) is treated as a theft signal: all of the user's active
refresh tokens are revoked and the request gets a 401.

Revocations *not* caused by rotation (logout, password change) never enter the
grace path and never trigger the mass revoke.

## Consequences

- Concurrent refreshes within 60 s succeed and converge on one session.
- The raw successor lives in process memory for up to 60 s. Accepted: the API
  runs as a single Railway instance; memory access implies full host
  compromise anyway.
  *(Update 2026-07-05: deploy target is now Coolify on Hetzner — still a
  single instance, the reasoning is unchanged.)*
- **Single-instance assumption.** With multiple API replicas the cache is not
  shared — a losing racer hitting another replica inside the grace window gets
  a plain 401 (no mass revoke, because the theft signal requires the rotation
  to be *older* than the grace window). Sporadic logouts would return; switch
  to a shared cache (Redis) or persist an encrypted successor before scaling out.
- A truly simultaneous pair of refreshes can both pass the revocation check
  and mint two successors. Both are valid sessions for the same user — benign,
  and rare enough to leave untransacted.
