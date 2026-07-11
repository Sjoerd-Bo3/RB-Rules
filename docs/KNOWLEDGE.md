# Kennisbank-ontwerp: van regels-opzoeker naar spelbegrip

Probleem (geobserveerd in live antwoorden): de vraag-pipeline voedt het LLM met
losse §-secties en kaartteksten, maar zonder samenhangend begrip van hoe het
spel *stroomt*. Antwoorden zijn daardoor formeel correct maar missen context —
en zodra de regels iets niet letterlijk dekken, valt er niets terug te vallen.

Doel: een gelaagde kennisbank die naast de normatieve regels ook
**interpretatie, conventies, spelverloop en meta-kennis** bevat — met per bron
een betrouwbaarheidsscore en per claim een corroboratie-telling (hoeveel
onafhankelijke bronnen zeggen hetzelfde).

## De kennispiramide

| Laag | Inhoud | Status in prompt | Bron |
|---|---|---|---|
| 0. Officiële regels | Core/Tournament §'s, errata, bans | **Normatief** — wint altijd | PDF's (bestaat) |
| 1. Spelbegrip (primer) | Beurtstructuur, resources, combat-flow, timing/prioriteit, zones, winconditie, keyword-gedrag in de praktijk | Achtergrondkennis — altijd aanwezig | Gegenereerd uit laag 0, door beheerder gereviewd |
| 2. Community-interpretatie | "Zo wordt § X in de praktijk gelezen", veelgemaakte misvattingen, judge-antwoorden op fora | Interpretatief — met corroboratie-label | Geharvest van internet |
| 3. Meta & tactiek | Archetypes, veelgebruikte combo's, tactieken, "waarom speelt iedereen X" | Context bij tactiekvragen | Decklijsten, guides, toernooiverslagen |

Hard principe: lagen worden in de prompt **expliciet gelabeld** en het
antwoordformat scheidt "Regelbasis" (laag 0) van "Community-consensus"
(laag 2/3). Interpretatie mag een oordeel kleuren, nooit dragen.

## Laag 1 — Game-primer (quick win, lost het kernprobleem op)

- Een set concept-documenten (~10-15 stuks, elk 200-400 woorden): de beurt,
  runes/energy, showdowns & combat, prioriteit en reacties, zones en
  kaartstromen, winnen/scoren, elk kern-keyword praktisch uitgelegd.
- **Generatie**: LLM destilleert ze uit de volledige regelindex (per concept:
  relevante §'s → samenvatting mét §-verwijzingen). Opslag als
  `knowledge_doc` met kind="primer", embeddings, en een review-status
  (dezelfde verify-flow als corrections — de beheerder keurt ze).
- **Gebruik**: een gecomprimeerde primer (~1.500 tokens) gaat áltijd mee als
  achtergrondblok in /ask; daarnaast doen primer-docs mee in retrieval.
- **Onderhoud**: bij een regelwijziging (change met severity high/medium)
  markeert de scheduler de gerelateerde primer-docs als "te herzien".

## Laag 2 — Claims-pipeline (community-kennis met bewijsvoering)

Datamodel:

```
claim: id, topic_type (card|mechanic|section|concept), topic_ref,
       statement (geparafraseerd, NL), first_seen, last_seen,
       corroboration (aantal onafhankelijke bronnen),
       trust_score (gewogen: bron-trust × corroboratie),
       status (unreviewed|accepted|rejected|superseded), embedding
claim_source: claim_id, source_id, url, quote_excerpt (kort), seen_at
```

Pipeline (nachtelijke job, hergebruikt de bestaande source-registry met
trust/rank en de scan-infrastructuur):

1. **Harvest**: per community-bron (fansites, Reddit-JSON, YouTube-transcripts
   van rules-uitleg, artikelen) nieuwe documenten binnenhalen — beleefd
   (robots.txt, rate-limit) en met de bestaande browser-UA/Cloudflare-lessen.
2. **Extractie**: LLM haalt claims uit elk document — alleen uitspraken over
   regels/interacties/conventies, geparafraseerd, met topic-koppeling
   (kaartnaam/mechaniek/§).
3. **Clustering**: nieuwe claim embedden → dichtstbijzijnde bestaande claims →
   LLM-oordeel "zelfde bewering?" → corroboratie +1 (nieuwe bron erbij) of
   nieuwe claim.
4. **Conflictdetectie**: claims over hetzelfde topic die elkaar tegenspreken →
   bestaand Conflict-model + reviewqueue. Claims die een officiële § of
   erratum tegenspreken → automatisch rejected met verwijzing.
5. **Review**: admin-queue toont nieuwe claims gesorteerd op impact
   (corroboratie × gebruik); accepteren maakt ze retrieval-baar.

Retrieval: geaccepteerde claims doen mee als eigen kanaal in /ask, met in de
prompt per claim: `[community, 4 bronnen, trust 0.8] "…"`. De router bepaalt
het gewicht (ruling: laag; conventie-/tactiekvraag: hoog).

## Laag 3 — Meta & tactiek

Bouwt op decks-backlog (#15): archetype-detectie uit decklijsten,
combo-frequentie (co-occurrence die we al minen als INTERACTS_WITH),
guide-extractie via dezelfde claims-pipeline met topic_type="tactic".
Graph-edges: Card—STAPLE_IN→Archetype. Pas oppakken als laag 1+2 staan.

## Wat dit oplevert voor het geobserveerde probleem

1. Primer altijd in context → antwoorden snappen de flow ("na X komt een
   showdown, dus …") ook als geen § het letterlijk zegt.
2. Community-claims vullen het gat tussen regels en praktijk — mét
   bronvermelding en corroboratie, dus controleerbaar en eerlijk gelabeld.
3. De trace (#40) toont voortaan ook welke lagen meededen — direct te zien
   waarom een antwoord wél of geen community-kennis gebruikte.

## Risico's & randvoorwaarden

- **Auteursrecht**: claims zijn parafrases + korte quote + bronlink; we
  publiceren geen overgenomen teksten. Opslag is intern.
- **Veroudering**: claims hebben last_seen; een erratum/rules-change
  invalideert claims op dat topic (status superseded, terug de queue in).
- **Kosten**: harvest/extractie nachtelijk, cheap-model, batched; cap per
  nacht. Corroboratie-clustering is embeddings-werk (lokaal, gratis).
- **Bron-hygiëne**: bronnen leven in het bestaande register met trust/rank;
  een bron die structureel door errata wordt tegengesproken zakt in trust.

## Uitvoeringsvolgorde

1. **Primer** (issue) — grootste begripwinst per uur werk.
2. **Claims-model + pipeline v1** met 2-3 bronnen + admin-review (issue).
3. **Retrieval-lagen + prompt/UI-integratie** ("Community-consensus"-blok,
   trace-uitbreiding) (issue).
4. Meta-laag na decks (#15).
