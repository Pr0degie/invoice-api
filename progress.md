# Progress

## Pre-Launch-Hardening — Mail-Config, MailKit, Warnings, Coolify (2026-07-05)

Backend-Hälfte der Deploy-Vorbereitung (Frontend-Hälfte in
`../invoiceflow/docs/progress.md`). Ziel: Coolify-Staging-Deploy auf Hetzner.

- **E-Mail-Konfiguration fail-fast** (`EmailStartupValidation`, aus
  `Program.cs` aufgerufen): unbekannter Provider (Tippfehler → sonst stiller
  Log-Sender), fehlender `Email__Smtp__Host`/`Email__FromAddress`, ungültiger
  Port, halbes User/Password-Paar und (Production) localhost als
  Mail-Link-Basis brechen jetzt den Boot ab, statt dass der Background-Worker
  Mails still verliert. `appsettings.Production.json` leert den Provider —
  Production muss explizit `Email__Provider=Smtp` oder `Log` wählen.
- **`FRONTEND_BASE_URL`-Präzedenz gefixt:** die Env-Var schlägt jetzt den in
  `appsettings.json` eingebackenen `App:FrontendBaseUrl`-Default. Vorher
  konnte sie nie gewinnen — Produktions-Mails hätten localhost-Links gehabt.
- **`docs/deploy.md` (neu):** komplette Env-Var-Checkliste für Coolify
  (API, Frontend, Postgres) inkl. der Fail-fast-Fehlerbilder.
- **MailKit 4.8.0 → 4.17.0** (GHSA-9j88-vvj5-vhgr);
  `dotnet list package --vulnerable --include-transitive` sauber für beide
  Projekte. Nullability-Folgefix in `SmtpEmailSender` (Auth nur bei
  vollständigem Credential-Paar — deckt sich mit der Startup-Validierung).
- **QuestPDF `MinimalBox()` → `Shrink()`** (reines Rename);
  `TreatWarningsAsErrors=true` in `InvoiceApi.csproj`, CI-Build mit
  `-warnaserror`. Release-Build warnungsfrei.
- **Railway-Reste entfernt:** `railway.json` gelöscht; README/CLAUDE.md/
  `Program.cs`-Kommentare/`.env.example` auf Coolify umgestellt; ADRs 0001/0002
  behalten ihre Historie und bekommen eine datierte Update-Notiz.
- **Tests: 191 grün** (168 → +23 für die Validierung). CLAUDE.md-Zähler
  aktualisiert.

## Prompt 15 — GoBD-konforme Account-Löschung (2026-07-05)

### Umgesetzt

- **`DeleteAccountAsync` verzweigt** (`AuthService`): Ohne nummerierte Rechnungen
  (`Status != Draft || Number != null`) Hard-Delete wie bisher; sonst
  **Anonymisierung** in einer Transaktion — unnummerierte Drafts + alle Refresh
  Tokens per `ExecuteDeleteAsync` weg, User-Felder genullt, E-Mail →
  `deleted-{guid:N}@anonym.invalid`, PasswordHash → Hash eines verworfenen
  Zufallswerts, `DeletedAt` gesetzt. Nummerierte Rechnungen (inkl. Storno und
  reopened Drafts mit Nummer, ADR 0003) samt PDF-/XML-Archiv bleiben unberührt.
- **Neues Feld `User.DeletedAt`** + Migration `20260705…_AddUserDeletedAt`
  (eine nullable Spalte, läuft auf leerer wie befüllter DB).
- **`DeletedAt != null` ⇒ 401 überall**: `GET/PATCH /auth/me`, Change-Password,
  zweites `DELETE /me`, Login (Dummy-Hash-Pfad, timing-neutral), Refresh
  (Belt-and-Braces), plus User-Lookups in `InvoiceService`
  (Finalize/Cancel) und `InvoicesController` (PDF/XML-Download).
- **ADR `docs/adr/0005-gobd-account-deletion-anonymization.md`**: DSGVO-Löschrecht
  vs. § 147 AO, Entscheidung, Alternativen. `DELETE /me` liefert weiterhin 204
  in beiden Zweigen — kein Frontend-Change nötig.
- **Tests** (AuthServiceTests, SQLite in-memory wegen Bulk-Ops): Hard-Delete-Pfad,
  Anonymisierung (Archiv intakt, Login mit alter + Platzhalter-Mail unmöglich,
  `/me` → 401 via Controller-Test), Unique-Email-Kollision (zwei Löschungen),
  Cancelled- und Reopened-Draft-Retention. Stand: 132/132 grün.

### Offen / TODO

- Lösch-Scheduler nach Ablauf der 8-Jahres-Frist (Belege + anonymisierte User-Rows
  endgültig entfernen) — bewusst nicht gebaut, eigener späterer Prompt.

## Prompt 13 — Auth- & API-Härtung (2026-07-05)

### Umgesetzt

1. **Timing-sichere Login-Prüfung** (`AuthService.LoginAsync`): Bei unbekannter
   E-Mail wird jetzt gegen einen statischen Cost-12-Dummy-BCrypt-Hash verifiziert,
   damit beide Pfade dieselbe Arbeit leisten; Fehlermeldung unverändert
   ("Invalid credentials."). Test: `Login_WithUnknownEmail_StillVerifiesAgainstDummyHash_WithSameError`.
