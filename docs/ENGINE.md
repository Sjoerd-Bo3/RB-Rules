# De Semantic Engine — visie, audit & sprint-plan

> Status: vastgesteld plan na de volledige codebase-audit (10-agent review, alle
> bevindingen adversarieel geverifieerd). De huidige Next.js-app is een **PoP**;
> de engine wordt gebouwd als **.NET 10 + SvelteKit** (Nocturne-patroon).
> Prioriteit: **kaarten eerst, decks gedeprioriteerd** (backlog).

## 1. Visie

Eén verbonden semantic/vector/graph-structuur (Postgres+pgvector + Neo4j) met
kaarten, regels, errata, bans en rulings, die mogelijk maakt:
1. rulings-Q&A met exacte §-citaten,
2. ontdekken van rare/onverwachte kaart-interacties,
3. semantisch kaarten zoeken ("kaarten die lijken op X", "kaarten die Y doen"),
4. *(backlog)* decks invoeren → legaliteit/synergie/suggesties, goede decks vinden.

**Alles is compositie van vier fundamenten:**
- **F1 — Core Rules als sectie-boom** (PDF-ingest, hiërarchie, sectie = chunk)
- **F2 — Card-embeddings** (pgvector, getypte kolom + HNSW)
- **F3 — Mechaniek-extractie** (LLM-mining: Card→HAS_MECHANIC→Mechanic→DEFINED_BY→RuleSection)
- **F4 — Deck-model** *(backlog)*

## 2. Doel-ontologie (v2)

```
(:Card {id, name, type, energy, might, embedding_ref})
  -[:FROM_SET]-> (:Set)          -[:HAS_DOMAIN]-> (:Domain)
  -[:HAS_TAG]->  (:Tag)          // facties/tribes (Mech, Piltover) — GEEN keyword
  -[:HAS_MECHANIC]-> (:Mechanic {name})            // Accelerate, Tank, Hidden…
                     -[:DEFINED_BY]-> (:RuleSection {code, doc_version})
  -[:HAS_ERRATA]-> (:Erratum {text, effective_from, source, trust})
  -[:BANNED_IN]->  (:BanEntry {format, effective_from, effective_until, source})
  -[:INTERACTS_WITH {kind, verified, explanation}]-> (:Card)   // gemined + LLM-geverifieerd
(:Ruling {text, status, date}) -[:APPLIES_TO]-> (:Card|:Mechanic|:RuleSection)
(:RuleSection) -[:REFERENCES]-> (:RuleSection)    // "zie regel 7xx"
// backlog: (:Deck)-[:CONTAINS {count, zone}]->(:Card), (:Archetype), CO_OCCURS
```
Elke edge draagt provenance: `source`, `trust`, `date`.

## 3. Audit-samenvatting (PoP, geverifieerd)

**Werkt:** change-tracker (hash→diff→AI-classificatie, push bij high), bronnen-
register met trust/rank, kaart-sync (957 kaarten, Riftcodex→Riot-fallback),
correctie-loop, beheer-UI + logs, PWA/deploy-keten.

**De vier grote gaten t.o.v. de visie:**
| # | Gat | Kern |
|---|---|---|
| 1 | **Core Rules PDF wordt nergens geingest** | "Regels" = landingspagina + 1 nieuwsartikel + community-errata. Sectie-regex vangt `601.2.d` niet; PDF-parser is dode enum (zou binaire bytes opslaan); RuleSection-graph effectief leeg. |
| 2 | **Kaarten hebben geen embeddings** | Semantisch kaartzoeken architectureel onmogelijk; kaartherkenning = exact-substring over hele tabel. |
| 3 | **"Keywords" zijn facties, geen mechanieken** | Mech/Piltover i.p.v. Accelerate/Tank/Hidden; echte mechanieken ongeparseerd in `text_plain`; nul Card↔Card-edges. |
| 4 | **Decks bestaan nergens** | 1 grep-hit in een doc. *(bewust backlog)* |

