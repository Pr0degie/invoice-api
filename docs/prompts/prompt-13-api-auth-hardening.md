# Prompt 13 — invoice-api: Auth- & API-Härtung

**Repo:** `invoice-api` | **Session-Scope:** nur Backend, keine Frontend-Änderungen

## Kontext

InvoiceFlow ist ein Rechnungstool mit echten Nutzer- und Finanzdaten. Ein Security-Review hat fünf Punkte ergeben, die alle im Auth-/API-Layer liegen und deshalb in einer Session zusammen abgearbeitet werden. Lies vorab `CLAUDE.md` und `docs/adr/0001-refresh-token-rotation-grace.md` — die bestehende Token-Rotation ist bewusst so gebaut und bleibt unverändert.

## Aufgaben

1. **Timing-sichere Login-Prüfung** (`AuthService.LoginAsync`): Wenn kein User zur E-Mail existiert, wird `Verify` aktuell übersprungen — die Antwortzeit verrät, ob die E-Mail registriert ist. Verifiziere in diesem Fall gegen einen statischen Dummy-BCrypt-Hash, sodass beide Pfade dieselbe Arbeit leisten. Fehlermeldung bleibt identisch ("Invalid credentials.").

2. **Passwort-Obergrenze + BCrypt-Workfactor pinnen**: BCrypt wertet nur die ersten 72 Bytes aus. Setze `[MaxLength(128)]` auf `Password` (RegisterDto) und `NewPassword` (ChangePasswordDto). Pinne den Workfactor in `BCryptPasswordHasher` explizit auf 12 statt Library-Default. Bestehende Hashes müssen weiter verifizierbar bleiben (BCrypt kodiert den Cost im Hash — verifiziere das mit einem Test).

3. **TLS-Zertifikatsprüfung zur DB**: `ParseDatabaseUrl` in `Program.cs` setzt `Trust Server Certificate=true` und schaltet damit die Zertifikatsvalidierung ab. Mach das konfigurierbar (env `Database__TrustServerCertificate`, Default `false`) und dokumentiere in `.env.example`, dass Railway-interne Verbindungen es ggf. brauchen. Der sichere Modus ist der Default.

4. **`/health` absichern**: Der Endpoint ist anonym, ohne Rate Limit und macht pro Aufruf einen DB-Roundtrip — ein billiger Hebel gegen die DB. Cache das Ergebnis in-memory für ~10 Sekunden oder gib dem Endpoint ein eigenes kleines Fixed-Window-Limit. Wähle den einfacheren Weg, beides zusammen ist nicht nötig.

5. **EF-Bulk-Operationen**: `LoginAsync` (Expired-Token-Housekeeping), `ChangePasswordAsync` und die Theft-Response in `RefreshAsync` laden Token-Listen in den Speicher, um sie dann zu löschen/updaten. Ersetze das durch `ExecuteDeleteAsync` / `ExecuteUpdateAsync`. Achtung: `ExecuteUpdateAsync` läuft am DbContext-Change-Tracking vorbei — prüfe, ob Tests mit dem InMemory-Provider betroffen sind, und passe sie ggf. auf SQLite/Testcontainers-kompatible Assertions an, statt die Produktionslogik zu verbiegen.

## Akzeptanzkriterien

- Alle bestehenden Tests grün, neue Tests für 1 und 2 (Timing-Pfad: gleiches Verhalten bei unbekannter Mail; Cost-12-Hash wird erzeugt, alter Cost-11-Hash verifiziert weiter).
- `dotnet build` ohne neue Warnings.
- `.env.example` und ggf. betroffene ADR-/Doku-Stellen aktualisiert.

## Abschluss (Pflicht, letzter Schritt der Session)

1. Aktualisiere die Projekt-Doku: `progress.md` (was wurde umgesetzt, was bleibt offen), `CLAUDE.md` nur falls sich Konventionen/Architektur geändert haben, betroffene ADRs.
2. Committe in logischen Einheiten mit aussagekräftigen Messages und pushe den Branch.
3. Die nächste Session startet ohne Kenntnis dieser Konversation — alles, was sie wissen muss, muss in Repo-Doku oder Commit-Messages stehen.

## Leitplanken

- Baue nichts über die fünf Aufgaben hinaus — kein Refactoring drumherum, keine neuen Abstraktionen, keine Validierung für Fälle, die nicht eintreten können. Die einfachste Lösung, die sauber funktioniert, gewinnt.
- Melde nur Fortschritt, den du mit einem Tool-Ergebnis aus dieser Session belegen kannst. Wenn Tests fehlschlagen, zeig den Output; wenn etwas offen bleibt, sag es explizit.
- Beginne deine Abschluss-Zusammenfassung mit dem Ergebnis in einem Satz, danach die Details. Vollständige Sätze, kein Arbeits-Kürzelstil.
