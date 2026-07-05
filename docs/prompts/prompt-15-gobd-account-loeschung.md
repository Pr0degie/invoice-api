# Prompt 15 — invoice-api: GoBD-konforme Account-Löschung (Anonymisierung)

**Repo:** `invoice-api` | **Session-Scope:** Datenmodell + AuthService + Tests; eigene Session, weil Migration

## Kontext

`DeleteAccountAsync` löscht den User hart und kaskadiert auf alle Rechnungen — inklusive finalisierter. Das kollidiert mit §147 AO: Buchungsbelege unterliegen der Aufbewahrungspflicht (seit der 2025er-Reform 8 Jahre), das DSGVO-Löschrecht greift für diese Belege nicht durch. Das Repo setzt GoBD sonst konsequent um (Immutability, PDF-/XML-Archiv, Storno statt Löschung — siehe `docs/adr/0002` und `docs/adr/0003`); die Account-Löschung ist die letzte Lücke. Ziel: personenbezogene Daten des Accounts entfernen, finalisierten Rechnungsbestand samt Archiv unverändert erhalten.

## Aufgabe

Entwirf und implementiere die Anonymisierungs-Variante von `DeleteAccountAsync`:

- **Drafts** dürfen weiterhin hart gelöscht werden (kein Beleg-Charakter).
- **Finalisierte Rechnungen (inkl. Storno/Cancelled)** und ihre archivierten PDFs/XMLs bleiben vollständig erhalten — die Snapshot-Daten auf der Rechnung (Sender, Empfänger, Beträge) sind Teil des Belegs und werden NICHT anonymisiert.
- Der **User-Datensatz** wird anonymisiert statt gelöscht: E-Mail durch nicht rückführbaren Platzhalter ersetzen (Unique-Constraint beachten, z. B. `deleted-{guid}@anonym.invalid`), PasswordHash invalidieren, Name/Adresse/Steuer-/Bankfelder nullen bzw. auf Platzhalter setzen, alle Refresh Tokens löschen. Ein Login ist danach unmöglich.
- Markiere den Zustand explizit (z. B. `DeletedAt`-Timestamp auf User), damit `/auth/me` u. ä. sauber 401 liefern statt einen Zombie-Account zu zeigen.
- Hat ein User **keine** finalisierten Rechnungen, ist Hard-Delete wie bisher erlaubt — das ist der häufige Fall (Test-Accounts) und spart Datenmüll.
- Schreib einen **ADR** (`docs/adr/0005-...`), der den Konflikt DSGVO-Löschrecht vs. §147 AO und die gewählte Lösung festhält, im Stil der bestehenden ADRs.

Entscheide selbst über Details wie Feldbenennung und Migrationsschnitt — du kennst nach dem Einlesen das Datenmodell. Wenn du auf eine Design-Entscheidung stößt, die das API-Verhalten für den Frontend-Client ändert (z. B. Response von DELETE /me), triff eine Empfehlung und setz sie um, statt zu fragen; dokumentiere sie im ADR.

## Akzeptanzkriterien

- EF-Migration vorhanden und läuft auf leerer wie befüllter DB durch.
- Tests: Hard-Delete-Pfad (nur Drafts), Anonymisierungs-Pfad (finalisierte Rechnung überlebt mit intaktem Archiv, User nicht mehr einloggbar, /me liefert 401), Unique-Email-Kollision beim Platzhalter ausgeschlossen.
- Bestehende Tests grün, ADR geschrieben.

## Abschluss (Pflicht, letzter Schritt der Session)

1. Aktualisiere die Projekt-Doku: `progress.md` (was wurde umgesetzt, was bleibt offen), `CLAUDE.md` nur falls sich Konventionen/Architektur geändert haben, betroffene ADRs.
2. Committe in logischen Einheiten mit aussagekräftigen Messages und pushe den Branch.
3. Die nächste Session startet ohne Kenntnis dieser Konversation — alles, was sie wissen muss, muss in Repo-Doku oder Commit-Messages stehen.

## Leitplanken

- Kein Feature-Ausbau darüber hinaus (kein Lösch-Scheduler nach Ablauf der 8 Jahre, kein Admin-UI — das wäre ein späterer Prompt).
- Fortschritt nur mit Belegen aus Tool-Ergebnissen melden; Zusammenfassung mit dem Ergebnis beginnen.
