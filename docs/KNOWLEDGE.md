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

## Evolutie-raamwerk: de bank groeit met het spel mee

Dit is geen eenmalige bouw — elke set brengt nieuwe kaarten, mechanieken en
community-kennis. Evolutie is een eersteklas onderdeel van het ontwerp:

1. **Set-release als event.** De change-classifier herkent set-releases al;
   dat event triggert voortaan de volledige keten: card-sync → nieuwe-
   mechanieken-detectie → gerichte claims-harvest op de nieuwe set →
   embeddings → graph-sync → primer-herziening waar nieuwe keywords de flow
   raken. Grotendeels bestaande scheduler-stappen, plus gerichte triggers.
2. **Het mechaniek-vocabulaire groeit.** De miner werkt nu met een vaste
   seed-lijst. Evolutie: de miner rapporteert óók keyword-kandidaten
   (bracketed termen in kaartteksten die niet in het vocabulaire staan) →
   admin-reviewqueue → geaccepteerde keywords worden vocabulaire → re-mine
   van kaarten met dat keyword. Zo leert het systeem "Overwhelm" kennen op
   de dag dat de eerste kaart ermee verschijnt.
3. **Kennis heeft een levenscyclus.** Elke eenheid (chunk, claim, primer-doc,
   uitleg-cache) draagt provenance en geldigheid. Errata/regelwijzigingen
   invalideren afhankelijke kennis automatisch (deels gebouwd: embeddings en
   uitleg-cache invalideren al bij tekstwijziging); primer-docs en claims
   krijgen dezelfde behandeling.
4. **Kennis-gaten worden gemeten, niet geraden.** Ask-traces + feedback
   vormen het kompas: vragen met "Onzeker"-label, negatieve feedback of lege
   retrieval worden geclusterd tot een "kennis-gaten"-rapport in het beheer —
   dat stuurt waar de volgende harvest of primer-uitbreiding heen gaat. De
   zelflerende correcties-loop (bestaat) is hier het handmatige zusje van.

## Einddoel: één brein — alles vector- én graf-gelinkt

Alle kennissoorten worden knopen in één samenhangend model, met twee
complementaire representaties over dezelfde identiteiten:

- **pgvector** voor semantische nabijheid (wat lijkt op elkaar)
- **Neo4j** voor getypeerde relaties (wat hoort bij elkaar en waarom)

Schema (groeit met de lagen mee):

```
(Card)-[:HAS_MECHANIC]->(Mechanic)     (Claim)-[:ABOUT]->(Card|Mechanic|Section)
(Card)-[:INTERACTS_WITH]->(Card)       (Claim)-[:SUPPORTED_BY]->(Source)
(Concept)-[:EXPLAINS]->(Section)       (Erratum)-[:SUPERSEDES]->(Card-tekst)
(Card)-[:CITED_IN]->(Section)          (Card)-[:STAPLE_IN]->(Archetype)
(Change)-[:AFFECTS]->(Section|Card)
```

Daarboven een **brein-API** — de toolset waarmee AI (onze eigen ask-agent
voorop) het model kan bevragen in plaats van alleen een statische prompt te
krijgen:

- `semantic_search(query, layer?)` — zoeken over alle lagen
- `neighbors(node, edge_types?)` — wat hangt hieraan vast
- `path(a, b)` — hoe zijn twee dingen verbonden (bewijsketen)
- `evidence(claim)` — bronnen + corroboratie van een bewering
- `contradictions(topic)` — waar spreken bronnen elkaar tegen

De ask-agent evolueert daarmee van "één prompt met context" naar een agent
die zelf het brein doorloopt (rb-ai draait al op de Agent SDK — tools zijn
een geconfigureerde MCP-server, maxTurns omhoog). Dezelfde brein-API is
daarna de bouwsteen voor alles wat we nog verzinnen: interactie-ontdekking,
deck-advies, "wat verandert er voor mijn deck door deze errata", enz.

## Uitvoeringsvolgorde

1. **Primer** (#49) — grootste begripwinst per uur werk.
2. **Claims-model + pipeline v1** met 2-3 bronnen + admin-review (#50).
3. **Retrieval-lagen + prompt/UI-integratie** ("Community-consensus"-blok,
   trace-uitbreiding) (#51).
4. **Evolutie-raamwerk**: set-release-keten, vocabulaire-groei,
   kennis-gaten-rapport (#52).
5. **Brein-API + agentic ask**: unified graph-schema + tools voor de
   ask-agent (#53).
6. Meta-laag na decks (#15).
