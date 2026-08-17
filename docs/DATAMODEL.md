# Datamodel (deep-dive #4)

> Status: ontwerp op papier. Twee samenwerkende stores:
> **Postgres + pgvector** (relationeel + embeddings) en **Neo4j** (graph).

## Verdeling
- **Postgres**: bronnen-register, documenten/versies, regel-chunks (+ vector),
  changelog/diffs, correcties/override-laag, gebruikers, feedback.
- **Neo4j**: de kennis-graph (kaarten ↔ keywords ↔ regelsecties ↔ errata ↔ bans).
- **Koppeling**: gedeelde stabiele id's (bv. `rule_section.code`, `card.id`) zodat
  een vector-hit in Postgres naar de juiste Neo4j-knoop verwijst en andersom.

## Postgres (relationeel + pgvector)

```sql
-- Bronnen-register
source(
  id, name, url, type,            -- 'official' | 'community'
  trust_tier,                     -- 1..4
  rank,                           -- fijnafstemming binnen tier
  parser,                         -- 'pdf' | 'html' | 'json_api'
  cadence,                        -- scanfrequentie
  enabled bool,
  last_hash, last_checked, last_modified
)

-- Documentversies (per bron, per ingest)
document(
  id, source_id -> source.id,
  doc_type,                       -- 'core_rules' | 'tournament_rules' | 'errata' | 'banlist' | ...
  version_label, retrieved_at, content_hash, raw_url
)

-- Regel-chunks met provenance + embedding
rule_chunk(
  id, document_id -> document.id,
  section_code,                   -- bv. '601.2.d'
  text,
  embedding vector,               -- pgvector (Voyage)
  source_id, retrieved_at, page_or_loc
)

-- Changelog / diffs (voedt de change-tracker feed)
change(
  id, source_id, document_id,
  section_code,
  change_type,                    -- 'ban' | 'errata' | 'core_rule' | 'tournament_rule' | 'set_release' | 'editorial'
  severity,                       -- 'high' | 'medium' | 'low'
  diff,                           -- toegevoegd/verwijderd/gewijzigd
  ai_summary, ai_meaning,         -- rijke uitleg
  detected_at
)

-- Tegenstrijdigheden (conflicten-inbox)
conflict(
  id, topic,                      -- kaart/sectie
  source_a_id, source_b_id,
  kind,                           -- 'stale' (community loopt achter) | 'contradiction'
  winner_source_id,               -- volgens trust-rank
  status,                         -- 'open' | 'reviewed' | 'resolved'
  detected_at
)

-- Correctie-/override-laag (recursive self-improvement)
correction(
  id, scope,                      -- 'card' | 'rule_section' | 'answer'
  ref,                            -- kaart-id / section_code / vraag-id
  text,
  provenance,                     -- wie + bron
  status,                         -- 'unverified' | 'verified'
  created_by -> app_user.id, created_at, verified_by, verified_at
)

-- Gebruikers (voor trust-niveaus: judge > speler)
app_user(
  id, display_name, role,         -- 'player' | 'judge' | 'admin'
  created_at
)

-- Feedback op antwoorden (duim omhoog/omlaag → voedt corrections)
feedback(
  id, question, answer, rating,   -- 'up' | 'down'
  correction_id -> correction.id,
  created_by -> app_user.id, created_at
)
```

Indexen: `rule_chunk.embedding` (pgvector ivfflat/hnsw), `change(detected_at)`,
`source(trust_tier, rank)`, `correction(status)`.

## Neo4j (graph)

**Knopen (labels):**
- `(:Card {id, name, type, domains, energy, might, abilities})`
- `(:Keyword {name})`
- `(:RuleSection {code, doc_type})`  ← gekoppeld aan `rule_chunk.section_code`
- `(:Erratum {id, text})`
- `(:BanEntry {id, format})`
- `(:Ruling {id, status})`          ← spiegelt `correction` (verified)
- `(:Set {name})`

**Randen (relaties), elk met `source`, `date`, `trust`:**
```
(:Card)-[:HAS_KEYWORD]->(:Keyword)
(:Keyword)-[:DEFINED_BY]->(:RuleSection)
(:Card)-[:HAS_ERRATA]->(:Erratum)-[:SUPERSEDES]->(:Card)      // printed_text
(:Card)-[:BANNED_IN]->(:BanEntry)-[:SOURCE]->(:RuleSection)
(:Card)-[:FROM_SET]->(:Set)-[:PATCH_NOTES]->(:RuleSection)
(:Ruling)-[:APPLIES_TO]->(:Card|:Keyword)
```

**Voorbeeld-traversal (retrieval):** vanaf herkende `Card`-knopen →
`HAS_ERRATA`, `BANNED_IN`, `HAS_KEYWORD`→`DEFINED_BY`, en gekoppelde `Ruling`s →
lever de feiten + bron mee aan Claude, samen met de pgvector-hits.

> **Let op — dit is het ontwerp van de Next.js-PoP** (`src/lib/neo4j.ts`), niet de
> live v2-graaf. Daar heet de kaart→mechaniek-relatie **`HAS_MECHANIC`** en wijst ze
> naar een `(:Mechanic)`-knoop; `OntologySchema` is daar sinds #274 de ÉNE bron voor
> die naam (zie ARCHITECTURE.md, `RbRules.Domain/Ontology`). Neem `HAS_KEYWORD`
> hierboven dus niet over in nieuwe v2-code — één relatie, één naam.

## Provenance & conflictlogica (overal)
- Elke chunk én elke edge draagt **bron + datum + trust**.
- "Officieel verslaat community" en "errata supersedes print" zijn regels over
  trust/edge-type — niet hard-coded in de UI.
- Corrections wijzigen **nooit** officiële tekst; ze leven als aparte laag/`Ruling`
  en worden bij conflict met een nieuwe officiële update geflagd voor herziening.
