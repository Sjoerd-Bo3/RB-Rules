# Kaart-ingest & updates (sets / errata)

Hoe de kaart-database wordt ingelezen en **update-bestendig** blijft voor nieuwe
sets en wijzigingen.

## Bron 1 (primair): Riftcodex API
Open REST-API, JSON, geen auth. Base: `https://api.riftcodex.com`
(overschrijfbaar via `RIFTCODEX_BASE`).

- **`GET /sets?page=&size=100`** — alle sets (`set_id`, `name`, `published_on`,
  `card_count`).
- **`GET /cards?set_id=…&page=&size=100`** — kaarten per set. Velden:
  `riftbound_id`, `name`, `collector_number`, `attributes.{energy,might,power}`,
  `classification.{type,supertype,rarity,domain[]}`, `text.plain`,
  `set.{set_id,label}`, `media.image_url`, `tags[]`.

**Implementatie:** `src/ingest/cards.ts` → `syncCards()` haalt eerst `/sets`,
daarna per set alle kaarten, en **upsert** ze (`ON CONFLICT … DO UPDATE`) in de
`card`/`card_set`-tabellen. Idempotent.

## Update-strategie (nieuwe sets / errata)
- Nieuwe set verschijnt automatisch in `/sets` → de eerstvolgende sync pakt 'm mee.
- Gewijzigde kaarten (errata aan de kaarttekst) worden geüpsert op `riftbound_id`.
- **Cron** (bv. wekelijks, naast de dagelijkse regel-scan):
  ```
  0 6 * * 1  cd /pad/naar/RB-Rules && docker compose run --rm app npm run sync:cards >> cards.log 2>&1
  ```
- Of vanuit **`/admin` → "Kaarten synchroniseren"** (en daarna "Graph sync").

## Bron 2 (authoritatief, fallback): Riot card-gallery — geïmplementeerd
Riot's eigen data zit in de Next.js-build van de officiële site:
`https://riftbound.leagueoflegends.com/_next/data/{BUILD_ID}/en-us/card-gallery.json`
Het `BUILD_ID` wordt automatisch gedetecteerd uit de HTML van de card-gallery-pagina.
`src/ingest/riot.ts` parseert alle ~957 kaarten (velden: type, rarity, domains via
`domain.values`, energy/might, set, tags, tekst) en upsert ze in dezelfde tabellen.

**Cloudflare op datacenter-IP's.** Vanaf de Azure-VM blokkeert Cloudflare
`api.riftcodex.com`. Daarom: `CARD_SOURCE=auto` (default) probeert Riftcodex en valt
bij een fout automatisch terug op de Riot-gallery (die datacenter-IP's niet blokkeert).
Forceer met `CARD_SOURCE=riot` of `=riftcodex`.

## Cloudflare-geblokkeerde bronnen (proxy)
Bronnen die datacenter-IP's blokkeren (bv. de Mobalytics ban-mirror) bereik je via
`OUTBOUND_PROXY` (vorm `http://user:pass@host:port`) — alle externe fetches lopen er
dan doorheen. De Mobalytics-bron staat daarom **standaard uit**; officiële bans komen
al via de Rules Hub (trust 1).

## Van DB naar graph
`src/lib/neo4j.ts → syncCardGraph()` bouwt uit de `card`-tabel:
```
(:Card {id, name, type, rarity, energy, might})
  -[:FROM_SET]->   (:Set {id, label})
  -[:HAS_DOMAIN]-> (:Domain {name})
  -[:HAS_KEYWORD]->(:Keyword {name})   // uit tags
```
Volgende stappen voor volledige GraphRAG: `(:Keyword)-[:DEFINED_BY]->(:RuleSection)`,
`(:Card)-[:HAS_ERRATA]->(:Erratum)`, `(:Card)-[:BANNED_IN]->(:BanEntry)` — die
koppelen kaarten aan de regel-/errata-/ban-data uit de change-tracker.

## Q&A gebruikt de kaartdata nu al
`src/lib/rag.ts` herkent kaartnamen in de vraag en voegt de **gezaghebbende
kaartgegevens** (type, domains, energy/might, tekst) toe aan de context naast de
vector-hits — zo combineert het antwoord regeltekst én kaartfeiten.
