# Prompt 16 — CI für invoice-api + Dependabot für beide Repos

**Repos:** `invoice-api` (Hauptarbeit) + `invoiceflow` (nur Dependabot-Config) | Kurze Session

## Kontext

invoice-api hat 459 Tests, aber keine CI — sie laufen nur, wenn jemand daran denkt. invoiceflow hat bereits `.github/workflows/ci.yml` als Stilreferenz. Ziel: Jede Regression und jede verwundbare Dependency fällt künftig automatisch auf.

## Aufgaben

1. **invoice-api: `.github/workflows/ci.yml`** — bei Push/PR auf main: .NET 8 Setup, `dotnet restore`, `dotnet build -warnaserror` (falls der Build das heute schon sauber hergibt — prüfe das zuerst; wenn nicht, ohne `-warnaserror` starten und einen TODO-Kommentar hinterlassen), `dotnet test`. Zweiter Job oder Step: `dotnet list package --vulnerable --include-transitive`, der bei Funden fehlschlägt.
2. **Dependabot beide Repos** — `.github/dependabot.yml`: nuget (invoice-api), npm + github-actions (invoiceflow), weekly, gruppierte Minor/Patch-Updates, damit nicht zehn Einzel-PRs pro Woche aufschlagen.
3. Falls Tests eine Postgres-Instanz brauchen (prüfen!): Service-Container im Workflow. Wenn die Suite rein in-memory läuft, nichts hinzufügen.

## Akzeptanzkriterien

- Workflow läuft lokal nachvollziehbar durch (`dotnet build && dotnet test` grün) und die YAML ist mit `actionlint` o. ä. validiert bzw. syntaktisch sauber.
- Dependabot-Configs in beiden Repos committet.

## Abschluss (Pflicht, letzter Schritt der Session)

1. Aktualisiere die Projekt-Doku: `progress.md` (was wurde umgesetzt, was bleibt offen), `CLAUDE.md` nur falls sich Konventionen/Architektur geändert haben, betroffene ADRs.
2. Committe in logischen Einheiten mit aussagekräftigen Messages und pushe den Branch.
3. Die nächste Session startet ohne Kenntnis dieser Konversation — alles, was sie wissen muss, muss in Repo-Doku oder Commit-Messages stehen.

## Leitplanken

- Kein Deploy-Step, kein Coverage-Gate, keine Matrix über mehrere .NET-Versionen — nur Build, Test, Vulnerability-Check.
- Zusammenfassung: Ergebnis zuerst, dann was wo liegt.