**Fundament-schulden:** geen tests; `schema.sql`-als-migratie breekt bij eerste
niet-additieve wijziging; pgvector dimensieloos zonder ANN (provider-wissel =
crash); NL-vragen ↔ EN-bronnen met EN-primair embeddingmodel; correcties
verouderen geruisloos; Neo4j zonder constraints, N+1-sync, en op de server
default UIT terwijl graph-code stil degradeert; `cadence` is dode config;
ban/errata via tekst-heuristiek ("Bandle" matcht "ban"); auto-deploy zonder gate.

## 4. Doelarchitectuur

```
Caddy (bestaand) → riftbound.bo3.dev
  ├─ rb-web   SvelteKit — feed, /ask, kaart-browser, interactie-verkenner
  ├─ rb-api   .NET 10 — domeinmodel, EF Core-migrations, Npgsql+pgvector(typed+HNSW),
  │           Neo4j.Driver (constraints, batched UNWIND), BackgroundServices
  │           voor ingest/sync (geen cron), xUnit, OpenAPI
  ├─ rb-ai    TS-sidecar — Claude Agent SDK op abonnement (classify/answer/mine)
  └─ postgres · neo4j (altijd aan) · ollama (embeddings, meertalig model)
```
- PoP blijft draaien tot pariteit; data (schema, snapshots, kaarten, correcties)
  migreert 1-op-1.
- Embeddings: meertalig model (bv. `bge-m3`) i.p.v. `nomic-embed-text` vanwege
  NL↔EN; dimensie + model vastgelegd als provenance-kolom.

## 5. Sprints

**S0 — Scaffold & port.** rb-api/rb-web/rb-ai in Nocturne-patroon; EF-migrations;
getypte vectors + HNSW; Neo4j-constraints; CI mét test-gate vóór GHCR; port van
change-tracker + kaart-sync + admin-basis. *Klaar als: PoP-functionaliteit draait
getest in de nieuwe stack.*

**S1 — Kaart-semantiek (F2+F3) — prioriteit.** Card-embeddings + ANN; LLM-mining
van mechanieken/triggers/effecten over alle kaarten (gesloten vocabulaire, batch,
herhaalbaar per set); Tag vs Mechanic gesplitst in de graph; kaart-browser +
detailpagina (oracle-tekst, ban-status, graph-buren); semantisch zoeken-UI.
*Klaar als: "kaarten die lijken op X" en "kaarten die Y doen" werken.*

**S2 — Regels-ruggengraat (F1).** Core Rules + Tournament Rules PDF-ingest →
sectie-boom; hybrid search (vector + BM25/pg_trgm + RRF); gestructureerde
ban/errata-entiteiten met effective-datums en bron-provenance; §-exacte citaten
met documentversie. *Klaar als: "wat zegt §601.2.d?" een letterlijk citaat geeft.*

**S3 — Interactie-ontdekking (F1+F2+F3).** Interactie-resolver ("hoe werkt
Deflect tegen Hidden?" → gecombineerd antwoord met regelpad); trigger↔effect-
padqueries als kandidaat-generator + LLM-verificatie → `INTERACTS_WITH`-edges;
"wat breekt er met kaart X?"-verkenner; rulings-precedentendatabank (correcties →
Ruling-knopen met invalidatie bij officiële updates). *Klaar als: rare-interactie-
vragen nuttige, citeerbare antwoorden geven.*

**Backlog (gedeprioriteerd):** deck-model + deck-code-import (C#-port van
LoRDeckCodes-variant), Piltover Archive-ingest (~10k decks; browser-UA werkt
vanaf DC-IP — 403 was UA-gebaseerd), melee.gg toernooi-decks, deck-check,
hand-simulator, co-occurrence/archetypes, vervangings-suggesties, meta-explorer.
Deck-research staat klaar; oppakken wanneer gewenst.

## 6. Open keuzes (defaults tenzij anders besloten)
- **AI-auth:** TS-sidecar op Claude-abonnement (default) vs API-key in .NET.
- **Gateway:** direct Caddy → web/api (default); YARP pas bij serieuze auth/OIDC.
- **Interactie-mining:** graph-kandidaten + LLM-verificatie (default), geen
  brute-force over alle ~450k paren.
