# ADR 0007 — Framework-Sprung .NET 8 → .NET 10 LTS (unter Auslassung von .NET 9)

Status: accepted · Date: 2026-07-17 · Scope: invoice-api (Prompt 17)

## Context

.NET 8 (LTS) erreicht am **10.11.2026** das End of Support — danach gibt es
keine Security-Patches mehr. Die API ist eine GoBD-relevante Rechnungs-API in
Produktion (Hetzner/Coolify); eine ungepatchte Runtime ist keine Option.

Kandidaten:

- **.NET 9 (STS):** End of Support ebenfalls **10.11.2026** — dasselbe
  EOL-Datum wie .NET 8. Ein Upgrade dorthin kauft null zusätzliche Laufzeit.
- **.NET 10 (LTS):** Support bis **November 2028**. Zwei Major-Sprünge in
  einem Schritt (8→9→10), beide Breaking-Changes-Listen gelten kumulativ.

## Decision

Direktes Upgrade auf **.NET 10 LTS**. Reines Runtime-/Toolchain-Upgrade ohne
Refactoring, ohne neue Sprachfeatures, ohne API-Änderungen.

Konkret:

- `net10.0` in beiden csproj; Microsoft.*-/Npgsql-Pakete auf die zu .NET 10
  gehörenden Majors (EF Core/JwtBearer 10.0.10, Npgsql.EFCore 10.0.3,
  Serilog.AspNetCore 10.0.0, Test.Sdk 18.8.1). Serilog.Sinks.Console 5.0.1 →
  6.1.1 (von Serilog.AspNetCore 10 erzwungen, NU1605).
- Nicht versionsgebundene Pakete (QuestPDF 2024.12.3, MailKit 4.17.0,
  ZUGFeRD-csharp 18.0.0, BCrypt.Net-Next 4.2.0, Swashbuckle 6.9.0) bleiben
  unangetastet — sie laden und bauen unter net10.0 (verifiziert). Kein Bump
  um des Bumps willen.
- Einziger Codefix: `ForwardedHeadersOptions.KnownNetworks` →
  `KnownIPNetworks` (in ASP.NET Core 10 obsolet; bricht `-warnaserror`).
- Docker-Basisimages `sdk:10.0`/`aspnet:10.0`. **Achtung:** die 10.0-Images
  basieren auf Ubuntu 24.04 (Debian-Varianten eingestellt). Folge hier:
  `adduser` existiert im Runtime-Image nicht mehr → der eingebaute Non-Root-
  User `app` der .NET-Images ersetzt den selbst angelegten `appuser`. Der
  fontconfig/fonts-liberation-Fix für QuestPDF funktioniert unter Ubuntu
  unverändert (Pakete heißen gleich); PDF-Font-Rendering im Container
  verifiziert.

## Consequences

- Security-Patches bis November 2028; nächster geplanter Sprung: .NET 12 LTS
  (~2028).
- EF Core 9+ wirft beim Start-`MigrateAsync()` eine Exception, wenn das Model
  ungenerierte Änderungen hat (`PendingModelChangesWarning`) — Model-Änderung
  ohne Migration schlägt jetzt beim Boot fehl statt still zu driften. Neue
  Disziplin: jede Model-Änderung braucht sofort ihre Migration.
- SDK 10 auditiert bei `restore` auch transitive Pakete (NU1902/NU1903). Mit
  `-warnaserror` kann eine neu veröffentlichte Advisory den Build brechen —
  gewollt (deckt sich mit dem Vulnerability-Scan-Job), aber als mögliche
  CI-Fehlerquelle dokumentiert.
- Guid-PKs werden von Npgsql EF 9+ clientseitig als UUIDv7 (sequentiell)
  generiert — funktional identisch, bessere Index-Lokalität.
- Swashbuckle 6.9.0 läuft unter .NET 10, ist aber die Alt-Linie; die
  Migration auf `Microsoft.AspNetCore.OpenApi` (oder Swashbuckle 10.x) ist
  ein eigener ADR-Kandidat (siehe progress.md, Prompt 17).

## Alternatives considered

- **.NET 9 als Zwischenschritt:** verworfen — gleiches EOL wie .NET 8,
  doppelter Migrationsaufwand für null Laufzeitgewinn.
- **Auf .NET 8 bleiben und EOL überbrücken:** verworfen — GoBD-relevante
  Produktions-API ohne Security-Patches ist kein vertretbarer Zustand.
