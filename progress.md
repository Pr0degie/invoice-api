# Progress

## Prompt 17 — .NET 8 → .NET 10 LTS Upgrade (2026-07-17)

### Umgesetzt

- **TFM `net8.0` → `net10.0`** in beiden csproj. Pakete: JwtBearer +
  EFCore.Design 8.0.28 → 10.0.10, Npgsql.EFCore 8.0.11 → 10.0.3,
  Serilog.AspNetCore 8.0.3 → 10.0.0 (erzwang Sinks.Console 5.0.1 → 6.1.1,
  NU1605), Tests: EFCore.InMemory/Sqlite → 10.0.10, Test.Sdk 17.14.1 → 18.8.1.
  QuestPDF/MailKit/ZUGFeRD/BCrypt/Swashbuckle **unverändert** (laufen unter
  net10.0, kein Bump um des Bumps willen).
- **Einziger Codefix:** `KnownNetworks.Clear()` → `KnownIPNetworks.Clear()`
  in `Program.cs` (ASP.NET-Core-10-Obsoletion, s. Recherche unten).
- **Dockerfile:** `sdk:10.0`/`aspnet:10.0`. Die 10.0-Images sind **Ubuntu
  24.04** (Debian eingestellt) — `adduser` fehlt im Runtime-Image, deshalb
  jetzt der in den .NET-Images eingebaute Non-Root-User `app` statt des
  selbst angelegten `appuser`. fontconfig/fonts-liberation unverändert.
- **CI:** `setup-dotnet` 8.0.x → 10.0.x in beiden Jobs, Scan-Job unverändert.
- **ADR 0007** (`docs/adr/0007-dotnet-10-lts-upgrade.md`): .NET 10 LTS statt
  9 — 9 hat dasselbe EOL-Datum wie 8 (10.11.2026), 10 läuft bis Nov 2028.
- **Verifikation:** `dotnet build -warnaserror` 0 Warnungen; **191/191 Tests
  grün** (CLAUDE.md-Testzahlen auf 191 konsolidiert); Container-Smoke-Test
  gegen `docker compose`: Register → Verify (Token aus Log) → Login →
  Profil-PATCH → Rechnung anlegen (Umlaute) → Finalize (`2026-001`) → PDF
  mit **eingebetteten Liberation-Fonts** (kein textloser Render unter
  Ubuntu-Basis) → XRechnung-CII-XML → Swagger UI (20 Pfade). Auth-Rate-Limit
  (5/min) griff dabei nachweislich.
- Lokales SDK: 10.0.302 (user-lokal `%USERPROFILE%\.dotnet10`, da der
  Maschinen-Install nur SDK 8/9 hat — für Builds `DOTNET_ROOT`/`PATH`
  entsprechend setzen oder SDK 10 systemweit nachinstallieren).

### Offen / ADR-Kandidat

- **Swashbuckle 6.9.0 → Microsoft.AspNetCore.OpenApi (oder Swashbuckle 10.x):**
  6.9.0 baut und läuft unter net10.0 (Swagger UI verifiziert), hängt aber an
  Microsoft.OpenApi 1.x, während das ASP.NET-Core-10-Ökosystem auf OpenApi 2.x
  (OpenAPI 3.1) umgestellt hat. Bewusst NICHT Teil dieser Session —
  eigene Entscheidung mit eigenem ADR, wenn `Swagger:Enabled`-Flag für
  Production (CLAUDE.md §7) angegangen wird.

### Breaking-Changes-Recherche (Evidenz, vor dem Umbau erhoben)

Quellen: offizielle Breaking-Changes-Listen .NET 9 + 10, ASP.NET Core 9 + 10,
EF Core 9 + 10, Npgsql-Release-Notes 9 + 10 (EF- und ADO-Ebene). Zwei
Major-Sprünge — beide Listen gelten kumulativ. Bewertung gegen dieses Repo:

**Treffer (JA):**

- **EF9: `MigrateAsync()` wirft bei pending model changes** — wir migrieren
  beim Start (`Program.cs`). Jede Model-Änderung ohne generierte Migration
  lässt den Boot künftig hart fehlschlagen (`PendingModelChangesWarning`).
  Kein Codefix nötig, aber neue Disziplin: Model-Change ⇒ sofort Migration.
  Positiver Nebeneffekt: EF9+ lockt Migrationen gegen konkurrierende Replikas.