2. **Passwort-Obergrenze + Workfactor**: `[MaxLength(128)]` auf `RegisterDto.Password`
   und `ChangePasswordDto.NewPassword`; `BCryptPasswordHasher` pinnt den Workfactor
   explizit auf 12. Alte Cost-11-Hashes verifizieren weiter (Cost steckt im Hash) —
   abgesichert in `tests/.../Auth/PasswordHasherTests.cs`.
3. **TLS zur DB**: `ParseDatabaseUrl` (Program.cs) setzt `Trust Server Certificate`
   nicht mehr hart auf `true`. Neu: env `Database__TrustServerCertificate`,
   Default `false` (= Zertifikate werden validiert). Doku in `.env.example`
   (Railway-interne Verbindungen brauchen ggf. `true`).
4. **`/health`**: DB-Probe-Ergebnis wird 10 s in `IMemoryCache` gecacht
   (Variante "Cache" gewählt, kein zusätzliches Rate Limit — Prompt wollte nur eins).
5. **EF-Bulk-Operationen**: Expired-Token-Housekeeping in `LoginAsync`
   (`ExecuteDeleteAsync`), Token-Revoke in `ChangePasswordAsync` und Theft-Response
   in `RefreshAsync` (`ExecuteUpdateAsync`) — keine Token-Listen mehr im Speicher.

### Testsetup-Änderung (wichtig für künftige Sessions)

`ExecuteDelete/ExecuteUpdate` werden vom EF-InMemory-Provider nicht unterstützt.
`AuthServiceTests` laufen deshalb jetzt gegen **SQLite in-memory**
(`Microsoft.EntityFrameworkCore.Sqlite` im Testprojekt, offene Connection +
`EnsureCreated`). Betroffene Tests machen `ChangeTracker.Clear()` vor Assertions,
weil Bulk-Ops am Change-Tracking vorbeischreiben. Die übrigen Testklassen nutzen
weiter InMemory. CI braucht weiterhin keinen Postgres-Container.
Stand: Build grün (nur die bekannte CS0618-Warnung, s. u.), 124/124 Tests grün.

### Offen / TODO

- Token-Rotation/Grace (ADR 0001) unverändert — bewusst nicht angefasst.
- Keine offenen Punkte aus den fünf Aufgaben.

## Prompt 16 — CI + Dependabot (2026-07-05)

### Umgesetzt

- **`.github/workflows/ci.yml`**: läuft bei Push/PR auf `main`.
  - Job `build-test`: .NET 8 Setup, `dotnet restore`, `dotnet build` (Release), `dotnet test`.
  - Job `vulnerability-scan`: `dotnet list package --vulnerable --include-transitive`,
    schlägt per grep auf die Ausgabe fehl, wenn verwundbare Pakete gefunden werden
    (der SDK-8-Befehl selbst liefert auch bei Funden Exit-Code 0).
  - Kein Postgres-Service-Container nötig: die Testsuite läuft komplett gegen
    EF Core InMemory (geprüft, keine Testcontainers/Npgsql-Nutzung in Tests).
- **`.github/dependabot.yml`**: nuget, weekly, Minor/Patch-Updates gruppiert
  (`nuget-minor-patch`), Majors als Einzel-PRs.
- **Dependency-Bumps**, damit der Vulnerability-Scan von Anfang an grün ist
  (alle Funde waren transitive Pakete hinter den auf 8.0.0 gepinnten Direkt-Deps):
  - `Microsoft.AspNetCore.Authentication.JwtBearer` 8.0.0 → 8.0.11
  - `Microsoft.EntityFrameworkCore.Design` 8.0.0 → 8.0.11
  - `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.0 → 8.0.11
  - `Microsoft.EntityFrameworkCore.InMemory` (Tests) 8.0.0 → 8.0.11
  - Danach: `dotnet list package --vulnerable --include-transitive` ohne Funde,
    Build grün, 121/121 Tests grün.

### Offen / TODO

- `-warnaserror` im Build-Step aktivieren, sobald die letzte Warnung behoben ist:
  CS0618 in `src/InvoiceApi/Services/PdfService.cs:105` — QuestPDF `MinimalBox()`
  ist obsolet, Umbenennung zu `Shrink()` (reines Rename, gleiche Semantik).
  Siehe TODO-Kommentar im Workflow.
- Dependabot-Config für das Repo `invoiceflow` (npm + github-actions) wird in
  einer separaten Session umgesetzt — nicht Teil dieses Repos.

## 2026-07-05 — CI-Fix: SQLitePCLRaw-Advisory (PR #1)

Der Job „Vulnerable dependencies" schlug auf dem PR fehl: GHSA-2m69-gcr7-jv3q (High)
in `SQLitePCLRaw.lib.e_sqlite3` 2.1.6, transitiv über `Microsoft.EntityFrameworkCore.Sqlite`
8.0.11 (nur Testprojekt). Die Advisory umfasst alle Versionen ≤ 2.1.11 (SQLite < 3.50.2);
Fix: direkte Referenz `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 im Testprojekt. Scan danach
ohne Funde, alle 132 Tests grün (inkl. SQLite-in-memory-AuthServiceTests).
