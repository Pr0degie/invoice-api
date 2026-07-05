# Progress

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
