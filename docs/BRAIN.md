# Het brein (#53) — ontwerp: unified vector+graph-model, brein-API en agentic ask

Dit document vertaalt het einddoel uit `docs/KNOWLEDGE.md` ("één brein — alles
vector- én graf-gelinkt") naar een implementeerbare architectuur, nu kennislaag
1 t/m 4 gebouwd zijn (#49 primer, #50 claims-pipeline, #51 retrieval-lagen,
#52 evolutie-raamwerk). Het sluit af met een gefaseerde opdeling in vijf
deelissues, elk zelfstandig bouwbaar en mergebaar in één dag-golf.

Leidend principe (uit KNOWLEDGE.md en CONVENTIONS.md): Postgres blijft de bron
van waarheid; Neo4j en alle brein-afgeleiden zijn projecties die altijd
opnieuw opbouwbaar zijn. De kennispiramide (officieel > geverifieerde rulings
> primer > community > meta) blijft in élk koppelvlak expliciet gelabeld —
ook wanneer een agent zelf het brein doorloopt.

## 1. Wat er al ligt — inventaris en koppelvlakken

### 1.1 De kennislagen

| Laag | Inhoud | Opslag | Code (rb-api) |
|---|---|---|---|
| 0. Officieel | Regel-§'s (chunks + FTS + embeddings), bans, errata, changes | `rule_chunk`, `ban_entry`, `erratum`, `change`, `document`, `source` | `IngestService`, `RuleChunkPipeline`, `BanErrataSyncService`, `ChangeClassificationService` |
| 0b. Geverifieerde rulings | Feedback → reviewqueue → geverifieerd, met embedding | `correction` (status=verified) | `AskEndpoints` (feedback), admin-verify |
| 1. Primer | 12 concept-docs, draft → approved, met embedding en §-refs | `knowledge_doc` (kind=primer) | `PrimerService`, `PrimerTopics` |
| 2. Community-claims | Geparafraseerde beweringen met corroboratie, trust-score, officiële toets en bronnen+citaat | `claim`, `claim_source` | `ClaimMiningService`, `ClaimMiner`/`ClaimJudge`/`OfficialCheck` (Domain), `ClaimRetrieval` (Domain) |
| Evolutie (#52) | Set-release-keten, groeiend keyword-vocabulaire, kennis-gaten-rapport | `mechanic_keyword`, run_log-grootboek | `SetReleaseService`, `MechanicVocabularyService`, `KnowledgeGapsService` |
| 3. Meta & tactiek | Deck-gebruikssignaal (aandeel recente decks, gemiddeld aantal exemplaren, top-co-occurrence) als gelabeld laag-3-blok in /ask (#267); archetype-detectie blijft niet-doel | `deck`, `deck_card` | `DeckMetaRetrieval` (Domain, poort + blok), `DeckPopularityQuery`, deck-meta-kanaal in `AskService` |

### 1.2 Retrieval: /ask vandaag

`AskService.AskAsync` is één retrieval-pass + één LLM-call:

1. Interne router (`QuestionRouter`, heuristisch) → vraagtype + structuur +
   bronnen-bias.
2. Query-rewrite (#66, één cheap-call) → genormaliseerde zoekzin, zoekqueries,
   lexicale termen.
3. Kanalen: vector (pgvector, per query), full-text (Postgres FTS), RRF-fusie
   (`RrfFusion`, Domain), kaartcontext (naam/mechaniek/lexicaal/semantisch),
   banlijst, geverifieerde rulings, primer (top-3 approved), community-claims
   (`ClaimRetrieval.TakeFor`-gewicht per vraagtype, afstands-plafond 0.55),
   deck-meta (laag 3, #267 — alléén bij kaart-/lijstvragen mét herkende
   kaartnaam, `DeckMetaRetrieval.ShouldRetrieve`).
4. Eén antwoord-call via `RbAiClient` → rb-ai (task cheap, of hard bij foto),
   met alle blokken gelabeld in piramide-volgorde.
5. `AskTrace` (#40) legt per vraag vast welke lagen meededen;
   `AskMetric` meet de duur.

Dit werkt, maar het model kan niets terugvragen: mist er een schakel (welke
kaarten delen dit keyword? wat zegt de erratum-keten? welke bronnen dragen
deze claim?), dan valt er niets bij te laden.

### 1.3 Graph: wat er echt in Neo4j staat

`GraphSyncService` schrijft batched en transactioneel: `(:Card)`, `(:Set)`,
`(:Domain)`, `(:Tag)`, `(:Mechanic)` met `FROM_SET`/`HAS_DOMAIN`/`HAS_TAG`/
`HAS_MECHANIC`; alleen canonieke printings (#57). `InteractionService.MineAsync`
schrijft daarnaast `INTERACTS_WITH`-edges (best-effort, Postgres leidend).

Eerlijke observaties:

- **Neo4j is vandaag write-only.** De graph-verkenner (`/graph`,
  `GraphQueryService`) en de interactie-buren lezen uit Postgres, niet uit
  Neo4j. Er is geen enkele lees-consument van de graph. Het brein geeft
  Neo4j zijn eerste echte afnemer — of legt bloot dat we hem niet nodig
  hadden; dit ontwerp kiest bewust voor het eerste, omdat pad- en
  buurvragen ("hoe hangt X aan Y?") in SQL snel onhandig worden.
- **De `RuleSection`-constraint in `GraphSchema` is dood**: er wordt nergens
  een `(:RuleSection)`-knoop geschreven.
- Van het doelschema uit KNOWLEDGE.md bestaat alleen `HAS_MECHANIC` en
  `INTERACTS_WITH`. `EXPLAINS`, `ABOUT`, `SUPPORTED_BY`, `SUPERSEDES`,
  `AFFECTS`, `CITED_IN` en `STAPLE_IN` ontbreken; claims, concepten,
  secties, bronnen, errata en changes zijn geen knopen.

### 1.4 rb-ai: wat de sidecar kan

`rb-ai` draait de Claude Agent SDK met drie task-types (`ai.ts`):
`cheap` (Sonnet, 1 beurt, geen tools), `hard` (Opus, 1 beurt, geen tools),
`research` (Sonnet, 16 beurten, alléén WebSearch/WebFetch, harde 5-min-
timeout, `Bronnen:`-contract). Er is nog geen MCP-server en geen manier om
de agent rb-api-tools te geven. `RbAiClient` (rb-api) degradeert naar `null`
bij uitval; de HttpClient-timeout staat op 6 minuten.

### 1.5 Bekende gaten (eerlijk)

- **#93 — claims-extractie faalt stil op productie** (60/60 mislukt zonder
  diagnose): de claims-reviewqueue is live nog leeg, dus kennislaag 2 heeft
  in productie nog géén data. Het brein-ontwerp bouwt op het claims-model,
  niet op gevulde data — maar verificatie moet met seeds/fixtures rekenen.
- **#92 — documenten worden als gemined gemarkeerd vóór de extractie
  slaagt**: per-claim-mislukkingen (`ClaimOutcome.Failed`) zetten
  `extractionComplete` niet op false, dus een document waarvan álle claims
  faalden wordt toch afgevinkt.
- **De set-release-keten slaat de claims-harvest nog altijd over**:
  `SetReleaseService.RunChainAsync` bevat hardcoded "claims-harvest:
  overgeslagen — claims-pipeline (#50) bestaat nog niet", terwijl #50
  inmiddels gebouwd is. Dit is een los eindje van de golf van vandaag.
- Geen archetype-detectie; de meta-laag zelf (deck-gebruikssignaal als
  laag-3-blok in /ask) bestaat sinds #267, maar kent geen graph-representatie
  (`STAPLE_IN` blijft niet-doel, §3).
- `AskTrace` kent de brein-dimensie nog niet (geen veld voor agentic
  stappen/tool-calls).

## 2. Doelbeeld → architectuur

### 2.1 Identiteiten: één NodeRef-conventie over beide representaties

Alles wat het brein kent krijgt één canonieke, tekstuele referentie die in
pgvector-land, Neo4j-land én API-contracten identiek is:

```
card:<riftboundId>          (canonieke printing, #57)
mechanic:<naam>             concept:<primer-topic-key>
section:<sourceId>/<code>   claim:<id>
source:<sourceId>           erratum:<id>
change:<id>                 set:<setId>
domain:<naam>               tag:<naam>
ruling:<correctionId>       (geverifieerde ruling)
```

Parsing/formatting is pure Domain-logica (`BrainRef`), unit-getest. In Neo4j
wordt de ref als `ref`-property op elke knoop gezet (naast de bestaande
`id`/`name`/`code`-properties), zodat elke API-respons en elke graph-query
dezelfde sleutel spreekt. Er komt géén super-node-tabel in Postgres: de
bestaande tabellen zíjn de knopen, de ref is een afspraak, geen opslag.

### 2.2 Het unified model: pgvector + Neo4j-projectie

**pgvector-kant (bestaat grotendeels).** Embeddings leven al per soort:
`rule_chunk`, `card`, `claim`, `knowledge_doc`, `correction` — allemaal
bge-m3/1024 met model-provenance. "Zoeken over alle lagen" wordt daarom géén
nieuwe mega-tabel maar een read-service die per laag een top-k vector-query
doet (één keer embedden, vijf goedkope HNSW-queries) en de lijsten gelabeld
teruggeeft. KISS: de lagen blijven eigen tabellen met eigen levenscyclus.

**Neo4j-kant (uit te bouwen).** `GraphSyncService` groeit van kaart-facetten
naar het volledige schema. Nieuwe knopen en relaties, allemaal afgeleid uit
Postgres en in dezelfde transactionele rebuild:

```
(:RuleSection {ref, code, sourceId, title?})
    (child)-[:PART_OF]->(parent)          ← RuleParentLookup-hiërarchie
(:Concept {ref, topic, title, status})
    (concept)-[:EXPLAINS]->(:RuleSection) ← KnowledgeDoc.SectionRefs
(:Claim {ref, statement, corroboration, trustScore, status, officialStatus})
    (claim)-[:ABOUT]->(Card|Mechanic|RuleSection|Concept)  ← topic_type/topic_ref
    (claim)-[:SUPPORTED_BY]->(:Source {ref, name, trustTier})
(:Erratum {ref, cardName})
    (erratum)-[:SUPERSEDES]->(:Card)      ← Erratum.CardRiftboundId
(:Change {ref, changeType, severity, detectedAt})
    (change)-[:AFFECTS]->(RuleSection|Card) ← classificatie + naam/§-match
```

Scope-keuzes, bewust:

- **Claims: alleen `status IN (accepted, unreviewed)`** gaan de graph in,
  mét status-property; rejected/superseded blijven alleen in Postgres
  (zichtbaar via het contradictions-koppelvlak, §2.3). Zo kan geen tool per
  ongeluk weerlegde kennis als buurknoop presenteren zonder label.
- **`CITED_IN` (Card→Section) schuift door**: dat vergt een aparte
  mining-pass (kaartnamen in sectieteksten) zonder afnemer vandaag. YAGNI —
  het schema kan hem er later bij zonder iets te breken.
- **`STAPLE_IN`/Archetype: niet-doel** (zie §3; de prompt-kant van laag 3
  bestaat sinds #267, een graph-representatie vergt archetype-detectie).
- Aantallen zijn klein (duizenden secties, honderden claims/changes,
  tientallen bronnen/errata): de bestaande batched-UNWIND-aanpak en de
  8GB-VM (Neo4j-memory-caps uit #45) kunnen dit ruim aan.

Sync-triggers blijven zoals nu: de `graph`-job in `JobCatalog`, de
scheduler-tick na mining, en de set-release-keten. De rebuild blijft één
transactie: half-geprojecteerde breinen bestaan niet.

### 2.3 De brein-API

Nieuwe endpoints onder `/api/brain/*` in een eigen `BrainEndpoints.cs`
(MapGroup-patroon), dun, met de logica in een nieuwe
`Infrastructure/BrainService` (Postgres-kant) en uitbreiding van een
Neo4j-lees-service (graph-kant). rb-api is alleen binnen het
compose-netwerk bereikbaar; de browser komt er uitsluitend via
rb-web-proxy's. Geen LLM-kosten in deze endpoints — alleen DB/Neo4j-reads,
dus geen `llm`-rate-limit nodig.

| Endpoint | Contract (verkort) | Bron |
|---|---|---|
| `GET /api/brain/search?q=&layers=&take=` | per laag een gelabelde lijst `{ref, layer, title, snippet, score, trustLabel}` | pgvector per laag (rules, cards, claims, primer, rulings) |
| `GET /api/brain/node/{ref}` | eigenschappen + laag + provenance (trust/status/§-refs) | Postgres (projectie, nooit embeddings) |
| `GET /api/brain/neighbors/{ref}?edges=&take=` | `{ref, name, edge, richting, props}` | Neo4j |
| `GET /api/brain/path?from=&to=&maxLen=4` | kortste pad als keten `[node, edge, node, …]` — de bewijsketen | Neo4j `shortestPath` |
| `GET /api/brain/evidence/{claimRef}` | statement, corroboratie, trustScore, officialStatus(+reason), bronnen met citaat+URL | Postgres (`claim`+`claim_source`+`source`) |
| `GET /api/brain/contradictions?topic=` | open `Conflict`-rijen + rejected/superseded claims op het topic, expliciet gelabeld | Postgres |

Afnemers, in volgorde van aansluiten:

1. **De ask-agent** (rb-ai, via MCP-tools — §2.4): de hoofdafnemer.
2. **rb-web**: de graph-verkenner v2 en admin-schermen (via
   `+page.server.ts`/proxy, conform de conventie "browser praat nooit
   rechtstreeks met rb-api").
3. **Toekomstige features**: interactie-ontdekking, deck-advies, "wat
   betekent deze errata voor mijn deck" — allemaal alléén nieuwe
   prompts/flows op dezelfde zes koppelvlakken.

Degradatie: Neo4j-uitval maakt `neighbors`/`path` een nette Problem-response
met detail; `search`/`node`/`evidence`/`contradictions` (Postgres) blijven
werken. De agent-tools vertalen dat naar "graph niet beschikbaar" als
toolresultaat — de agent kan dan alsnog semantisch verder (fouten zijn data).

### 2.4 Agentic ask

**Mechaniek.** rb-ai krijgt een vierde task-type `agentic`:

- In-process MCP-server (Agent SDK `createSdkMcpServer`) met zes tools die
  1-op-1 op de brein-API mappen: `semantic_search`, `get_node`,
  `neighbors`, `path`, `evidence`, `contradictions`. Elke tool doet een
  HTTP-call naar rb-api (`RB_API_URL`, nieuw env, compose-intern) en geeft
  compacte, gelabelde JSON terug — inclusief laag- en trust-labels, zodat
  de kennispiramide ook binnen de agent-loop zichtbaar blijft.
- Model Sonnet, `maxTurns` ~8, alléén de brein-tools in de allowlist
  (géén web, géén bash), `permissionMode: "dontAsk"`, harde timeout
  (90–120s via AbortController, zelfde patroon als research) én een
  tool-call-teller in de MCP-server (cap ~12 calls) — dubbele rem op
  kosten en latency.
- Systeem-prompt = de bestaande ruling-skill + een agent-addendum: welke
  tools er zijn, dat officieel > rulings > primer > community blijft
  gelden, en dat elk toolresultaat met laag-label gewogen moet worden.

**Wanneer mag een vraag door-redeneren?** Single-pass blijft het
standaardpad (latency, kosten, voorspelbaarheid). `AskService` voert eerst
de normale retrieval uit en beslist daarna:

1. **Feature-flag** `ASK_AGENTIC` (env: `off` | `auto` | `force`;
   default `off` bij oplevering, daarna `auto` na gemeten gedrag). `force`
   bestaat alleen voor verificatie.
2. In `auto` kwalificeert een vraag als: (a) vraagtype `Ruling` met ≥2
   herkende kaartnamen (interactievragen — precies waar één pass
   aantoonbaar context mist), óf (b) het lege-retrieval-signaal uit het
   gaps-rapport (#52): geen secties, geen kaartcontext, geen primer — de
   bank weet aantoonbaar niets en door-redeneren is de enige kans.
3. De agentic call vervángt de finale LLM-call en krijgt dezelfde
   contextblokken mee als startpunt (de agent hoeft niet te her-ontdekken
   wat de retrieval al vond); de tools dienen voor wat er ontbreekt.
4. **Vangnet**: levert de agent geen bruikbaar antwoord (timeout, fout,
   leeg), dan volgt alsnog de klassieke single-pass-call. De gebruiker
   merkt alleen extra wachttijd, nooit een slechter antwoord.

**Meetbaar.** `AskTrace` krijgt `Agentic` (bool) en `BrainSteps` (compacte
tool-call-log: toolnaam + ref per stap); `AskMetric` krijgt `Agentic` zodat
de duurstatistiek beide paden apart toont. De admin-trace-pagina toont de
brein-stappen — dezelfde controleerbaarheid als de bestaande denkstappen.

### 2.5 Dynamische relaties (#116): open vocabulaire, gereviewde projectie

De vaste edge-types uit §2.2 dekken de structuur, niet de interessantste
kennis ("counters", "enables", "wordt beperkt door"). Daarvoor bestaat één
generiek edge-TYPE `RELATES_TO {kind, trust, explanation, status}` — het
kind is een property-waarde uit een open maar gereviewd vocabulaire, geen
nieuw edge-type per soort.

**Architectuurregel: LLM-relaties gaan NOOIT rechtstreeks de graph in.**
Het INTERACTS_WITH/claims-patroon is veralgemeniseerd:

1. **Postgres is de bron**: `relation` (from_ref/to_ref als BrainRef, kind,
   explanation, provenance, trust, status unreviewed/accepted/rejected) en
   `relation_kind` (candidate/accepted/rejected — het
   mechaniek-vocabulaire-patroon uit #52, met een seed-lijst in
   `RelationMiner.SeedKinds`).
2. **Mining** (`RelationMiningService`, job "relations"): cheap-calls per
   anker (primer-concepten, zelf-invaliderend gemarkeerd via
   `relations_mined_at`; plus één gecapte mechanieken-overzichtspass), met
   de #93-discipline — gedeelde LlmJson-parser, rauwe respons in run_log
   bij uitval, markeren pas na succes, dedupe op (van, naar, kind) over
   álle statussen zodat verworpen voorstellen verworpen blijven. De LLM
   mag alleen refs gebruiken die de prompt zelf aanbood.
3. **Projectie** (`RelationProjection`, in de transactionele rebuild van
   `GraphSyncService`): accepted én unreviewed (status als edge-property),
   rejected nooit — en alléén met een geaccepteerd kind.
4. **Bevraagbaar**: neighbors/path krijgen een kind-filter als
   geparametriseerde property-waarde; de edge-TYPE-whitelist blijft vast.
   De rb-ai-tools tonen kind en uitleg in de toolresultaten.
5. **Beheer**: overview "relaties" (status-chips, van→naar klikbaar naar de
   brein-verkenner, accepteer/verwerp) met de kandidaat-kinds als queue op
   dezelfde pagina.

Trust blijft leidend: een relatie draagt de trust van zijn bewijsbron
(LLM-interpretatie van gecureerd/officieel materiaal weegt als tier-2 op de
ClaimScoring-schaal).

## 3. Niet-doelen

- **Geen archetypes** (`STAPLE_IN`): archetype-detectie is een
  onderzoeksproject, geen feature (#267). De meta-laag als prompt-blok
  (deck-gebruikssignaal, kennislaag 3) bestaat wél sinds #267 — zonder
  graph-representatie.
- **Geen publieke brein-API**: koppelvlakken zijn compose-intern; extern
  ontsluiten is een apart besluit (auth, quota — raakt #42).
- **Geen agentic-default**: single-pass blijft de norm; agentic is een
  gerichte escalatie achter een flag met vangnet.
- **Geen write-tools voor de agent**: het brein is read-only voor AI;
  kennis muteren blijft via pijplijnen en reviewqueues lopen.
- **Geen verplaatsing van waarheid naar Neo4j**, geen aparte vector-store,
  geen unificatie van embedding-tabellen: Postgres blijft bron, projecties
  blijven herbouwbaar.
- **Geen streaming/voorlezen** (#31) en **geen accounts/quota** (#42) in
  deze golf — de agentic-latency maakt #31 wel urgenter (benoemd als risico).
- **Geen CITED_IN-mining** in v1 (zie §2.2).

## 4. Risico's en randvoorwaarden

- **Kosten en latency (VM + abonnement).** Agentic = 2–8 LLM-beurten i.p.v.
  1; een ruling kan van ~10s naar 30–90s gaan. Mitigatie: de gate (§2.4),
  maxTurns/tool-cap/harde timeout, Sonnet i.p.v. Opus, en meten vóór
  verbreden (`AskMetric.Agentic`). De brein-API zelf is LLM-vrij en dus
  goedkoop; Neo4j-queries krijgen expliciete LIMIT's.
- **Kennislagen-integriteit in de agent-loop.** Buiten de ene prompt om
  kennis ophalen mag de piramide niet slopen. Mitigatie: elk toolresultaat
  draagt laag- en trust-labels; tools geven alleen accepted claims als
  kennis terug (weerlegde alleen via `contradictions`, gelabeld); het
  agent-addendum herhaalt de voorrangregels; de trace maakt elk gebruik
  controleerbaar.
- **Lege laag 2 zolang #92/#93 open staan.** Het brein werkt zonder claims,
  maar de claim-knopen blijven leeg tot de extractie-bug is opgelost.
  Verificatie van claim-gerelateerde onderdelen gebruikt daarom seeds/
  fixtures; #92/#93 blijven eigen issues (geen duplicatie in deze golf).
- **Drift tussen de twee representaties.** Postgres muteert continu; Neo4j
  per sync. Mitigatie: volledige transactionele rebuild (bestaand patroon),
  sync-triggers na elke mutatie-keten, en een drift-teller in het
  kennis-gaten-rapport (aantallen per knooptype Postgres vs Neo4j).
- **Neo4j-uitval.** Bestaand verwacht pad: brein-API degradeert per
  koppelvlak (§2.3), agentic ask redeneert semantisch verder of valt terug
  op single-pass, de site blijft volledig functioneel.
- **Ref-stabiliteit.** `card:`-refs volgen de canonieke printing (#57): een
  canonical-flip wijzigt refs. De graph-rebuild ruimt wezen al op; de
  brein-API resolvet variant-ids naar canoniek (bestaande `CardResolver`).

## 5. Gefaseerde opdeling — concept-issueteksten

Volgorde: 1 → 2 → (3 → 4) met 5 parallel na 2. Elk deelissue is één
branch/PR, mergebaar na groene CI + eigen verificatie, conform de
werkafspraken (nooit deployen tijdens een live admin-job).

---

### Deelissue 1 — Brein-graph: alle kennislagen als knopen en relaties in Neo4j

**Doel.** De graph groeit van kaart-facetten naar het unified schema uit
`docs/BRAIN.md` §2.2: `RuleSection` (+`PART_OF`-hiërarchie), `Concept`
(+`EXPLAINS`), `Claim` (+`ABOUT`, `SUPPORTED_BY`), `Source`, `Erratum`
(+`SUPERSEDES`), `Change` (+`AFFECTS`) — naast de bestaande Card/Set/
Domain/Tag/Mechanic en `INTERACTS_WITH`. Elke knoop krijgt een
`ref`-property volgens de `BrainRef`-conventie. De dode
`RuleSection`-constraint krijgt eindelijk knopen; nieuwe constraints voor
de nieuwe knooptypes (idempotent, `IF NOT EXISTS`).

**Raakvlakken.** `RbRules.Domain`: nieuw `BrainRef` (parse/format, puur) en
een pure topic→ref-mapper voor claims (`topic_type`/`topic_ref` →
Card/Mechanic/Section/Concept, incl. kaartnaam→canoniek-id).
`RbRules.Infrastructure/GraphSchema.cs` (constraints),
`GraphSyncService.cs` (nieuwe batched UNWIND-stappen binnen dezelfde
transactie; `GraphSyncResult` uitbreiden), `JobCatalog.cs` (detailregel
graph-job). Geen EF-migratie nodig.

**Afhankelijkheden.** Geen — bouwt op wat er ligt. Claims-knopen blijven
leeg tot #92/#93 zijn opgelost; dat is verwacht en geen blocker.

**Verificatie.** Unit-tests op `BrainRef` en de topic-mapper (incl.
niet-matchende topic_refs → knoop zonder ABOUT-edge, nooit een crash).
`dotnet test` groen. Handmatig: lokale stack, seed een paar claims,
graph-job draaien, met Cypher tellen dat elk knooptype en elke relatie
bestaat en dat de rebuild idempotent is (twee runs → zelfde tellingen).
run_log toont de nieuwe aantallen.

---

### Deelissue 2 — Brein-API: search, node, neighbors, path, evidence en contradictions

**Doel.** De zes koppelvlakken uit `docs/BRAIN.md` §2.3 onder `/api/brain/*`,
voor de ask-agent (deelissue 3), rb-web en alle toekomstige features.
Gelabelde resultaten (laag + trust), projecties zonder embeddings, nette
degradatie bij Neo4j-uitval.

**Raakvlakken.** Nieuw `RbRules.Api/Endpoints/BrainEndpoints.cs` (dun,
MapGroup) + registratie in `Program.cs`. Nieuw
`RbRules.Infrastructure/BrainService.cs` (search over de vijf
embedding-tabellen met éénmalige embed-call; node/evidence/contradictions
als Postgres-projecties) en `BrainGraphService.cs` (neighbors/path via de
bestaande `IDriver`, parameterized Cypher met LIMIT). Response-records in
`ApiContracts.cs`. Hergebruik: `CardResolver` (variant→canoniek),
`RuleParentLookup`, `EmbeddingService`.

**Afhankelijkheden.** Deelissue 1 voor `neighbors`/`path` (gevulde graph);
`search`/`node`/`evidence`/`contradictions` werken al zonder.

**Verificatie.** Unit-tests op de pure delen (ref-validatie, laag-filter,
edge-type-whitelist voor Cypher — nooit string-interpolatie van
gebruikersinvoer in queries). `dotnet test` groen. Handmatig met curl tegen
de lokale stack: elk endpoint met echte refs; Neo4j gestopt → neighbors/path
geven een Problem-response met detail, de rest blijft werken.

---

### Deelissue 3 — rb-ai: brein-tools als MCP-server en task "agentic"

**Doel.** rb-ai kan als agent over het brein redeneren: een in-process
MCP-server met de zes brein-tools (HTTP naar rb-api via `RB_API_URL`), en
een nieuw task-type `agentic` met Sonnet, `maxTurns` ~8, alleen-brein-tools
in de allowlist, harde timeout en een tool-call-cap. Elk toolresultaat
draagt laag-/trust-labels; het agent-addendum in de systeem-prompt borgt
de voorrangregels (server-side geplakt, zoals `RESEARCH_CONTRACT`).

**Raakvlakken.** `rb-ai/src/ai.ts` (Task-union + MODEL + opties + MCP-server
+ addendum), nieuw `rb-ai/src/brain-tools.ts` (tooldefinities + fetch-laag,
zonder SDK testbaar), `rb-ai/src/validate.ts` + `validate.test.ts`
(task-validatie: onbekend blijft op `cheap` terugvallen), compose/deploy:
`RB_AI`-container krijgt `RB_API_URL` (na-deploy-stap in de PR-beschrijving
benoemen).

**Afhankelijkheden.** Deelissue 2 (de tools zijn dunne clients van de
brein-API).

**Verificatie.** `npm test` groen (validatie + toolresult-formatting +
cap-gedrag met een gemockte fetch). Handmatig: lokale stack, directe POST
naar rb-ai met `task:"agentic"` en een interactievraag; log toont
tool-calls; rb-api onbereikbaar → tools geven "brein niet beschikbaar" als
resultaat en de call faalt netjes binnen de timeout (rb-api's
`RbAiClient`-pad blijft: fout → null → degradatie).

---

### Deelissue 4 — Agentic ask in /ask: gate, vangnet en trace

**Doel.** `AskService` mag kwalificerende vragen laten door-redeneren:
feature-flag `ASK_AGENTIC` (`off`/`auto`/`force`), de auto-gate uit
`docs/BRAIN.md` §2.4 (Ruling met ≥2 kaartnamen, óf lege retrieval), de
agentic call als vervanging van de finale LLM-call mét de bestaande
contextblokken als startpunt, en het vangnet: agent faalt → klassieke
single-pass. Volledig meetbaar: `AskTrace.Agentic` + `BrainSteps`,
`AskMetric.Agentic`, admin-trace toont de brein-stappen.

**Raakvlakken.** `RbRules.Domain`: pure gate-logica (bijv.
`AgenticGate.ShouldEscalate(type, cardMentions, retrievalSignaal, flag)`),
unit-getest. `AskService.cs` (escalatie + vangnet + trace),
`RbAiClient.cs` (task "agentic" doorgeven; timeout volstaat al),
EF-migratie voor de nieuwe trace/metric-kolommen (let op de les van
PR #91: migratie strippen tot de echte delta), `rb-web` admin-trace-detail
(brein-stappen tonen; ontwerptokens, geen emoji).

**Afhankelijkheden.** Deelissue 3.

**Verificatie.** Unit-tests op de gate (alle flag-standen, beide
triggers, geen-escalatie-pad). `dotnet test` + `svelte-check` groen.
Handmatig lokaal met `ASK_AGENTIC=force`: interactievraag → antwoord met
gevulde `BrainSteps` in de admin-trace; rb-ai gestopt → vangnet levert het
single-pass-antwoord. Duurstatistiek toont het agentic-pad apart. Default
staat de flag op `off` — de deploy verandert het live gedrag niet totdat
de beheerder hem omzet.

---

### Deelissue 5 — Brein-verkenner en keten-hygiëne: /graph v2, set-release-claims, drift-meting

**Doel.** Het brein zichtbaar en gezond maken. (a) `/graph` groeit naar
brein-verkenner: naast kaart-buren ook claims, concepten en secties
verkennen via de brein-API (rb-web-proxy's; klikbaar doorlopen langs
refs). (b) De set-release-keten roept nu écht de claims-harvest aan
(`SetReleaseService` stap 3: `ClaimMiningService.RunAsync` gecapt,
best-effort — het hardcoded "overgeslagen"-bericht verdwijnt). (c) Het
kennis-gaten-rapport (#52) krijgt een drift-blok: aantallen per knooptype
in Postgres vs Neo4j, zodat een achterlopende graph gemeten wordt in
plaats van geraden.

**Raakvlakken.** `rb-web/src/routes/graph/*` (+ `+page.server.ts`-loads op
de nieuwe endpoints), `SetReleaseService.cs`, `KnowledgeGapsService.cs`
(+ Neo4j-tellingen via de graph-lees-service uit deelissue 2, best-effort
bij Neo4j-uitval), admin-gaps-scherm.

**Afhankelijkheden.** Deelissues 1 en 2. Onafhankelijk van 3/4 —
parallel te bouwen.

**Verificatie.** `svelte-check` + `tsc` + `dotnet test` groen. Playwright-
screenshots van /graph op 390/768/1280px, horizontale overflow 0 (aanpak
PR #47). Handmatig: setrelease-job draaien → run_log toont een echte
claims-stap i.p.v. "overgeslagen"; gaps-rapport toont drift-tellingen en
degradeert netjes zonder Neo4j.

---

## 6. Wat dit oplevert

Na deze vijf deelissues is KNOWLEDGE.md's einddoel geen visie meer maar
infrastructuur: één identiteitsconventie over twee representaties, zes
stabiele koppelvlakken waar élke toekomstige feature (deck-advies,
errata-impact, interactie-ontdekking) alleen nog een prompt/flow op hoeft
te bouwen, en een ask-pipeline die weet wannéér één retrieval-pass niet
genoeg is — en dan zelf, controleerbaar en binnen budget, het brein
doorloopt.
