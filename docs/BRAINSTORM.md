# Riftbound Rules Companion — Brainstorm & Plan

> Status: brainstorm / research fase. Nog geen implementatie.
> Laatst bijgewerkt: 2026-06-15

Een onofficieel community-hulpmiddel dat de versnipperde Riftbound-regels
(Core Rules, Tournament Rules, errata, rulings, bans, changelogs) samenbrengt in
één doorzoekbare, altijd-actuele bron — met automatische change-tracking,
bronvermelding en AI-vraagbaak (tekst / voice / foto).

---

## 1. Het probleem

Riftbound-regelinformatie staat verspreid over veel bronnen, wordt regelmatig
geüpdatet (errata, bans, rules updates) en is onoverzichtelijk. Spelers weten
niet wat de actuele/officiële regel is, waar die vandaan komt, of wat er recent
is veranderd.

## 2. Bronnen-landschap (research)

### Officieel = bron van waarheid (Riot)
- **Rules Hub** (centrale index, met "last updated"-datums):
  https://riftbound.leagueoflegends.com/en-us/rules-hub/
- **Core Rules (CR)** — PDF (bv. laatst 30-3-2026)
- **Tournament Rules (TR)** — PDF (bv. laatst 29-4-2026)
- **Patch Notes** — Core Rules / Spiritforged / Unleashed
- **Errata** — Origins / Spiritforged / Unleashed
- **Ban list** — staat op Rules Hub + announcements
- **Tournament Rules Updates & Changelogs** — maandelijkse announcements

### Community = aanvulling / cross-check (lagere trust)
- riftbound.gg (mirrors + changelogs)
- Piltover Archive (kaartendatabase + deckbuilder) — piltoverarchive.com
- Mobalytics ban list, RiftRank, Rift Mana, Fextralife wiki

### Gestructureerde kaartdata (voor foto-feature)
- Piltover Archive / Riftcodex API
- GitHub: vikkumar2021/RiftboundCardDatabase (cards.json), Moke96/RiftBuilder

> **Kerninzicht:** de officiële Rules Hub + gelinkte PDF's vormen de ruggengraat.
> We bewaken die op datum + hash; community-bronnen alleen als aanvulling/cross-check.

## 3. Architectuur

```
INGEST (cron, 1×/dag of 1×/week)
  Rules Hub + PDF's + community → hash/datum-check →
  alleen bij wijziging: PDF→tekst, chunken, embedden
  → opslaan mét provenance (bron, datum, versie, hash)
        │
        ▼
KENNISBANK  Postgres + pgvector
  - rules-chunks (vector) met provenance
  - kaarten + errata (gestructureerd)
  - changelog / diff-historie
  - correctie-/override-laag (zie §6)
        │
        ├──► CHANGE-TRACKER: diff oud↔nieuw, LLM vat veranderingen samen +
        │     flagt tegenstrijdigheden official↔community
        │
        └──► VRAAG & ANTWOORD (RAG): tekst/voice/foto → retrieval →
              Claude antwoordt MÉT citaten (vision leest de kaart)
```

### Stack (voorgesteld)
- **Frontend:** Next.js **PWA** (web + installeerbaar op telefoon, camera + mic).
  Later native iOS/Android via **Capacitor**-wrapper op dezelfde codebase.
- **Kennisbank:** Postgres + `pgvector` (bv. Supabase, managed).
- **Ingestie:** geplande job (cron / GitHub Action) met hash-/diff-check.
- **LLM:** Claude — Opus 4.8 voor lastige rulings, Sonnet 4.6 voor goedkope
  retrieval-antwoorden, **vision** voor foto's. Antwoorden altijd mét bron.
- **Embeddings:** Voyage AI (Anthropic heeft geen eigen embeddings-endpoint).

## 4. "Self-learning" — pragmatisch

Geen dure fine-tuning. In plaats daarvan: een continu bijgewerkte RAG-kennisbank
+ een feedback-/correctie-laag die met hoge prioriteit wordt meegezocht. Voelt
zelflerend, blijft uitlegbaar en terug te draaien.

## 5. Bronnen-register met relevantie/rank

Elke bron krijgt: `trust_tier` (1 officieel … 4 overig), `rank/weight`
(fijnafstemming), `enabled`, `cadence` (scanfrequentie), `parser`
(pdf/html/json-api), `last_hash` + `last_checked`. Elke chunk onthoudt herkomst
(bron, URL, sectie/pagina, datum, hash). Rank bepaalt: zoekvolgorde,
conflictoplossing (officieel verslaat community) en antwoordsamenstelling.

## 6. Correctie-/override-laag (recursive self-improvement)

- Feedback op een antwoord (duim omlaag + correctie, of judge voegt ruling toe)
  wordt opgeslagen als aparte **override-entry** met herkomst (wie/wanneer/bron).
- Wordt bij volgende vragen met hoge prioriteit meegezocht → systeem verbetert.
- Vangrails: verificatie-status (`unverified` → `verified`), gebruikersniveaus
  (judge > speler), nooit stilletjes officiële tekst overschrijven, bij nieuwe
  officiële update die de correctie tegenspreekt → automatisch flaggen.

## 7. Kanttekening (IP/legaal)

Onofficieel fan-project. Officiële tekst citeren + linken i.p.v. integraal
kopiëren; "unofficial fan project" duidelijk vermelden; Riot's fan-content
policy respecteren.

## 8. Beslissingen tot nu toe

- **Platform:** PWA-first (web + Android), native store-app later via Capacitor.
- **MVP-kern:** change-tracker / dashboard.
- **Fundamenten vanaf dag 1:** bronnen-register (rank/trust) + correctie-laag.

## 9. Roadmap

- **Fase 0** — Scaffolding: repo-structuur, bronnenlijst als config.
- **Fase 1 (MVP)** — Ingest + change-tracker: bronnen bewaken, diff-dashboard
  "wat is er veranderd sinds X" met bronvermelding.
- **Fase 2** — Tekst-Q&A (RAG) met citaten.
- **Fase 3** — Foto: kaart/bord herkennen → errata/rulings ophalen.
- **Fase 4** — Voice + feedback-/correctie-loop.

## 10. Open deep-dives (nog te doen)

1. Change-tracker concreet (dashboard/UX per scan). ← volgende
2. Kosten & hosting.
3. Scraping-aanpak + standaard community-bronnen.
4. Datamodel (concrete tabellen).