- **ASP.NET Core 10: `ForwardedHeadersOptions.KnownNetworks` obsolet**
  (Umstieg auf `KnownIPNetworks`, `System.Net.IPNetwork`) — wir rufen
  `KnownNetworks.Clear()` in `Program.cs` auf; mit `-warnaserror` ein
  Build-Brecher. Einziger nötiger Codefix im Repo.
- **.NET 10 Container-Images sind Ubuntu 24.04 „Noble"** — Debian-Varianten
  gibt es für .NET 10 nicht mehr. `apt-get` im Dockerfile trifft Ubuntu-Repos;
  `fontconfig`/`fonts-liberation` heißen dort gleich. PDF-Font-Rendering muss
  im Container verifiziert werden (nicht nur der Build).
- **SDK 10: `dotnet restore` auditiert auch transitive Pakete** — neue
  NU1902/NU1903-Warnungen können mit `-warnaserror` den Build brechen,
  wenn eine Advisory auftaucht (deckt sich inhaltlich mit unserem
  Vulnerability-Scan-Job).
- **.NET 9: DI-Validierung (`ValidateOnBuild`/`ValidateScopes`) im
  Development-Env aktiv** — Scoped-in-Singleton-Fehler knallen jetzt beim
  Start. Unser `EmailBackgroundService` bezieht scoped Services korrekt über
  Scope-Factory; verifiziert durch Container-Start (Development).

**Prüfen (durch Build/Tests/Smoke-Test abgedeckt):**

- Swashbuckle 6.9.0 unter net10.0: hängt an Microsoft.OpenApi 1.x, ASP.NET
  Core 10 bringt OpenApi 2.x-Ökosystem. Entscheid per Build + `/swagger`-Test.
- Microsoft.Data.Sqlite 10 (nur Testprojekt): `DateTime`/`DateTimeOffset`
  ohne Offset gilt beim Lesen jetzt als UTC statt Local — kann
  Zeitzonen-Asserts in den SQLite-basierten `AuthServiceTests` kippen
  (Escape-Hatch: AppContext-Switch `Pre10TimeZoneHandling`).
- STJ validiert Property-Namen-Konflikte in DTOs; Config-Binder behält
  `null`-Werte in Arrays (`Cors:AllowedOrigins`). Beides testabgedeckt.
- EF10: `Contains`-Queries erzeugen `IN (@p1,…)` statt Array-Param;
  SQL-Parameternamen vereinfacht — nur relevant für SQL-Snapshots (haben wir
  nicht).
- Npgsql EF 9: Guid-PKs werden clientseitig als **UUIDv7** (sequentiell)
  statt v4 generiert — funktional harmlos, bessere Index-Lokalität.

**Nicht-Treffer (geprüft, betrifft uns nicht):** BinaryFormatter-Entfernung
(ungenutzt), HttpClientFactory-Änderungen (keine ausgehenden HTTP-Calls),
JwtBearer-Handler (keine gelisteten Breaking Changes in 9/10; transitive
IdentityModel-Bumps durch Testsuite abgedeckt), Serilog-Request-Logging
(kein Breaking Change; `Serilog.AspNetCore` 10.0.0 ist das zu net10.0
passende Major), Rate-Limiting-Stack (keine Änderungen), Kestrel/TLS
(terminiert Traefik), Cookie-Auth-Redirects (Bearer-only), Postgres-Enums /
`MapEnum` (Enums sind STJ-Strings), EF-Migrate-in-Transaktion (wir wrappen
nicht), Cosmos/SQL-Server-Punkte, `date`/`time`→`DateOnly`-ADO-Mapping (nur
Raw-SQL, haben wir nicht), Sync-API-Deprecation Npgsql 10 (wir sind async).

**Paket-Kompatibilität:** QuestPDF 2024.12.3, MailKit 4.17.0, ZUGFeRD-csharp
18.0.0, BCrypt.Net-Next 4.2.0 laden unter net10.0 (net6+/netstandard2.0-
Targets) — kein Bump nötig. QuestPDF braucht seit 2024.3 kein fontconfig mehr
(eigene native Builds); der Font-Fix im Dockerfile bleibt trotzdem nötig,
weil das aspnet-Image keine Fonts mitbringt.

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
