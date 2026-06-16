# Kosten & Hosting (deep-dive #2)

> Status: research/raming. Prijzen per 2026-06; verifieer bij de bron vóór commitment.
> Bedragen in USD, afgerond, indicatief.

## Samenvatting

De MVP (change-tracker) kan **vrijwel gratis** draaien op free tiers. Zodra je
GraphRAG + Q&A serieus gebruikt, zit je grofweg op **~$65–150/maand**, vooral
bepaald door de Neo4j-keuze en het Q&A-gebruik. LLM-kosten voor de change-tracker
zelf zijn verwaarloosbaar dankzij de hash-check (95% van de scans = gratis).

## Bouwstenen & prijzen

### LLM — Claude (per 1M tokens)
| Model | Input | Output | Waarvoor |
|---|---|---|---|
| Opus 4.8 (`claude-opus-4-8`) | $5 | $25 | lastige rulings, foto-redenering |
| Sonnet 4.6 (`claude-sonnet-4-6`) | $3 | $15 | change-uitleg, goedkope Q&A |
| Haiku 4.5 (`claude-haiku-4-5`) | $1 | $5 | classificatie, simpele taken |

- **Prompt caching:** cache-write 1.25× (5 min TTL) of 2× (1 uur); cache-read ≈0.1×.
  Voor RAG met een vaste regel-context levert dit ~90% besparing op herhaald gebruik.
- **Batches API:** 50% korting voor niet-tijdkritische verwerking (bv. her-indexeren).

### Embeddings — Voyage AI (per 1M tokens)
| Model | Prijs |
|---|---|
| voyage-3 | $0.06 |
| voyage-3-lite | $0.02 |
- **Eerste 200M tokens gratis** voor nieuwe accounts → onze hele initiële ingest
  (Core/Tournament Rules + errata + card-DB ≈ enkele miljoenen tokens) valt
  ruimschoots binnen het gratis budget.

### Databases & hosting (per maand)
| Dienst | Free tier | Betaald |
|---|---|---|
| **Neo4j AuraDB** (graph) | Free tier (beperkt, geen creditcard) | Professional vanaf **$65/GB**, min. 1GB |
| **Supabase** (Postgres + pgvector) | Free tier | Pro **$25** (8GB db, 100K MAU); pgvector gratis inbegrepen |
| **Vercel** (Next.js PWA hosting) | Hobby gratis | Pro **$20** |

## Raming per fase

### Fase 1 — MVP (change-tracker), alles op free tiers
- Neo4j Free + Supabase Free + Vercel Hobby = **$0**
- Embeddings: binnen 200M gratis = **$0**
- LLM change-uitleg: enkele wijzigingen/week × Sonnet ≈ **< $1/maand**
- **Totaal: ~$0–5/maand** (prima om mee te starten/testen)

### Fase 2+ — productie met GraphRAG + Q&A
- Neo4j AuraDB Professional (1GB): **~$65**
- Supabase Pro: **$25**
- Vercel Pro: **$20** (of Hobby gratis bij licht gebruik)
- Embeddings: meest binnen gratis; daarna ~$0.06/1M = **~$0–5**
- LLM Q&A: zie rekenvoorbeeld hieronder
- **Totaal infra: ~$90–110/maand** + LLM-gebruik

### Q&A-rekenvoorbeeld (per vraag)
- Context: ~5–10k tokens regeltekst (grotendeels **gecached** → ~0.1×) + vraag
- Antwoord: ~500–1.000 output-tokens
- Met Sonnet 4.6 + caching: grofweg **$0.005–0.02 per vraag**
- 1.000 vragen/maand ≈ **$5–20/maand**. Opus voor de lastige ~10% verhoogt dit licht.

## Kostenbeheersing (ingebouwd in het ontwerp)
- **Hash-check vóór LLM:** 95% van de scans stopt gratis.
- **Modelrouting:** Haiku/Sonnet standaard, Opus alleen voor lastige rulings/foto.
- **Prompt caching** op de vaste regel-context.
- **Batches (-50%)** voor her-indexeren bij set-releases.
- **Voyage gratis tier** dekt vrijwel alle embedding-kosten.

## Eerlijke kanttekening bij de Neo4j-keuze
Neo4j is de grootste vaste kostenpost (~$65/mnd zodra je van de Free tier af moet).
Het alternatief (graph-lite in Postgres/Supabase) zou deze post elimineren — één
database i.p.v. twee. We hebben bewust voor Neo4j gekozen om de kracht/visualisatie;
houd er rekening mee dat dit de grootste recurring kost is. Start gerust op de Neo4j
Free tier en upgrade pas als de data/queries het vereisen.

## Bronnen
- Claude-prijzen: via de claude-api skill (model-catalogus, 2026-06).
- Voyage: https://docs.voyageai.com/docs/pricing
- Neo4j AuraDB: https://neo4j.com/pricing/
- Supabase: https://supabase.com/pricing
- Vercel: https://vercel.com/pricing
