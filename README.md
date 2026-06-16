# Riftbound Rules Companion

Een **onofficieel** community-hulpmiddel dat de versnipperde Riftbound-regels
(Core Rules, Tournament Rules, errata, rulings, bans, changelogs) samenbrengt in
één doorzoekbare, altijd-actuele bron — met automatische **change-tracking** en
bronvermelding.

> Status: **MVP — change-tracker**. Q&A (GraphRAG), foto- en voice-lookup volgen.
> Zie [`docs/BRAINSTORM.md`](docs/BRAINSTORM.md) voor het volledige plan.

## Stack
- **Frontend:** Next.js (App Router) PWA — web + installeerbaar op telefoon.
- **Kennisbank:** Postgres + `pgvector` (relationeel + embeddings).
- **Graph (Fase 2):** Neo4j — kaart ↔ regel ↔ errata ↔ ban.
- **LLM (Fase 2):** Claude (Opus/Sonnet/Haiku) + Voyage embeddings.

## Self-host (Mac mini of VM) — vrijwel gratis

1. **Start de databases**
   ```bash
   cp .env.example .env
   docker compose up -d        # Postgres (+pgvector) en Neo4j
   ```
2. **Installeer & initialiseer**
   ```bash
   npm install
   npm run db:init             # schema + bronnen-register
   ```
3. **Eerste scan (change-tracker)**
   ```bash
   npm run ingest              # cron'baar, bv. dagelijks
   ```
4. **Start de app**
   ```bash
   npm run dev                 # http://localhost:3000
   ```

> Voor PWA-camera/mic in productie is HTTPS nodig — bij self-hosting thuis is
> **Cloudflare Tunnel** (gratis) een makkelijke route die je IP/poorten afschermt.

### Dagelijkse scan automatiseren
Cron op de host:
```
0 7 * * *  cd /pad/naar/RB-Rules && /usr/local/bin/npm run ingest >> ingest.log 2>&1
```

## Beheer (`/admin`)
Bronnen toevoegen/beheren (trust-tier, rang, cadans, aan/uit), verwijderen, en
**handmatig scannen**. Beveiligd met `ADMIN_PASSWORD` uit `.env` (niet ingesteld =
volledig vergrendeld). De bronnen-config (`config/sources.ts`) is alleen de seed;
daarna is `/admin` de bron van waarheid.

## Vraag & Conflicten
- **`/ask`** — stel een regelvraag; vector-RAG haalt relevante regeltekst op en
  Claude antwoordt **mét citaten** naar de bron (officieel > community).
- **`/conflicts`** — tegenstrijdigheden tussen officieel en community
  (tegenstrijdig / loopt-achter), gedetecteerd met AI.

## Kaart-database
Kaarten worden ingelezen via de [Riftcodex API](https://riftcodex.com) (open, geen
auth) en zijn **update-bestendig**: nieuwe sets/errata komen automatisch mee via
upsert. Cron: `npm run sync:cards`. Details + Riot-fallback: [`docs/CARD_INGEST.md`](docs/CARD_INGEST.md).
De Q&A gebruikt deze kaartgegevens al naast de regeltekst.

## Vanuit `/admin` te triggeren
- **Scan bronnen** (change-tracker)
- **Kaarten synchroniseren** (Riftcodex → `card`-tabel; nieuwe sets/updates)
- **Index opbouwen** (chunk + Voyage-embeddings → pgvector, voor Q&A)
- **Conflicten checken** (officieel vs. community)
- **Graph sync** (RuleSection- én Card-knopen → Neo4j; basis voor GraphRAG)

## AI-classificatie
Bij een gedetecteerde wijziging classificeert Claude automatisch **type + ernst +
uitleg ("wat betekent dit")** — mits AI-auth is geconfigureerd (zie
[`docs/AI_AUTH.md`](docs/AI_AUTH.md)). Zonder auth blijft de tracker gewoon werken
(kale diff, type "unknown").

## Mappen
- `config/sources.ts` — bronnen-register (trust-tier + rang); toevoegen = data-wijziging.
- `db/schema.sql` — Postgres-schema.
- `src/ingest/` — scan-pipeline (fetch → hash → diff → opslaan).
- `src/app/` — PWA: wijzigingen-feed + bronnen-overzicht.
- `docs/` — plan, kosten, scraping, datamodel.
