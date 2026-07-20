# Architectuur — RB-Rules (arc42)

Dit document beschrijft de architectuur van RB-Rules (Riftbound Rules
Companion, live op https://riftbound-v2.bo3.dev) volgens de arc42-structuur.
Het beschrijft de staat van `main` op dit moment. Elke bewering is bedoeld
verifieerbaar in de repo; waar mogelijk staat het bronbestand erbij.

Verwante ontwerpdocumenten die dieper gaan dan dit overzicht:
`docs/CONVENTIONS.md` (bindende code-conventies), `docs/KNOWLEDGE.md`
(kennislagen-visie), `docs/BRAIN.md` (brein-ontwerp), `docs/AI_AUTH.md`
(abonnement vs. API-key), `docs/DEPLOY.md`, `docs/DATAMODEL.md`,
`docs/CARD_INGEST.md` en `docs/SCRAPING.md`.

> Let op: de repo bevat naast de v2-stack (`rb-api`/`rb-web`/`rb-ai`) ook nog
> de gedeprecte Next.js-PoP in de root (`src/`, `next.config.mjs`,
> `docker-publish.yml`). Dit document beschrijft uitsluitend de v2-stack, die
> de PoP heeft vervangen.

---

## 1. Inleiding & doelen

RB-Rules is één altijd-actuele bron voor Riftbound-regels, bans, errata,
rulings en kaarten, automatisch bijgehouden uit officiële bronnen, met een
AI-vraagbaak die als toernooi-scheidsrechter antwoordt. Het einddoel
(`docs/KNOWLEDGE.md`, `docs/BRAIN.md`) is één samenhangend "brein": alle kennis
vector- én graf-gelinkt, bevraagbaar door AI-tools.

### Kerndoelen

1. **Altijd-actuele regelbron.** Officiële bronnen worden periodiek gescand;
   wijzigingen komen als voor/na-diff in de wijzigingen-feed
   (`IngestService`, `ScanScheduler`).
2. **AI-vraagbaak met bronplicht.** Elk `/ask`-antwoord is herleidbaar:
   §-citaties met ouderregels, kaartfeiten als bewijs, en een zekerheids-label
   (`AskService.cs`, prompt `BasePrompt`).
3. **Degradatie boven uitval.** Uitval van een externe dienst (Ollama, rb-ai,
   Riot, Neo4j) is een verwacht pad: het systeem degradeert netjes in plaats
   van een kale 500 te geven (`docs/CONVENTIONS.md` "Fouten zijn data").

### Kwaliteitseisen (top 5)

| Kwaliteit | Concreet | Verankerd in |
|---|---|---|
| Correctheid/traceerbaarheid | Antwoord scheidt officiële regels van community-consensus, met citaties | `AskService.cs`, `docs/KNOWLEDGE.md` |
| Beschikbaarheid/robuustheid | Elke pijplijnstap is best-effort; één haperende stap stopt de run niet | `JobCatalog.RunAllAsync`, `ScanScheduler` |
| Actualiteit | Scan per cadence, dagelijkse kaart-sync, set-release-keten | `ScanScheduler`, `SetReleaseService` |
| Herbouwbaarheid | Alle afgeleiden (embeddings, mechanics, graph) opnieuw op te bouwen uit Postgres | `docs/CONVENTIONS.md`, `GraphSyncService` |
| Kosten/latency-beheersing | AI opt-in per taak, rate-limiting op dure routes, agentic achter een gate | `rb-ai/src/ai.ts`, `Program.cs` (rate limiter), `AgenticGate` |

---

## 2. Randvoorwaarden

### Technische randvoorwaarden

- **Claude-abonnement, nooit API-key in rb-api.** Al het LLM-verkeer loopt via
  de rb-ai-sidecar op `CLAUDE_CODE_OAUTH_TOKEN` (abonnement). rb-api kent geen
  API-keys (`docs/AI_AUTH.md`, `docs/CONVENTIONS.md`, `rb-ai/src/ai.ts` regel
  16-18, compose `rb-ai`-service).
- **Lokale Ollama bge-m3, provenance heilig.** Embeddings zijn `vector(1024)`
  met HNSW-index; elke embedding bewaart de modelnaam. Een model-wissel is een
  expliciete her-embed, nooit stilzwijgend mixen van dimensies
  (`docs/CONVENTIONS.md`, `EmbeddingService`, `CardEmbeddingPipeline`).
- **Eén Azure-VM (8GB B2ms).** De hele stack draait in één compose-project met
  memory-limits per service, omdat de host-OOM-killer anders willekeurig kiest
  (`deploy/server-setup-v2/docker-compose.yml`, issue #45/#82).
- **Secrets nooit in code.** Alleen via GitHub Secrets of de VM-`.env`; compose
  weigert te starten zonder `POSTGRES_PASSWORD`/`NEO4J_PASSWORD`
  (`docker-compose.yml` `:?`-guards, `v2-deploy.yml` bootstrap-validatie).
- **Strikte laagscheiding** `Api → Infrastructure → Domain`, éénrichting
  (`docs/CONVENTIONS.md`, csproj-referenties).
- **EF-vertaalbaarheid.** Alleen bewezen naar SQL vertaalbare LINQ; geen
  `Contains(char)`, geen eigen methoden in expression trees
  (`docs/CONVENTIONS.md`).

### Organisatorische/stijl-randvoorwaarden

- Nederlandstalige UI en communicatie; Engelse speltermen onvertaald.
- Geen emoji's in de UI; status = kleur + tekst (`rb-web/src/app.css`).
- Nieuwe wensen tussendoor worden eerst een GitHub-issue.
- Nooit mergen/deployen terwijl een live admin-job draait (`v2-deploy.yml`
  job-gate).

---

## 3. Context & afbakening

### Externe systemen

| Extern systeem | Rol | Koppelvlak |
|---|---|---|
| **playriftbound.com / Rules Hub** | Officiële regel-PDF's, patch notes, errata (laag 0) | `IngestService` via `SafeExternalHttp`; bronnen in `SourceSeed.cs` |
| **playriftbound.com/en-us/news/…** (bron-feeds, #167) | Index-pagina's die periodiek nieuwe artikel-URL's opleveren — ontdekt bronnen, ís er geen | `FeedCrawlService` (`RiotNewsFeed`-parser) via `SafeExternalHttp`; feeds in `SourceFeedSeed.cs` |
| **Riot-kaartgallery** | Leidende kaartenbron (JSON, set-facetten, token-kaarten); de riftcodex-API vult daarna alleen aan — extra kaarten en set-metadata, bestaande kaarten blijven onaangeraakt (#150) | `CardSyncService` |
| **Community-bronnen** | riftbound.gg, fanfinity, UVS Games-PDF, mobalytics (laag 1-3) | `SourceSeed.cs`, `ClaimMiningService`, `BanErrataSyncService` |
| **Piltover Archive** | Community-decks (#15, fundament meta-laag 3) | `DeckIngestService` via `SafeExternalHttp`; **alleen** de sitemap en publieke `/decks/view/{uuid}`-pagina's — hun `/api/` is robots-disallowed en wordt nooit aangeraakt; attributie + deep-link per deck |
| **Claude Agent SDK** | LLM-uitvoering op abonnement | `rb-ai` (sidecar), intern koppelvlak `/ask` |
| **Ollama (bge-m3)** | Lokale embeddings | `EmbeddingService` (compose-intern) |
| **Web-push / SMTP** | Meldingen (VAPID) en magic-link-login | `PushService`, `MailService` |
| **Gebruikers** | Spelers, judges (vragen stellen), beheerder (jobs, review) | `rb-web` UI |

### Praktijkvalkuilen bij de externe koppelvlakken

- Riot's domein is **playriftbound.com**; PDF-links zijn opake Sanity-CDN-
  hashes, dus matchen gebeurt op ankertekst ("Core Rules")
  (`docs/CONVENTIONS.md`, `HubDiscovery`, `PdfDiscovery`).
- Riftcodex-site/Mobalytics/community-sites blokkeren datacenter-IP's
  (Cloudflare); de riftcodex-API werkt wél vanaf de VM, maar is sinds #150
  uitdrukkelijk aanvullend — de Riot-gallery is de leidende kaartenbron
  (riftcodex-eerst conserveerde eerder naamschade). Een lege of gedeeltelijke
  community-oogst is een verwacht resultaat, geen bug (`docs/KNOWLEDGE.md`);
  riftcodex-uitval in auto-modus is een run_log-info, geen jobfout.
- De Rules Hub wisselt per request de volgorde van artikellinks; flip-flop-
  suppressie zit in `IngestService` (hash-historie + lege-diff-guard).
- Piltover Archive geeft **403 zonder browser-User-Agent**; de deck-data zit
  als Next.js/RSC-flight in `self.__next_f.push`-chunks (`PiltoverDeckPage`).
  Netiquette is een harde afspraak: ~1,5 s throttle, cap per run met
  hervatting via het run_log-grootboek, her-fetch alleen bij een nieuwere
  sitemap-lastmod — de ~10k-backfill loopt bewust over meerdere runs.
- Bron-feeds (#167): de rules-and-releases-, algemene nieuws- en Rules-Hub-
  index delen dezelfde React-kaartcomponent
  (`data-testid="articlefeaturedcard-component"`) — één `RiotNewsFeed`-parser
  dekt alle drie. Ook de "smalle" rules-and-releases-feed toont af en toe een
  announcements-/organizedplay-artikel tussendoor (vandaar CategoryFilter op
  élke feed, niet alleen de brede hub); sommige artikel-URL's missen het
  categorie-segment (`/en-us/news/<slug>` i.p.v. `/en-us/news/<categorie>/
  <slug>`) en een enkele kaart linkt extern (bv. YouTube) — de parser sluit
  die uit op host in plaats van op categorie. AutoApprove auto-enablet een
  artikel alléén als feed én artikel op een officieel Riot-domein staan
  (`OfficialDomains`) — anders reviewqueue, ook met AutoApprove aan; zo maakt
  een typo/look-alike nooit onbeheerd trust-1 official bronnen aan
  (`FeedCrawlService`, endpoint-guard + crawl-guard, defense-in-depth net als
  `UrlGuard`).

### Contextdiagram

```mermaid
flowchart LR
    players["Spelers / judges"]
    admin["Beheerder"]
    subgraph rb["RB-Rules (riftbound-v2.bo3.dev)"]
      web["rb-web"]
      api["rb-api"]
      ai["rb-ai"]
    end
    riot["playriftbound.com / Rules Hub / Riot-gallery"]
    community["Community-bronnen"]
    pa["Piltover Archive (publieke deck-pagina's)"]
    claude["Claude Agent SDK (abonnement)"]
    notify["Web-push / SMTP"]

    players --> web
    admin --> web
    web --> api
    api --> ai
    ai --> claude
    api -->|scan / kaart-sync| riot
    api -->|harvest / bans| community
    api -->|deck-ingest| pa
    api --> notify
```

---

## 4. Oplossingsstrategie

| Doel/kwaliteit | Strategische keuze | Bewijs |
|---|---|---|
| Onderhoudbaarheid | Strikte lagen `Api → Infrastructure → Domain`; endpoints dun, logica in services, pure logica in Domain | `docs/CONVENTIONS.md`, `Program.cs`, `Endpoints/*.cs` |
| Herbouwbaarheid | Postgres is source of truth; Neo4j en alle brein-afgeleiden zijn herbouwbare projecties | `docs/BRAIN.md` §2.2, `GraphSyncService` |
| Geen API-key in rb-api | Sidecar-patroon: rb-ai draait de Agent SDK op het abonnement, alleen intern bereikbaar | `rb-ai/src/server.ts`, `docs/AI_AUTH.md` |
| Kosten/latency | AI opt-in per taaktype; single-pass standaard, agentic escalatie achter een flag met vangnet | `rb-ai/src/ai.ts`, `AgenticGate`, `AskService` |
| Robuustheid | Elke stap best-effort; fouten zijn data (`run_log`, Problem-responses, null-degradatie) | `JobCatalog`, `RbAiClient`, `AskService` |
| Actualiteit | In-app scheduler i.p.v. externe crontab; set-release als event | `ScanScheduler`, `SetReleaseService` |

---

## 5. Bouwsteenzicht

### Niveau 1 — de drie containers + datastores

```mermaid
flowchart TB
    subgraph vm["Azure-VM — compose-project rb-rules-v2"]
      caddy["Caddy (centraal, extern netwerk)"]
      subgraph internal["intern netwerk"]
        web["rb-web · SvelteKit / adapter-node"]
        api["rb-api · .NET 10 minimal API"]
        ai["rb-ai · Node/TS sidecar (Agent SDK)"]
        pg[("Postgres + pgvector")]
        neo[("Neo4j")]
        ollama["Ollama (bge-m3)"]
      end
    end
    caddy --> web
    web -->|server-load / +server.ts-proxy| api
    api -->|task cheap/hard/research/agentic| ai
    api --> pg
    api --> neo
    api --> ollama
    ai -.->|brein-tools via RB_API_URL| api
```

- **rb-web** is het enige publiek bereikbare component (achter Caddy). De
  browser praat nooit rechtstreeks met rb-api: alle data loopt via
  server-loads (`+page.server.ts`) of `+server.ts`-proxy's, met de
  `api()`-helper (`rb-web/src/lib/api.ts`, `docs/CONVENTIONS.md`).
- **rb-api** en **rb-ai** zitten uitsluitend op het interne compose-netwerk;
  rb-web zit op `internal` én `caddy` (`docker-compose.yml` `networks`).

### rb-api — belangrijkste modules

Lagen (`docs/CONVENTIONS.md`, csproj-referenties):

- **`RbRules.Domain`** — pure, unit-testbare logica zonder I/O: `BrainRef`
  (identiteitsconventie), `QuestionRouter`, `QueryRewriter`, `RrfFusion`,
  `RuleSectionParser`, `SetLegality`, `VariantGrouping`, `RiftboundIds`
  (id-parse/normalisatie, #144), `RiftcodexCardMapper` (bronvorm-adapter,
  #144), `SetCoverage` (dekking per set, #145), `ClaimMining`,
  `RelationMining`, `RelationTriage` (prompt + tolerante parser voor de
  relatie-triage, #199 v1), `AgenticGate`, `SourceSeed`, `SourceFeedSeed` (#167),
  `RiotCardMapper`, `HubDiscovery`, `RiotNewsFeed` (bron-feed-parser, #167),
  `OfficialDomains` (Riot-domein-allowlist voor de feed-AutoApprove-gate, #167),
  `PiltoverDeckPage`/`PiltoverSitemap`/`DeckCardLinker`
  (#15), `IpHashing` (HMAC-SHA256 IP-hash voor de ask-geschiedenis, #157),
  `BenchmarkPrompt` (gecommitteerde-keuze-prompt + deterministische
  letter-parser, #158), `BenchmarkSeed` (judge-vragenset, idempotent net als
  `SourceSeed`), `SourceDossierCompleteness` (#171, pure statusfunctie —
  scan/vervolgstap-uitkomst + opbrengst → volledig/onvolledig/leeg/nooit
  gescand, gedeeld door de dossier-service en het kennis-gaten-rapport),
  `DeckLegality` (#15 fase 3 spoor A: puur op platte kaartfeiten — legaal /
  illegaal-met-reden (nog niet legale set of geband) / onvolledig bij
  niet-gekoppelde kaarten of een set zonder bekende releasedatum),
  `SourceContentKind` (#188 increment 2: bron-type-classificatie — "faq" |
  "patch-notes" | "other" — als LLM-BESLISSING i.p.v. een keyword-heuristiek;
  `SystemPrompt` (Engels, #187-lijn; "faq" beperkt tot Q&A-/clarificatie-
  ARTIKELEN — rulebooks/how-to-play-gidsen zijn expliciet "other", en
  gemengd/onzeker is sinds de #188-review neutraal "other" i.p.v. de oude
  #185-tie-break "patch-notes wint")/`BuildPrompt`/`Parse` (objectvorm-guard,
  zelfde patroon als `ClarificationInformativeness.ParseOperative`),
  `HeuristicKind` (het oude `ClarificationSources`-predicaat, nu het
  deterministische vangnet bij AI-uitval/onbruikbaar antwoord — dáár wint
  patch-notes bij een dubbel-keyword-naam nog wel, conservatief),
  `Resolve` (de ene plek die consumers gebruiken: gepersisteerde
  `Source.ContentKind` als die er is, anders de heuristiek — transitioneel
  gedrag tot een bron opnieuw gescand is sinds deze increment) en
  `TryApplyOverride` (beheerder-override via het source-PATCH-pad: geldige
  kind ⇒ herkomst "admin", definitief; leeg ⇒ wissen/herclassificeren);
  geclassificeerd bij de scan van een trust-1-bron
  (`IngestService.ClassifyContentKindAsync`, gepersisteerd op `Source.
  ContentKind`/`ContentKindSource`, met een run_log-regel wanneer het
  LLM-oordeel afwijkt van de heuristiek), gelezen door
  `ClarificationMiningService` (bronselectie/retractie) en `IngestService`
  (de templated Change)),
  `ClarificationMining` (#177: `ClarificationSources`, de naam-/URL-heuristiek
  die vóór #188 increment 2 de primaire bron-type-classificatie was — nu het
  vangnet achter `SourceContentKind`; `IsMatch`/`IsPatchNotesSignal` blijven
  ongewijzigd als de twee losse substring-predicaten waar `SourceContentKind.
  HeuristicKind` op leunt; `ClarificationMiner`, prompt+parser voor de
  concept-extractie (output in het Engels, #186) — levert sinds #188 ook een
  `operative`-veld per item (het LLM-oordeel: stelt dit item de échte
  regel/definitie/interactie, of kondigt het slechts een wijziging aan?);
  `ClarificationGrounding`, de citaat-in-brontekst-check;
  `ClarificationInformativeness.IsMetaOnly`, de derde poort-toets die een
  kale aankondigingszin zonder regelinhoud weert — sinds #188 niet meer de
  primaire informativiteitsbeslisser (die is het `operative`-LLM-oordeel
  hierboven) maar het deterministische vangnet wanneer dat oordeel ontbreekt
  of uitvalt; `ClarificationInformativeness.JudgeSystemPrompt`/`ParseOperative`
  (#188), de lichte her-toets-prompt die `CorrectionReevaluationService`
  gebruikt om opgeslagen tekst (zonder verse extractie) alsnog te
  classificeren; `ReviewNoteAnchor` (#184), een pure regex-parser die een
  anker-correctie uit een beheerder-opmerking haalt (bv. "mechanic:Recall");
  en sinds #188 increment 3 `ClarificationMiner.GetSystemPrompt`/
  `BuildVocabularyBlock` (het echte anker-vocabulaire — mechaniek-namen +
  primer-concepten — letterlijk in de extractieprompt op `{VOCABULARY}`, zodat
  de LLM een bestaand anker KIEST i.p.v. een vrije-vorm-onderwerp te verzinnen
  dat toch niet resolvet — issue #199: 117/133 pending items faalden hierop)
  en `ClarificationAnchorRepair` (Engelse herstel-pas-prompt — één bestaand
  pending item + citaat + het oorspronkelijke onherkende onderwerp als
  context, anker-KEUZE uit hetzelfde vocabulaire, "none" expliciet een
  eersteklas antwoord; `ParseAnchorChoice` geeft sinds de adversariële
  review een drieledige `AnchorChoice` terug — `Choice`/`None`/`Unusable`,
  zodat de aanroeper een DEFINITIEVE "geen anker past" (terminaal) kan
  onderscheiden van flaky output (transiënt), zelfde objectvorm-guard-patroon
  als `ClarificationInformativeness.ParseOperative`; en `HasLexicalSupport`,
  de deterministische lexicale-steun-poort vóór auto-promotie: de ankerterm
  — volledig voor mechanic/card/section, minstens één significant token
  (≥4 tekens) van key of titel voor een concept — moet aantoonbaar in
  verduidelijking + citaat + oorspronkelijk onderwerp voorkomen, anders is
  een resolvend-maar-verkeerd anker een onzichtbare one-way door naar
  verified) — puur en getest,
  zelfde patroon als `ClaimMining`), `Entities.cs`. Bewuste enige uitzondering:
  het `Pgvector`-datatype op entiteiten (#44, `docs/CONVENTIONS.md`).
- **`RbRules.Domain/Ontology` — ontologie-schema v0 (brein-fundament, nog niet
  bedraad).** Eerste fundament-brok van het Poracle-brein (brein-epic, §2 van de
  geïntegreerde brein-architectuur): een losstaande, pure Domain-module zonder
  DB, migratie of koppeling aan bestaande services/flows. `OntologyTypes`/
  `OntologySchema` leggen de klassenhiërarchie (`EntityType`, multi-label,
  SUBCLASS_OF transitief + acyclisch), de kern-relaties (`RelationType` met
  domain/range, kardinaliteit en logische eigenschappen — transitief/symmetrisch/
  functioneel/acyclisch, plus de reïficatie-dwang voor de gekwalificeerde
  relaties COUNTERS/MODIFIES/GRANTS/REQUIRES) en de disjointness-assen
  (Keyword ⟂ Mechanic ⟂ Status, Spell ⟂ Object) vast als één onveranderlijk,
  machine-leesbaar register — bewust de ÉNE schema-bron waaruit later
  prompt-enums, de parser-poort en Neo4j-constraints gegenereerd worden (dus
  geen losse constanten elders). `OntologyValidationService` is de bijbehorende
  pure, deterministische poort: hij valideert een kandidaat-triple
  `(subjectType, relationType, objectType[, context])` op domain/range mét
  subclass-overerving, kardinaliteit, disjointness en de reïficatie-vlag, en
  geeft een gestructureerd resultaat (geldig + reden + schendingen) terug —
  bedoeld als schema-gate náást het LLM-oordeel, niet in plaats daarvan.
  Modelleer-keuze t.o.v. de kale ASCII-boom in §2.1: `Card` hangt niet onder
  `Object` maar de object-kaarttypes erven van beide (multi-parent), zodat
  `Spell ⟂ Object` vervulbaar blijft. Nog geen endpoint, EF-migratie of
  Neo4j-write — puur, volledig unit-getest (`OntologySchemaTests`).
- **`RbRules.Domain/Provenance.cs` + `ProvenanceAuditService` — provenance-
  ruggengraat (fase 0a, #233).** Versla faalmodus #4 (ontbrekende provenance)
  als schema-invariant, niet als discipline. Twee nieuwe entiteiten (Postgres,
  bron van waarheid): `MiningRun` (PROV-O-*Activity* — welk model/prompt-versie/
  vocab-snapshot leidde feiten af; vult het gat tussen het te-grove `RunLog` en
  de losse feiten) en `Assertion` (gereïficeerd feit-met-herkomst: `Subject` =
  BrainRef van het feit, `WAS_GENERATED_BY`→`MiningRun`, `DERIVED_FROM`=BrainRef
  van de bron, plus model/prompt/embedding-stempel en lichte valid-time — bewust
  géén volledige bitemporaliteit). Het **dubbele write-guard**: de pure
  `AssertionProvenanceGuard` (Domain) + een `RbRulesDbContext.SaveChanges`-poort
  die 'm afdwingt (een Assertion zonder zowel `WAS_GENERATED_BY` als
  `DERIVED_FROM` faalt hard), náást de Neo4j-uniciteitsconstraint (een
  relatie-existentie-constraint is Enterprise-only, dus de garantie leeft in
  Postgres + de deterministische projectie). `EmbeddingProvenance` levert de
  content-hash (SHA-256 van de geëmbede tekst) op elke embedding-rij; de dim is
  structureel 1024 (getypte vector-kolom). `ProvenanceAudit`/`ProvenanceAuditService`
  zijn de **Ring-A-gate** (€0, geen LLM): tel afgeleide feiten zonder Assertion
  en embeddings zonder herkomst, gesplitst in "nieuw" (ná de cutoff — moet 0
  zijn) en "legacy" (geïnventariseerd voor backfill). Puur/EF-vertaalbaar getest
  (`ProvenanceBackboneTests`).
- **`RbRules.Domain/EntityResolution.cs` + `CanonicalEntities.cs` +
  `CanonicalDrift.cs` + `EntityResolutionService` — canonieke entiteiten &
  entity-resolution (fase 1, #225).** Versla faalmodus #1 (duplicatie) en #2
  (synoniem-proliferatie). Drie nieuwe entiteiten (Postgres = SoT, additief
  bovenop `Card.Mechanics[]` — bestaande strings blijven ongemoeid):
  `CanonicalEntity` (één rij per mechanic/keyword/concept — kind uit de
  Concept-tak van de ontologie — met `CanonicalLabel`, het `AltLabels`-
  alias-lexicon, `Definition`+embedding, `Status` candidate/canonical/merged,
  `MergedIntoId`-tombstone en `CreatedByRunId`-0a-provenance), `MergeDecision`
  (expliciete merge-beslissing als first-class knoop: bron/doel, `DecidedBy`
  auto|admin, `Memo` met signaal-uitslag en — cruciaal voor het herstelpad —
  `MovedAltLabels` zodat `Unconsolidate` exact díe labels terugtrekt) en
  `MergeCandidate` (voorgesteld paar → reviewqueue; telt als duplicatie-schuld).
  De **pure** bouwstenen (`EntityResolution.cs`, IO-loos, volledig unit-getest):
  `AliasNormalizer` (case/whitespace/underscore/koppelteken-collapse — het
  canonicalisatie-oppervlak), `Magnitude` (splitst de trailing integer af zodat
  `Assault 2`/`Assault 3` de FAMILIE `Assault` delen met de magnitude als
  parameter — kritiek Risico 2a: nooit weggestript tot aparte entiteit),
  `Trigrams` (Jaccard-similarity, spiegelt `pg_trgm` zodat de gate exact de
  productie-beslissing meet), `EntityResolutionClassifier` (drietraps-signalen
  blocking→trigram→embedding-cosine: 3/3 = auto-merge-kandidaat, 2/3 = review,
  minder = geen match — NOOIT auto-merge op alleen embedding), `EntityResolutionGate`
  (auto-merge standaard UIT — mag pas schrijven ná een gemeten ER-gouden-set-
  precisie ≥ 0,95 ÉN labels ≥ 4 tekens; kritiek Risico 2b) en
  `EntityResolutionGoldSet` (gelabelde merge/niet-merge-paren + precisie-meting,
  patroon eval-scaffold #235). De **service** hangt de IO eromheen: `ResolveAsync`/
  `ResolveOrRegisterAsync` (resolve tegen `CanonicalLabel ∪ AltLabels` VÓÓR
  kandidaat-creatie — stopt synoniem-proliferatie over sets heen),
  `RegisterExistingMechanicsAsync` (additieve, niet-destructieve backfill uit
  `Card.Mechanics`/geaccepteerde `MechanicKeyword`s), `ScanForMergeCandidatesAsync`
  (blocking in-memory bij fase-1-cardinaliteit, gate-consistent; `pg_trgm`+GIN
  staan als schaal-pad in de migratie), `MergeAsync`/`UnconsolidateAsync`
  (tombstone + Decision-memo + omkeerbaar herstelpad — rode draad #236) en
  `DriftSnapshotAsync` (`CanonicalDriftSnapshot`: node-count per kind, singletons,
  duplicatie-schuld — queryable voor inzicht #236). EF-migratie
  `CanonicalEntities225`; getest in `EntityResolutionTests`.
- **`RbRules.Domain/ReifiedInteractions.cs` + `InteractionPromotionGate.cs` +
  `InteractionProjection.cs` + `InteractionExtraction.cs` +
  `InteractionPromotionService` — reïficatie & gekwalificeerde relaties (fase 2,
  #226).** Versla faalmodus #3 (structuurverlies): een kale
  `(:Card)-[:COUNTERS]->(:Card)`-edge is verboden, elk COUNTERS/MODIFIES/GRANTS/
  REQUIRES-feit leeft als gereïficeerde **`Interaction`** (Postgres = SoT) met
  rollen agent/patient (BrainRefs naar Card/Keyword), een `Kind` uit de
  reïficatie-verplichte ontologie-relaties, een optionele `GovernedByRef` naar de
  RuleSection en een `Status` ∈ {candidate, verified, promoted, rejected,
  **model_hypothesized_unruled**}. Condities (window/status/cost) zijn losse,
  individueel weerlegbare **`InteractionCondition`**-knopen met expliciete
  `SubjectRole` i.p.v. platgeslagen in proza. De **reïficatie-vorm-poort**
  (`OntologyValidationService.ValidateReifiedInteraction`, fase 0b) dwingt de rol-
  range en de kale-edge-dwang af. De **promotie-poort**
  (`InteractionPromotionGate`, puur) is deterministisch: `schema ∧ (lexicaal ∨
  consensus≥N) ∧ verdict` → promoted; anders reviewqueue met een `StatusReason`
  die zégt welke poort faalde. Twee bindende bijzonderheden: (a) een levende
  **`RejectionTombstone`** (op de `agent|patient|kind`-dedupe-sleutel) blokkeert
  stil-heropenen — herstel is een expliciete beheerdersactie
  (`LiftTombstonesAsync`); (b) cold-start (kritiek Risico 1) — een emergente
  card×card-hypothese zonder lexicale/consensus-steun wordt NIET verworpen maar
  getierd als `model_hypothesized_unruled` (eigen trust-label, micro-reviewqueue),
  nooit stil weggegooid. Elke acceptatie legt een `Assertion` (0a-provenance,
  subject `interaction:{id}`) én een **`InteractionDecision`**-memo vast (rode
  draad #236 — niets levert onzichtbare state). `InteractionProjection` bouwt de
  gedenormaliseerde `RELATES_TO`-qualifier-cache (window/actor_status/cost_delta/
  tier) — herbouwbaar uit de reïficatie, nooit de bron; bij ≥2 condities per as of
  een patient-rol markeert ze `reifiedOnly`. `InteractionExtraction` definieert de
  tool-forced `emit_interactions`-structured-output-vorm met enum-poorten uit de
  ontologie (`RelationTypeConstraint`); de live rb-ai-call is bewust
  integratie-follow-up (de bestaande `InteractionMiner` kent geen condities en
  tool-forcing vereist een rb-ai-uitbreiding — de promotie-pipeline + structuur
  staan er). EF-migratie `ReifiedInteractions226`; getest in
  `ReifiedInteractionTests` (30 tests).
- **`RbRules.Domain/Reasoning/*` + `ReasoningService` — de redeneer-laag (fase 3,
  #227, §5).** VASTGELEGDE BESLISSING: **één engine, Neo4j-native** — Cypher voor
  monotone inferentie, contradictie via bounded `WHERE NOT EXISTS`, **géén apart
  C#-Datalog** en geen OWL-runtime in de hot-path. `InferenceRuleRegistry` genereert
  de inferentie-regels DETERMINISTISCH uit de ontologie (de ÉNE schema-bron, geen
  losse regel-lijst ernaast): **isa-closure** (GOVERNED_BY-overerving over de
  type-lattice uit `OntologySchema.Ancestors`), **property-chain** (ketens die uit de
  relatie-domain/range in een GOVERNED_BY→RuleSection uitkomen, bv.
  `HAS_KEYWORD ∘ INVOKES ∘ GOVERNED_BY` — een Deflect-vraag bereikt §7.4 in één hop),
  **symmetrische sluiting** (uit de `Symmetric`-trait: INTERACTS_WITH/CONTRADICTS) en
  **subproperty-collapse** (alias-kind → canonieke super-property uit
  `OntologySchema.RelatesToKindSubProperties`, v0 leeg). De denorm-cache RELATES_TO en
  de kennis-loze INTERACTS_WITH-hint zijn geen inferentie-hop (uit een niet-bron leid
  je geen kennis af). Elke afgeleide edge draagt verplicht `derived=true` +
  `derivedByRule` + run-provenance (`DerivedEdgeProvenance`, inzicht #236); afgeleide
  edges zijn **nooit bron** — ze worden bij elke run gewist en opnieuw
  gematerialiseerd, nooit als Postgres-feit gepersisteerd (SoT = de basisfeiten).
  `ContradictionDetector` bouwt bounded read-only patronen — **claim-contradicts-
  official** (community-claim tegen een RuleSection zonder officiële dekking →
  misvattingen-kanaal), **ruling-collision** (botsende geverifieerde rulings →
  escalatie) en, gegenereerd uit `OntologySchema.AreDisjoint`, één **disjointness-
  violation**-patroon per effectief disjunct labelpaar (`:Unit:Spell` vangt kaart-sync-
  schade à la #150 → reviewqueue). Treffers worden via `ConflictRouter` gerouteerd en
  door `ToConflict` naar **`ReasoningConflict`**-rijen (Postgres = SoT ook hier, eigen
  tabel naast bron-niveau `Conflict`) vertaald, idempotent op een dedupe-sleutel.
  `ReasoningService` (job `reason`, ná `graph`) hangt de Cypher-executie eromheen —
  best-effort, want Neo4j zit niet in CI/lokaal; **live-Cypher-executie is
  integratie-follow-up** (zoals de fase-2-projectie), de pure regel-/patroon-generatie
  en de conflict-vertaling zijn wél getest (`InferenceRuleRegistryTests`,
  `ContradictionDetectorTests`). `OntologyConsistencyAudit` (job `owlaudit`, optioneel,
  nooit in "alles") is de **OWL2-RL-nachtaudit-skeleton**: een pure zelf-toets van de
  afgedwongen schema-bron (acyclisch, disjointness vervulbaar, geen dangling
  domain/range) — geen OWL-runtime. EF-migratie `Reasoner227`.
- **`RbRules.Infrastructure`** — services met I/O: `RbRulesDbContext` (EF Core),
  `IngestService`, `FeedCrawlService` (#167, bron-feed-crawl — eerste stap
  van `IngestService.ScanAsync`; sinds #175 ook herkomst-adoptie — een
  herontdekt artikel dat al een `Source` zonder `FeedId` is, krijgt die
  `FeedId` zonder curatie te raken — en `MergeNearDuplicateSourcesAsync`,
  een near-duplicaat-samenvoeging vooraf in elke run die bronnen in
  afwijkende URL-vorm samenvoegt met referentie-omhangen, #144-patroon),
  `RuleChunkPipeline`, `CardSyncService`,
  `CardEmbeddingPipeline`, `EmbeddingService` (Ollama), `AskService`,
  `AskHistoryService` (eigen ask-geschiedenis op user_id/ip_hash, #157),
  `RbAiClient`, `GraphSyncService`/`GraphQueryService`/`BrainGraphService`
  (Neo4j), `BrainService`, `BrainExplorerService` (read-only inspectie-laag over
  de brein-tabellen voor de admin-Brein-verkenner, #236 — puur Postgres, geen
  live-Neo4j), `MechanicMiningService`, `ClaimMiningService`,
  `ClarificationMiningService` (#177, job "clarify" — concept-extractie uit
  officiële FAQ-/clarificatie-artikelen naar `Correction`s met eigen gefocuste
  embedding en onderwerp-anker. Hybride autoriteitspoort: alleen `verified` als
  het concept grounded is (`ClarificationGrounding`: citaat in `Document.
  Content`) én anchored (`ClaimTopicMapper.Resolve` op kaartnaam/mechaniek-
  vocabulaire/§-code/primer-concept) én informative (geen kale
  aankondigingszin — sinds #188 primair `ExtractedClarification.Operative`,
  het LLM-oordeel dat `ClarificationMiner` meelevert; ontbreekt dat (null),
  dan valt `StoreAsync` terug op `ClarificationInformativeness.IsMetaOnly`)
  — anders
  `unverified` + `StatusReason` de reviewqueue in; een `rejected` tombstone
  wordt nooit heropend. Sinds #185 trekt elke run bovendien vóóraf de eerder
  ten onrechte gemínede patch-notes-`Correction`s terug
  (`RetractPatchNotesCorrectionsAsync`, hard delete, idempotent — sinds de
  #188-review achter een consensus-poort: verwijderen alleen als de
  effectieve kind patch-notes is ÉN de deterministische heuristiek dat
  bevestigt of de beheerder de kind expliciet vastzette (herkomst "admin");
  oneens ⇒ overslaan + run_log-waarschuwing, en een wees-bron (Source-rij
  weg) wordt nooit meer op alleen haar id opgeruimd — alleen gelogd voor
  handmatige beoordeling). Dedupliceert
  per concept op (bron, Scope, Ref) + embedding-nabijheid — een parafrase bij
  een her-mine werkt de bestaande ruling bij (nooit degraderend) i.p.v. te
  stapelen, zelfde poort-patroon als `ClaimMiningService`; backfilt bestaande
  bronnen vanzelf, geen tijdvenster op de bronselectie (sinds #188 increment 2:
  `SourceContentKind.Resolve` op elke trust-1-bron in plaats van de kale
  naam-/URL-heuristiek — zelfde uitkomst voor een nog-niet-geclassificeerde
  bron dankzij de null-fallback, maar nu ook correct voor een bron zonder
  magisch woord in zijn slug); de extractieprompt krijgt sinds #188 increment
  3 ook het echte anker-vocabulaire mee (`ClarificationMiner.GetSystemPrompt`
  i.p.v. de kale `SystemPrompt`) — de anker-resolver-opbouw zelf staat
  gedeeld in `AnchorResolverFactory`, die sinds diezelfde increment naast de
  opaque `ClaimTopicMapper` ook de leesbare mechaniek-/concept-vocabulaire
  teruggeeft (`BuildWithVocabularyAsync`; het bestaande `BuildAsync` blijft
  ongewijzigd voor aanroepers die alleen de resolver nodig hebben) zodat
  extractie, herstel-pas en validatie gegarandeerd hetzelfde vocabulaire
  zien),
  `CorrectionReevaluationService` (#184, her-evaluatie van één `Correction`
  op een beheerder-opmerking: draait dezelfde hybride poort opnieuw voor dat
  ene item — roept `ClarificationGrounding`/`ClaimTopicMapper.Resolve` aan
  zonder hun logica te wijzigen; informativiteit toetst het (#188) zelf via
  een lichte `RbAiClient`-classificatie (`ClarificationInformativeness.
  JudgeSystemPrompt`/`ParseOperative` — er is hier geen verse extractie om
  een `Operative`-veld van te krijgen), die bij AI-uitval of onbruikbare
  output terugvalt op `ClarificationInformativeness.IsMetaOnly`; een
  `ReviewNoteAnchor`-anker in de opmerking overschrijft Scope/Ref bij
  resolutie; alleen van toepassing op clarify-mining-`Correction`s (Provenance
  `clarify-mining:{sourceId}`, de enige ontstaanswijze met brontekst om tegen
  te gronden); een `rejected`- of al `verified`-item degradeert/heropent
  nooit, alleen de opmerking wordt dan bewaard. De gate-hertoets zelf staat
  sinds #188 increment 3 in de private `ApplyGateAsync`, geëxtraheerd zodat
  `RepairPendingAnchorsAsync` (zie hieronder) 'm hergebruikt i.p.v.
  dupliceert; het gedeelde pad doet bewust GEEN duplicaat-check (review-fix:
  een handmatige #184-anker-correctie is een bewuste menselijke verplaatsing
  die altijd mag — het #184-spookduplicaat is daar al gedekt door de
  cross-bucket-redding op ReviewNote in `StoreAsync`).
  `RepairPendingAnchorsAsync` (#188 increment 3 herzien na de adversariële
  review; job "clarify" tweede stap — zie `JobCatalog.ClarifyAsync`) is de
  geautomatiseerde tegenhanger: voor de bestaande achterstand (issue #199,
  117/133 pending items met StatusReason "onderwerp … niet herkend") doet
  één rb-ai-aanroep per item een anker-KEUZE uit het vocabulaire
  (`ClarificationAnchorRepair`, met citaat + oorspronkelijk onderwerp als
  context); daarna is alles deterministisch. **Autoriteitsmodel
  (review-uitkomst):** auto-promotie alleen bij lexicale steun
  (`ClarificationAnchorRepair.HasLexicalSupport`) én de volledige
  `ApplyGateAsync`-poort; zonder lexicale steun een AANBEVELING — Scope/Ref
  verhuizen wél (queue toont het item bij het juiste onderwerp), status
  blijft pending met reden "anker hersteld via LLM-suggestie … wacht op
  review", beheerder verifieert via het bestaande /verify-pad
  (#199-principe: machine sorteert voor, mens klikt). **Terminaliteit:** een
  definitieve uitkomst ("none" of een niet-resolvende keuze) plakt
  `TerminalMarker` ("anker-herstel geprobeerd") aan de StatusReason en het
  selectie-predicaat sluit die uit — geen eeuwige her-eligibiliteit of
  window-starvation; AI-uitval/onbruikbare output is transiënt (geen
  marker), en een her-mine die het item bijwerkt schrijft een verse reden
  zonder marker (her-opent eligibility — het beoogde
  herstel-na-nieuwe-informatie-pad). **Duplicaat-bewaking (alléén dit
  geautomatiseerde pad):** vóór elke verplaatsing een CANONIEKE check —
  `ClaimTopicMapper.Resolve` op zowel de keuze als alle bezetters van
  dezelfde bron, vergelijking op `BrainRef.Format()` zodat aliassen
  (kaartvarianten, concept-key vs. -titel) niet langs elkaar heen matchen;
  bezet ⇒ terminale duplicaat-kandidaat-reden ("al bezet … mogelijk
  duplicaat, beoordeel handmatig"), niet verplaatst. Kandidaten: pending +
  zonder `ReviewNote` (#184-eigendom blijft onaangeraakt) + StatusReason
  "niet herkend" zonder `TerminalMarker`. Gecapt (standaard 40) met
  `AnchorRepairResult.CapHit` over alleen echt-eligible items, zelfde
  #190-contract als `ClarificationMineResult.CapHit`. Zet BEWUST geen
  `ReviewNote` op het verplaatste item (zou een geautomatiseerde keuze
  onterecht als mens-beoordeeld labelen) — de canonieke duplicaat-check
  compenseert het ontbreken van de `ReviewNote`-gebaseerde
  cross-bucket-redding die `StoreAsync` (#184, ongewijzigd) voor handmatige
  correcties gebruikt),
  `RelationMiningService`, `RelationTriageService` (#199 v1, zie
  "`RelationTriageService`" hierboven), `InteractionService`, `PrimerService`,
  `KnowledgeRegenerationService` (#187, job "regenerateknowledge" — wipet de
  LLM-afgeleide kennislaag (claim, correction, knowledge_doc kind=primer,
  relation) en reset de mining-markers zodat her-mining met de Engelse
  prompts schoon opnieuw opbouwt; nooit de bron-/mensenwerk-tabellen, geen
  automatische her-generatie erna, expliciete admin-actie),
  `SetReleaseService`, `DeckIngestService` (#15, robots-compliant
  Piltover Archive-ingest), `BenchmarkService` (judge-benchmark-job, draait
  op `AskService` met `AskOptions.Benchmark = true`, #158; sinds #174 ook
  `RunSweepAsync` — dezelfde vragenset door elk model uit `AI_BENCHMARK_MODELS`
  (of een verstandige default), elk 2×, met `Model`/`RunIndex`/`SweepId` op
  `BenchmarkRun` als groepering — de gedeelde kern `RunOneAsync` draait één
  volledige vragenset-doorloop en wordt door zowel `RunAsync` als
  `RunSweepAsync` aangeroepen),
  `KnowledgeGapsService` (kennis-gaten-rapport; sinds #171 ook het
  bron-verwerkingssignaal, zelfde `SourceDossierCompleteness`-statusfunctie
  als de dossier-service), `SourceDossierService` (#171, spiegelbeeld van
  `CardDetailService.DossierAsync`/#127: herkomst via `FeedId`, opbrengst
  via `SourceId` — Document/RuleChunk/Change — en genormaliseerde `SourceUrl`
  — BanEntry/Erratum/Correction — plus claims via de `ClaimSource`-FK, en
  verwerkingsstatus uit `run_log`), `SourceListService` (#180, de admin-
  bronnenlijst-projectie: dezelfde bronnen als `/api/sources` — incl.
  genegeerde, de UI filtert client-side — plus de negeer-kandidaat-vlag.
  Bewust LICHTER dan `SourceDossierService`: vier gebatchte tellingen
  (`run_log` "scan"-regels met status ≠ error, `Change`, `ClaimSource`,
  `Correction.Provenance` op het `clarify-mining:{sourceId}`-prefix) over
  de HELE lijst in plaats van een query per bron — geen N+1. De pure
  drempelbeslissing (`SourceIgnoreCandidacy.Evaluate`, Domain) zit los van
  de I/O), `ReviewNoteService` (#124, beheerder-
  notitie → geverifieerde ruling), `ChatRulingService` (#166, in-chat-ruling →
  verified/pending naar autoriteit), `DeckBrowserService` (#15 fase 3 spoor A:
  read-only projectie boven op de Piltover Archive-decks — lijst/facetten/
  paginering + de per-deck `DeckLegality`-uitkomst; laadt set-releasedatums en
  gebande canonieke kaarten per format één keer per aanroep, net als
  `CardEndpoints`' set-legaliteitslookup), `JobLedger`,
  `JobCatalog`/`JobPaths`/`PathRunner` (#190 — zie de eigen paragraaf
  hieronder), `PushService`,
  `MailService`, `UserAccountService`, `PasskeyService`, en de migraties in
  `Migrations/`.
- **`RbRules.Api`** — compositie: `Program.cs` doet alleen DI-registratie,
  migratie/seed/graph-constraints bij opstart en de `MapXxxEndpoints()`-
  aanroepen. Endpoints per feature als extension-methods:
  `CardEndpoints`, `DeckEndpoints`, `RuleEndpoints`, `KnowledgeEndpoints`,
  `BrainEndpoints`, `AskEndpoints`, `AuthEndpoints`, `FeedEndpoints`,
  `PushEndpoints`, `AdminEndpoints`. Achtergrondwerk via `JobRunner` +
  `JobCatalog`/`JobPaths` + `ScanScheduler`; contracten in `ApiContracts.cs`;
  admin achter `AdminAuthFilter`, gebruikersquota via `UserQuotaFilter`.

**`JobCatalog`/`JobRunner`/`JobPaths`/`PathRunner` (achtergrondjobs + paden,
#59/#122/#190).** `JobCatalog` (Infrastructure) is de vlakke catalogus van
`JobDefinition`'s (naam → `Func<IServiceProvider, Action<string>,
CancellationToken, Task<JobOutcome>>`); `JobOutcome(Detail, Drained = true)`
is sinds #190 het uniforme resultaat van elke job — `Drained` is het
machine-leesbare "geen VERS werk meer deze run"-signaal. Vers-werk-semantiek
(review-fix #190): items die zojuist FAALDEN tellen niet als resterend werk
— een directe herhaling faalt vrijwel zeker opnieuw (rb-ai down, poison
item), dus die horen bij de volgende run/tick, niet bij een drain-lus. De
per-run gecapte jobs leiden Drained af van hun eigen resultaat:
`claims`/`clarify`/`relations`/`relationtriage`/`decks` via `CapHit` (bij claims telt ook een
hertoets-backlog groter dan het `MaxRechecksPerRun`-venster mee — een
goedkope COUNT vooraf; `clarify` is sinds #188 increment 3 zelf twee gecapte
stappen — extractie (`ClarificationMiningService.RunAsync`) + de
anker-herstel-pas (`CorrectionReevaluationService.RepairPendingAnchorsAsync`)
— en is pas Drained als BEIDE hun cap niet raakten), `mine` via
`Remaining − Failed`. `classify` is
ongecapt (één run = de hele backlog) en meldt om dezelfde reden
`Remaining − Failed` — na een volledige pass resteren immers alleen
failures; alle overige jobs zijn per definitie in één run klaar en laten de
default `true` staan. `JobRunner`
(Api) is de generieke, in-memory éénjob-gate: `TryStart(name, work)` zet
`_current`, draait `work` in een losse scope + `Task.Run`, en schrijft bij
afronding altijd een `run_log`-regel (Kind="job", Ref=naam, Status=ok/error,
Detail) — ongeacht of `work` een gewone job of een heel pad is, want beide
hebben exact dezelfde functiehandtekening.

**Per-item budget telt alleen nieuw werk (#200).** `ClaimMiningService`/
`ClarificationMiningService.RunAsync` verhogen de per-run-teller
(`processed`, getoetst aan `maxClaims`/`maxItems`) alléén voor uitkomsten die
écht nieuw werk deden — een gloednieuwe rij (`New`/`Rejected`/`Conflict`/
`Corroborated` resp. `NewVerified`/`NewPending`) of een reële mislukte
poging (`Failed`, embedding-/LLM-call gedaan maar zonder resultaat). Een
dedupe-treffer (`Seen` resp. `Updated`/`RejectedKept`/`Skipped` — hetzelfde
item kwam al eens langs uit dezelfde bron) telt bewust NIET mee: vóór #200
verbrandde een her-run van een document met méér items dan de cap zijn hele
budget aan het opnieuw dedupen van al-opgeslagen items en kwam het nooit
voorbij de eerdere strandingsplek. `ClarificationMiningService.StoreAsync`
controleert sindsdien ook de genormaliseerd-exacte dedupe-treffer vóór de
embedding-call (niet erna) — die treffer heeft geen vector nodig om te
herkennen, dus een herhaald item kost geen Ollama-call meer (de
embedding-poort voor parafrases, `NearestWithin`, verandert niet).
`CapHit`/`Drained` hierboven blijven ongewijzigd: met de nieuwe telling
betekent CapHit nog steeds "er ligt vers werk klaar voor een volgende run".

`JobPaths` (Infrastructure, naast `JobCatalog`) is de padencatalogus: een
`PathDefinition(Name, Steps)` is een geordende lijst `PathStep(JobName,
Drain = false, MaxRepeats = 10)` die elk naar een bestaande `JobCatalog`-naam
verwijst (gevalideerd in `JobPathsTests`); Drain hoort alleen op per-run
gecapte jobs — `classify` staat daarom zonder Drain in het Ingest-pad. Vier
paden — Ingest-, Kaart-, Kennis- en het Volledige-regeneratiepad (zie PRD
§4.5 voor de precieze stappen; bewust GEEN wipe erin, dat blijft
`regenerateknowledge` als losse Gevarenzone-actie). Het Kennis-pad kreeg met
#199 v1 een vijfde stap: `relationtriage` (Drain: true), ná `relations` en
vóór `graph`. Het Ingest-pad kreeg met #206 een nieuwe stap
`consolidatechanges` ná `classify` en vóór `mine` (ongecapt, geen Drain —
zelfde afweging als `classify`: het aantal ongekoppelde changes binnen het
venster is klein).

**`ChangeConsolidationService`/`ChangeFeedService` (changeconsolidatie,
#206).** Een officiële en een community-bron die hetzelfde event melden
(bv. de Rules Hub- en Mobalytics-melding van dezelfde ban-update) staan
zonder ingrijpen als twee losse `Change`-rijen in de feed. `Change` kreeg
een nullable zelf-verwijzende FK, `ConsolidatedWithId` (migratie
`ChangeConsolidation`), naar de PRIMAIRE change van een geconsolideerd
paar — beide rijen blijven bestaan (herleidbaarheid; consolidatie is een
presentatie-koppeling, geen inhoudelijke waarheid, die blijft bij de
structured `BanEntry`-/errata-precedentie #168). `ChangeConsolidationService`
(Infrastructure, job `consolidatechanges`, Ingest-pad ná `classify`) werkt
op nog niet-geconsolideerde ("root") changes binnen een terugkijkvenster
van 30 dagen (ruim boven het kandidaat-venster) en volgt het #188-patroon
"deterministische poort, LLM-oordeel":
- `ChangeConsolidationGate.IsCandidate` (Domain, puur/getest): zelfde
  `ChangeType`, verschillende `SourceId`, `DetectedAt` binnen 72 uur van
  elkaar, én overlappende geraakte referenties — dezelfde AFFECTS-resolutie
  als de graph-projectie (`ChangeAffectsMapper.Resolve`, §6.3), geen aparte
  extractielaag. Geen bruikbare refs aan een van beide kanten ⇒ nooit een
  kandidaat (liever twee kaarten in de feed dan een fout gekoppeld paar).
- `ChangeEventJudge` (Domain): één cheap LLM-call ("beschrijven deze twee
  changes hetzelfde event? ja/nee"), zelfde parser-patroon als `ClaimJudge`
  (objectvorm-guard vóór `TryGetProperty`, `LlmJson.Candidates`). AI-uitval
  of onparseerbaar antwoord ⇒ null; de service behandelt het paar dan als
  NIET geconsolideerd (de veilige kant, met een `run_log`-regel) — dat is
  transiënt: de volgende run probeert het paar gewoon opnieuw.
- **Pair-memo** (review-fix findings 2+6): een "nee"-oordeel wordt per paar
  onthouden via het bestaande run_log-als-memo-idioom (het
  SetReleaseService-/DeckIngestService-grootboekpatroon): kind
  `consolidatechanges`, ref `pair:{minId}-{maxId}`, status `rejected`. De
  kandidaat-lus laadt die memo's in één gebatchte query en slaat zulke
  paren over — elke paar-judge is éénmalig (geen herhaald LLM-budget, geen
  tweede flip-kans op een eerder afgewezen paar). Een "ja" hoeft geen memo
  (de merge zelf is het bewijs); transiënte uitval krijgt er bewust geen.
  Binnen één run bewaakt een set van al-geprobeerde effectieve paren
  (review-fix findings 4+7) dat een via de root-hermapping "ingeklapt" paar
  niet nogmaals gejudged wordt.
- **Ontkoppelen** (review-fix finding 1): `POST
  /api/admin/changes/{id}/unconsolidate` (op de secundaire) zet
  `ConsolidatedWithId` terug op null én schrijft hetzelfde sticky pair-memo
  — zonder memo zou de eerstvolgende run de handmatige correctie meteen
  terugdraaien. In rb-web als "Ontkoppel"-knop bij de bevestiging in
  `/admin/overview/wijzigingen`.
- `ChangeConsolidationPrimary.Wins` (Domain): hoogste bron-trust
  (laagste `TrustTier`) wint, bij gelijke trust de VROEGSTE detectie — het
  omgekeerde tie-break-doel van `Precedence` (#168, waar bij gelijk gezag
  de RECENTSTE datum wint: daar gaat het om welke tekst nu geldt, hier om
  wie een gebeurtenis het eerst meldde). De verliezer krijgt
  `ConsolidatedWithId` = de winnaar; bestaande secundairen van de verliezer
  verhuizen in dezelfde merge mee naar de winnaar (nooit ketens: een
  secundaire wijst altijd naar de wortel-primaire, ook als een later
  binnenkomende hogere-trust-bron de bestaande primaire verdringt).

`ChangeFeedService` (Infrastructure) is de gedeelde query achter zowel het
publieke `GET /api/changes` (`FeedEndpoints`) als het admin-overzicht
(`AdminOverviewService.ChangesAsync`, `GET /api/admin/overview/changes`):
alleen primaire changes (`ConsolidatedWithId == null`) tellen mee in de
lijst/paginering; secundairen komen genest terug als `ConfirmedBy`-lijst
(bron, URL, TrustTier, samenvatting, duiding én voor/na-diff — review-fix
finding 3: de secundaire details blijven ná consolidatie inspecteerbaar)
op de primaire. rb-web (`/` en `/admin/overview/wijzigingen`) toont dat als
een "bevestigd door {bron}"-badge met link, uitklapbaar naar de secundaire
samenvatting/duiding/diff. Dezelfde roots-only-regel geldt voor de
dashboard-tegel (`/api/admin/status` Counts.Changes) en de changes-historie
in het sectie-dossier (`RuleBrowserService.DossierAsync`) — de tegel telt
wat de lijst toont. De feed-curatie-delete
(`DELETE /api/admin/changes/{id}`, `ChangeFeedService.DeleteAsync`,
review-fix finding 9) verwijdert bij een primaire óók haar secundairen in
dezelfde transactie — het is per definitie hetzelfde event, en de kale
FK-SetNull zou de kaart anders meteen laten herrijzen vanuit de andere
bron; een secundaire los verwijderen kan gewoon. Interne consumers
(kennis-hertoets #119, classificatie-backfill #58, push, bron-dossier,
graph-projectie) blijven bewust ongefilterd — die moeten élke detectie
zien; elk draagt een comment met de reden.

**`RelationTriageService` (LLM-triage voor relatievoorstellen, #199 v1).**
Per open `Relation` (Status "unreviewed", `Recommendation == null`,
`ArchivedAt == null` — een geparkeerd voorstel kost geen LLM-budget en
krijgt geen aanbeveling, review-fix findings 2/4/7) één
retrieval-gegronde LLM-beoordeling (cap 40/run, zelfde vers-werk-semantiek
als de andere gecapte miners) — de context is bewust goedkoop (geen
embeddings): per ref (`BrainRef`) een exacte lookup (kaarttekst, §-chunk op
`SourceId`+`SectionCode`, primer-doc op `Topic`, claim op id) of, alleen voor
`mechanic:`-refs, dezelfde ILike-eerste-§-match als
`RelationMiningService.BuildMechanicsContextAsync`. De parser
(`RelationTriage.Parse`, Domain) volgt de #188-increment-3-les: een
objectvorm-guard vóór elke `TryGetProperty`, want `LlmJson.Candidates` levert
ook array-vormige blokken op. Het resultaat (`accept`/`reject`/`unsure` + één
zin Engelse motivering, met de geraadpleegde refs erin gevouwen) landt op drie
nullable kolommen (`Relation.Recommendation`, `RecommendationReason`,
`RecommendedAt`, migratie `RelationTriage`) — bewust GEEN vierde kolom voor de
refs. Dit is uitdrukkelijk GEEN autoriteitspad (de optionele auto-accept uit
issue #199 is bewust niet gebouwd: een LLM-oordeel alleen mag nooit een
statuswijziging dragen zonder deterministisch vangnet of mens): `Status`
verandert alleen via `RelationTriageService.DecideAsync` (het bestaande
accept-/reject-pad, nu ook aangeroepen door de losse `AdminEndpoints`-acties)
of `BulkDecideAsync` (#199, de bulk-actie per aanbevelingsgroep — één
transactie over alle "unreviewed", niet-gearchiveerde voorstellen met die
aanbeveling, endpoint `POST /api/admin/relations/bulk-decide`). De bulk is
**TOCTOU-gefenced** (review-fix finding 1): de UI stuurt de geladen
groepstelling (`expectedCount`) en de max `RecommendedAt` binnen de groep
(`asOf`) mee; wijkt de herberekende groep af (andere telling, óf een item
met een nieuwere aanbeveling — bv. door een gelijktijdige
`relationtriage`-run in het kennis-pad), dan wordt er NIETS beslist en
antwoordt het endpoint 409 — de beheerder beslist wat hij zag, nooit wat er
intussen bij kwam (dat zou de facto het auto-accept-pad zijn dat v1 níét
heeft). De fence werkt over paginering heen zonder id-lijsten; de
`AdminOverviewService`-groepstellingen dragen `AsOf` mee en de bulk-knoppen
renderen alléén in de te-reviewen-weergave
(`relationBulkActionsVisible`, rb-web) zodat telling, zichtbare items en
actie-scope hetzelfde universum zijn (review-fix findings 3/5/8).
Input-validatie zit puur op het contract-record
(`RelationBulkDecideRequest.ValidationError`, finding 6): ontbrekende of
ongeldige velden zijn een 400, geen NRE-500. Een mens-beoordeeld voorstel
(Status niet meer "unreviewed") wordt nooit her-getriaged.
`PathRunner.RunAsync(path, sp, report, ct, findJob?)` (Infrastructure)
draait de stappen sequentieel via `job.Run(sp, ...)` — `findJob` is een
test-seam die in productie op `JobCatalog.Find` defaultet. Bij `Drain: true`
herhaalt hij dezelfde job tot `outcome.Drained`, met twee vangrails
(review-fix #190): de harde `MaxRepeats`-grens én een no-progress-guard die
de lus vroegtijdig stopt zodra twee opeenvolgende runs een identiek
resultaat geven (zelfde `Detail` én nog steeds niet Drained — dan eet iets
het per-run-budget op zonder dat er iets landt); beide vangrails zijn geen
fout, het pad loopt door naar de volgende stap en de volgende run pakt de
rest op dankzij de idempotente jobs. Elke (herhaalde) stap logt een eigen
`run_log`-regel (Kind=padnaam, Ref=stapnaam), geschreven via een EIGEN,
verse `IServiceScope`/DbContext per schrijfactie en best-effort (review-fix
#190): nooit de scoped context waarin een stap net crashte — een vervuilde
change-tracker zou de error-regel kunnen verliezen of half werk van de
gefaalde stap alsnog committen, en een log-exceptie mag de oorspronkelijke
stap-fout nooit maskeren. Gooit een stap een exception, dan logt
`PathRunner` die stap als "error" en gooit door — het pad stopt daar
(JobRunner's catch markeert de hele padrun als error); de al voltooide
stappen blijven staan. Een pad start via
`jobs.TryStart(pathName, (sp, report, ct) => PathRunner.RunAsync(path, sp,
report, ct))` in `AdminEndpoints`/`ScanScheduler` — dezelfde
`JobRunner`-instantie, dus een pad en een losse job kunnen nooit tegelijk
draaien. `ScanScheduler` heeft ook een pad-equivalent van zijn
`TryStartPeriodicJobAsync` (`TryStartPeriodicPathAsync`), maar de
schedule-lijst (`PathSchedules`) is bewust leeg — de mogelijkheid staat
klaar, de bestaande nachtelijke/wekelijkse cadans van de losse jobs
verandert niet.

Belangrijke endpointgroepen (`Endpoints/*.cs`): `/api/cards*`, `/api/decks*`
(#15 fase 3 spoor A: lijst/facetten/detail, read-only), `/api/rules*`,
`/api/knowledge`, `/api/brain/*` (search, node, neighbors, path, evidence,
contradictions), `/api/ask` + `/api/ask/stream` + `/api/ask/history` (eigen
ask-geschiedenis op user_id/ip_hash, geen id-parameter, #157) +
`/api/ask/ruling` (in-chat ruling vastleggen, autoriteit bepaalt verified vs
pending, #166), `/api/auth/*`
(magic-link + passkeys), `/api/changes|sources|bans|sets/upcoming`,
`/api/push/*`, `/api/admin/*` (o.a. vraag-traces: `/asktraces` als slanke
lijst, `/asktraces/{id}` met het volledige gesprek — antwoord + eerdere
beurten, #143; bron-dossier: `/sources/{id}/dossier`, #171; correcties:
`/corrections` — projectie via `AdminOverviewService.CorrectionsAsync`, incl.
bron-naam en `UrlGuard`-gesaniteerde link, #184 — `/corrections/{id}/verify|
reject|reevaluate`; paden, #190: `GET /paths` (de catalogus, voor de
beheer-UI), `POST /paths/{name}` — zelfde `TryStart`-conflictgedrag (202/409)
als `POST /jobs/{name}`, de padnaam verschijnt vanzelf op `/status`; relaties,
#199 v1: `/relations/{id}/accept|reject` lopen via
`RelationTriageService.DecideAsync` (ongewijzigd contract), plus
`POST /relations/bulk-decide` — de bulk-actie per aanbevelingsgroep, één
transactie, hergebruikt hetzelfde pad per item, alleen unreviewed én
niet-gearchiveerd; TOCTOU-gefenced op `expectedCount`+`asOf` → 409 bij een
veranderde groep, 400 bij ontbrekende/ongeldige velden
(`RelationBulkDecideRequest.ValidationError`), alles-of-niets);
Brein-verkenner (#236, `BrainAdminEndpoints` → `BrainExplorerService`, alle
GET, read-only, admin-gated): `/brein/overzicht` (tegel-tellingen per
brein-tabel), `/brein/entities` (canonieke entiteiten + alt-labels +
merge-status, `kind`/`status`/`page`), `/brein/interactions` (gereïficeerde
interacties + condities + tier + provenance-anker, `status`/`page`; sinds #243
levert het endpoint naast de items een `entities`-lookup: de distinct kaart-/
mechanic-refs van de pagina, in twee EF-vertaalbare batch-queries opgelost naar
naam + afbeelding + `/cards/{id}`-href resp. canoniek label + definitie — voor
hover-detail en doorklik in de UI, read-only, geen tweede client-fetch),
`/brein/assertions/{**ref}` (de provenance-keten van een feit-ref:
WAS_GENERATED_BY/DERIVED_FROM/VERIFIED_BY — catch-all zodat section-/card-refs
met slash meekomen), `/brein/conflicts` (reasoning-tegenspraken + routering,
`status`/`page`), `/brein/answertraces` + `/brein/answertrace/{id}` (lijst +
herspeelbaar detail: dragende subgraaf/paden + trust-toen + epoch-stempels),
`/brein/observability` (fase-7 rollups: mining-precisie + canonieke drift +
duplicatie-schuld + tier-verdelingen; de Neo4j/GDS-delen blijven leeg tot de
graph-jobs draaien — nette lege staat), en `/brein/cockpit` (brein-jobs-ui: de
operationele pipeline-status — per-stap-tellingen (interacties + mechanic-
predicaten, canonieke entiteiten, conflicts/open) + de laatste-run per brein-job
(uit `RunLog` Kind="job", Ref=jobnaam — greatest-n-per-group in-memory, niet
server-side) + de `/ask`-retrieval-flag (`BreinRetrievalSettings.Enabled`, uit de
env-singleton, niet de DB)). Puur additief: raakt geen bestaande
endpoint/service/flow, leest bestaande tabellen (geen migratie). De cockpit-
trigger-knoppen zelf starten via het bestaande `POST /api/admin/jobs/{name}`
(JobRunner-gate: één job tegelijk, 409 als er al een draait) — de vier
brein-jobs (`breinmine-interacties`, `breinmine-predicaten`, `breinprojectie`,
`reason`) waren voorheen API-only.

### rb-ai — belangrijkste modules

- `src/server.ts` — minimale `node:http`-server met `/health` (incl.
  capaciteits- en pooltellers), `/ask`, `/ask/stream` (NDJSON-streaming),
  `/prewarm` (#154, altijd direct 202) en de tool-forced brein-extractie
  `/extract/interactions` + `/extract/predicates` (#226, zie §6.6); koppelt de
  client-verbinding aan een `AbortController` zodat een weggelopen client de
  Claude-call afbreekt, en vertaalt de capaciteitsgrens (#155) naar een 429 met
  machine-leesbare code.
- `src/ai.ts` — `askClaude` met vier taaktypes en de per-taak-modellen; één
  optiebron `buildQueryOptions` voor koud én warm (contract-getest tegen
  drift); de server-side prompt-addenda `RESEARCH_CONTRACT` en
  `AGENT_ADDENDUM`; de in-process brein-MCP-server (`createBrainMcpServer`);
  `extractWithTool` (#226) — één geforceerde in-process MCP-tool die de
  gevalideerde argumenten in een closure vangt (tool-forced structured output).
- `src/extract.ts` — PUUR (zonder Agent SDK, unit-getest): de
  vocabulaire→zod-schema-vertaling voor de brein-extractie (#226). Bouwt de
  enum-poorten voor `emit_interactions`/`emit_mechanic_predicates` uit het door
  rb-api aangeleverde ontologie-vocabulaire (spiegelt de .NET-Domain
  `InteractionExtraction`/`MechanicPredicateExtraction`) + de request-validatie.
- `src/warmpool.ts` — signaal-gedreven warme-sessie-pool (#154): houdt na een
  `/prewarm`-signaal maximaal één voorverwarmde cheap-SDK-sessie klaar
  (subprocess boot alvast, API-call pas bij de vraag; één sessie = één call,
  nooit hergebruik over vragen heen), met TTL, dode-sessie-degradatie naar
  koud en kill-switch `AI_WARM_POOL=0`.
- `src/concurrency.ts` — globale semaphore op gelijktijdige SDK-sessies
  (#155): `AI_MAX_CONCURRENCY` (default 3), agentic weegt 2, korte wachtrij
  (30 s) en daarna een nette 429 die rb-api als bestaand degradatiepad ziet.
- `src/brain-tools.ts` — de zes brein-tooldefinities + fetch-laag naar rb-api
  (`RB_API_URL`), met tool-call-cap.
- `src/relations.ts` — afsplitsen van relatievoorstellen uit het agent-antwoord
  (`RELATIONS_MARKER`).
- `src/validate.ts` — request-validatie (onbekende taak valt terug op `cheap`).

### rb-web — belangrijkste modules

Paginastructuur (`rb-web/src/routes/`): `/` (**Overzicht-dashboard**, #214),
`/wijzigingen` (de volledige wijzigingen-feed, #214 — verhuisd van de root),
`/rules` (+ `/rules/[code]`), `/primer`, `/ask` (+ `/ask/stream`), `/cards`
(+ `/cards/[id]` + `explain`), `/decks` (+ `/decks/[id]`, #15 fase 3 spoor A:
browser + legaliteitsbadge, detail met decklijst per sectie en deep-link naar
Piltover Archive — read-only, geen editor), `/graph` ("Brein"-verkenner),
`/rulings`, `/account` (+ passkey/verify), `/admin` (+ `/admin/status`,
`/admin/overview/[kind]`, en de read-only **Brein-verkenner** `/admin/brein`
met sub-routes `entities`/`interactions`/`conflicts`/`answertrace`, #236 — eigen
`+layout` met tab-nav + auth-guard, server-loads proxyen de `/api/admin/brein/*`-
endpoints; de interacties- en answertrace-pagina laden hun provenance-keten
resp. herspeelbaar detail server-side via `?sel=`/`?id=`, geen client-fetch).
Het Brein-overzicht draagt bovenaan de **operationele cockpit** (brein-jobs-ui):
de server-load proxyt óók `/api/admin/brein/cockpit`, en een `job`-action (zelfde
patroon/409-afhandeling als de `job`-action op `/admin`) triggert de vier
brein-jobs via `POST /api/admin/jobs/{name}`; de knop-disabled/"Bezig"-staat komt
uit de bestaande `/admin/status`-poll).
Een globale **`+error.svelte`** (#219) rendert binnen
de shell: bij 404 een "zoekende" poro + terug-links naar `/` en `/ask`, bij
elke andere status een generieke variant (kop = `status + boodschap`). De
status → tekst-logica staat als pure, unit-geteste functie in
`$lib/errorCopy.ts`; de component is puur presentatie.

**Samengestelde shell (#214).** `+layout.svelte` is de globale shell: een
vaste **zijbalk** links (212px op desktop; merk met poro-mascotte +
woordmerk "Poracle" (#216), globaal
zoekveld → `/ask?q=`, gegroepeerde nav Actueel/Kennis met decoratieve
domein-stippen, onderaan Account/Beheer + thema-schakelaar), de **content**
in het midden, en een **opt-in rechterrail** (vanaf 1080px). Pagina's leveren
rail-inhoud via een context-store (`$lib/shell.svelte.ts`, `useShell().rail =
{ snippet, kind, count, title }`) — `kind:'context'` (leespagina's:
"op deze pagina / bron") of `kind:'filters'` (lijstpagina's). Op **mobiel**
(<760px) klapt de zijbalk in tot een bovenbalk met **hamburger → slide-over
drawer** (scrim); filters zitten dan achter een **"Filter"-knop met teller die
een bottom-sheet opent** waarin de chips wrappen (Reset + "Toon N") —
**nooit horizontaal filterscrollen**. De thema-schakelaar zet `data-theme` op
`documentElement` en bewaart de keuze in `localStorage`; een inline-script in
`app.html` zet het thema vóór de eerste verf (FOUC-vrij).

**Merk-assets (#216).** De poro-mascotte leeft in één herbruikbaar component
`rb-web/src/lib/PoroMark.svelte` (viewBox `0 0 120 124`, `size`-prop,
crème lijf via `--poro-*`-CSS-vars zodat 'm op elk oppervlak crème blijft in
licht én donker) — ingezet in de publieke shell, de beheer-shell en de
home-hero. Een opt-in **`animate`-prop** (#220, `false`/`'idle'`/`'wink'`; default
`false` → statisch, bestaande gebruiken onveranderd) legt subtiele beweging op een
binnen-`<g>` en de oog-groepen (de buiten-`<svg>` blijft vrij voor de
brand-link-hover-bounce). Alles bevriest bij `prefers-reduced-motion: reduce` via
een component-eigen `animation: none` (de globale `app.css`-vangrail zet enkel de
duur op ~0, niet de animation-name). De favicon/PWA-iconen zijn een afgeronde **gele** tegel (`#f5c518`)
met de poro in donkere lijn (`#20190a`): `static/favicon.svg` + `static/icon.svg`
(SVG, letterlijke kleuren) en de daaruit gerenderde `static/icon-192.png`,
`static/icon-512.png` en `static/apple-touch-icon.png`. `app.html` linkt
`rel="icon"` (svg) + `rel="apple-touch-icon"` (png); `manifest.webmanifest`
(`name`/`short_name` "Poracle", `theme_color:#f5c518`,
`background_color:#f6f7f9`) somt de icon-set op.

**Rail-patroon op de long-tail-routes (#214).** De rail-store wordt sinds de
design-refresh op de hele publieke long-tail toegepast. Lijstpagina's leveren
een **filter-rail** (`kind:'filters'`, mobiel de bottom-sheet met teller):
`/rules` (bron-chips per `TocSource`, alleen gemount bij >1 bron; de boom
filtert client-side), `/rulings` (onderwerp-type-filter, verhuisd uit de
inline chip-rij) en `/decks` (domein- + sorteerfilter als `.filter-form`,
gelijk aan `/cards`) — het actieve filter blijft als verwijderbare chip in de
content. Leespagina's leveren een **contextuele rail** (`kind:'context'`):
`/cards/[id]` ("Op deze pagina" met ankers naar de aanwezige dossier-secties +
een domein-blok) en `/primer` ("Concepten", met een rustiger/bredere
leeskolom). Kaart- en deckdetail dragen bovendien een **domein-tint** (3px
domein-rand/-streep via `domainColorVar`, chips getint via `--dom-*` +
`color-mix`). Alles op bestaande `app.css`-tokens — geen nieuwe tokens.

**Beheer-console (#214).** De `/admin`-routes draaien in een **eigen shell**:
`admin/+layout.svelte` vervangt de publieke chrome binnen het beheer door een
console-zijbalk ("← naar de site", merk poro + "Poracle [beheer]" (#216), nav met
tel-badges, Gevarenzone in rood, thema-schakelaar onderaan; mobiel <760px een
eigen bovenbalk + slide-over drawer met scrim). De publieke chrome wordt
**onderdrukt zonder `+layout.svelte` te wijzigen**: `onMount` zet `admin-shell`
op `<html>` (weg bij `onDestroy`), en `:global`-regels gated op
`html.admin-shell` verbergen de publieke `.sidebar`/`.topbar`/`.site-footer` en
zetten het `.shell`-grid op één kolom — de onderdrukking lekt zo nooit buiten
het beheer (terug naar `/` herstelt de publieke zijbalk). `admin/+layout.server.ts`
levert alleen `{ authed }` (volle nav bij ingelogd, anders alleen merk-chrome
rond het login-scherm; sinds #236 doet het bij ingelogd één goedkope
`/api/admin/brein/overzicht`-fetch voor de **Brein**-nav-badge — brein-uitval
laat de badge stil weg). De **tel-badges** komen verder uit de al geladen
`page.data` (`status.counts.openCorrections` → Reviewqueue, `sources.length` →
Bronnen) — geen badge waar die data ontbreekt (nette degradatie); de
thema-schakelaar hergebruikt de bestaande `useShell()`-store. Het
Overzicht-dashboard voegt in `admin/+page.server.ts` één extra **parallelle**
fetch toe aan de bestaande `Promise.all`: graph-drift
(`/api/admin/overview/gaps` → `.drift`, `.catch(() => null)`) voor de
drift-tabel — alle overige data-bindings en alle form-actions zijn 1-op-1
behouden.

Gedeelde `$lib`: `api.ts` (server-side proxy), `AnswerView.svelte`,
`RuleWidget.svelte`, `CardWidget.svelte`, `RbText.svelte`, `ChangeCard.svelte`,
`markdown.ts` + `rbtokens.ts` (sanitize + icoon-injectie vóór `{@html}`),
`answerFormat.ts`, `changeCard.ts`, `passkeys.ts`, `quota.ts`, `ranges.ts`
(compacte reeksweergave, #145), `types.ts` (API-responstypen die meer dan
één route deelt). Ontwerptokens in `app.css` (`var(--accent)` etc.).
`ChangeCard.svelte` (#210) is het eerste presentatiecomponent met een
optioneel admin-actieslot via Svelte 5 snippets (`actions`,
`confirmationActions`) en een `compact`-prop voor dichte contexten — het
patroon voor toekomstige herbruikbare kaarten (i.p.v. per-route duplicatie).

**Ontwerptokens: theme-aware (#214).** Sinds de design-refresh is `app.css`
licht-standaard mét een koele-graphite donker-variant. De neutralen
(`--bg`/`--surface`/`--surface-deep`/`--text`/`--muted`/`--border`) en de
semantische tokens worden op drie niveaus gezet: `:root` (licht),
`@media (prefers-color-scheme: dark)` (volgt de OS-voorkeur) en een expliciete
`:root[data-theme='dark'|'light']`-override die in béíde richtingen wint (voor
een latere thema-schakelaar en voor Playwright). Geel (`--accent`) is
uitsluitend het actie-/merk-accent, nooit een sfeerkleur. Nieuw is een
canonieke **domein-kleurtaal** `--dom-fury|body|mind|calm|chaos|order|colorless`
— gelijk in beide thema's, één plek om een hue te wijzigen; gebruikt door de
kaarttekst-runen (`:rb_fury:` …) en de ChangeCard-randstreep/chips. De
iOS-16px-formfix en de horizontale-overflow-vangrail blijven ongemoeid.
_(De per-route layout-uitrol dekt sinds #214 de hele publieke site én het
beheer — zie de rail- en console-alinea's hierboven; de tokens en de
change→domein-afleiding zijn layout-onafhankelijk gefundeerd.)_

**Change→domein-afleiding (#214).** De feed kleurt elke wijziging met het
domein van de geraakte kaart(en). `ChangeDomains` (Infrastructure) leidt dit
read-time af (geen kolom/migratie) uit de gestructureerde ban-/errata-laag
(`BanEntry`/`Erratum` → `Card.Domains`): alleen `ban`/`errata` hebben zo'n
kaart-laag, de rest valt terug op geen domein. Gedeeld door
`PublicStatsService` (#214) voedt de dashboard-tegels via publiek
`GET /api/stats` (read-time COUNTs: canonieke kaarten, geverifieerde rulings,
bans, recente wijzigingen — geen migratie). `ChangeFeedService`
(publiek `/api/changes`) en `AdminOverviewService`
(`/api/admin/overview/changes`); beide DTO's dragen een `Domain`-veld.

### Datastores

- **Postgres + pgvector** — source of truth. Getypeerde `vector(1024)` met
  HNSW; snake_case; EF-migraties bij opstart (`RbRulesDbContext`, `Migrations/`,
  `Program.cs`). Sinds fase 1 (#225) ook de `pg_trgm`-extensie — voorlopig als
  gedocumenteerd schaal-pad voor het lexicale entity-resolution-signaal (de
  fase-1-scorer draait in-memory en gate-consistent). Sinds fase 4 (#228) het
  immutable `answer_trace` (+ `answer_trace_support`, cascade) — het GraphRAG-
  auditspoor per /ask-antwoord (§6/#236, migratie `AnswerTrace228`). Sinds fase 5
  (#229) `mechanic_predicate` — de getypeerde mechanic-predicaten
  (triggers_on/prevents/grants/requires_target) die de abductieve hypothese-motor
  voeden (migratie `MechanicPredicates229`). Sinds fase 6 (#230) `ontology_version`
  (semver-historie + structuur-vingerafdruk per toegepaste migratie),
  `schema_proposal` (de `:Proposed`-staging-reviewqueue) en `lifecycle_event` (het
  geconsolideerde, herstelbare tombstone-/deprecatie-/staleness-log) — migratie
  `Governance230`.
- **Neo4j** — herbouwbare projectie van de kennislagen; getypeerde relaties,
  batched UNWIND, dictionaries-only params (`GraphSyncService`, `GraphSchema`).
- **Ollama** — lokale embedding-service (bge-m3).

---

## 6. Runtimezicht

### 6.1 De /ask-flow (parallelle retrieval, met agentic escalatie)

`AskService.AskCoreAsync` is één retrieval-fase + één afrondende LLM-call, met
een optionele agentic escalatie. Sinds #152 is de retrieval-fase geen
seriële ketting meer maar overlappende kanalen op vaste slots — zelfde input
geeft byte-voor-byte dezelfde prompt, ongeacht de volgorde waarin de kanalen
concurrent landen:

```mermaid
sequenceDiagram
    participant W as rb-web
    participant A as rb-api · AskService
    participant AI as rb-ai
    participant O as Ollama
    participant DB as Postgres
    W->>A: POST /api/ask
    A->>A: history + rewrite-cache-lookup (LRU, #152)
    par rewrite overlapt met de rewrite-onafhankelijke kanalen
        A->>AI: query-rewrite (cheap, overgeslagen bij cache-hit)
    and
        A->>O: embed de rúwe vraag
        A->>DB: naam-match + FTS (ruwe tekst) + banlijst — elk op eigen DbContext (IDbContextFactory)
    end
    A->>A: QuestionRouter.Classify
    A->>O: batch-embed resterende zoekqueries (alleen wat nog ontbreekt)
    par onafhankelijke lees-kanalen, elk op een eigen context
        A->>DB: vector-kanaal (RuleChunks, per query)
        A->>DB: FTS-her-run (alleen als de rewrite iets wezenlijks veranderde)
        A->>DB: primer + rulings + kaartcontext + claims + misvattingen
    end
    A->>A: alle kanaalslots innen; RRF-fusie (bron-bias per vraagtype)
    A->>A: prompt-piramide (officieel > primer > community)
    alt agentic-escalatie (gate: Ruling met 2+ kaarten / lege retrieval — of gebruiker: Grondig binnen dagtegoed)
        A->>AI: task=agentic (brein-tools)
        AI-->>A: antwoord + brein-stappen (of vangnet)
    else single-pass (ook bij gebruikerskeuze Snel)
        A->>AI: task=cheap (of hard bij foto)
        AI-->>A: antwoord (evt. streamend)
    end
    A->>DB: AskMetric + AskTrace (incl. PhaseTimings + kanaal-uitval-markers, best-effort)
    A-->>W: antwoord + citaties + kaarten + claims
```

Kernpunten (`AskService.cs`):

1. **Query-rewrite met overlap en cache** (#66, #152): de rewrite-call start
   als taak tegelijk met het embedden van de ruwe vraag en de
   rewrite-onafhankelijke kanalen (naam-match, FTS op de ruwe tekst,
   banlijst) — de volledige rewrite-latentie valt zo van het kritieke pad.
   Een kleine procesbrede **LRU-cache** (`RewriteCache`, sleutel = de
   genormaliseerde vraag) slaat de rewrite-call helemaal over bij een
   herhaalde/gelijksoortige vraag; een null-uitkomst (uitval/onzin-output)
   wordt nooit gecacht. Uitval blijft het bestaande pad → rauwe vraag.
2. **Parallelle retrieval-kanalen** (#152): de onafhankelijke lees-kanalen
   (vector per query, FTS, primer, rulings, kaartcontext, banlijst, claims,
   misvattingen) draaien concurrent, elk op een eigen `RbRulesDbContext` uit
   `IDbContextFactory` (een DbContext is niet thread-safe). Zonder factory
   (unit-tests op EF InMemory) draaien dezelfde kanalen sequentieel op de
   scoped context — functioneel identiek, alleen niet concurrent. Elk kanaal
   levert aan een vast slot; faalt één kanaal, dan is het resultaat een leeg
   kanaal plus een marker in `AskTrace.Sections` (`kanaal-uitval: ...`) —
   nooit een 500, en de overige kanalen zijn onaangeraakt.
3. **Multi-channel retrieval**: vector (pgvector per query), full-text
   (Postgres FTS), gefuseerd met **RRF** (`RrfFusion`, Domain) plus bron-bias
   per vraagtype; daarnaast primer (top-3 approved), geverifieerde rulings,
   kaartcontext (naam/mechaniek/lexicaal/semantisch), banlijst en
   community-claims (`ClaimRetrieval.TakeFor`, afstandsplafond).
4. **Prompt-piramide**: blokken staan in vaste volgorde officieel > primer >
   community, elk expliciet gelabeld (`docs/KNOWLEDGE.md`).
5. **Per-fase-instrumentatie** (#152): wandkloktijd van rewrite/embed/
   retrieval/AI als compacte JSON op `AskTrace.PhaseTimings` (`AskPhases`,
   Domain) — zichtbaar in de beheer-trace-uitklap en als gemiddelde
   fase-verdeling op `/api/ask/stats`. De fasen overlappen (parallelle
   pipeline), dus de som is bewust niet gelijk aan de totale duur.
6. **Streaming** (#31): citaties/claims/vraagtype gaan vooraf via `onMeta`; het
   antwoord komt woord-voor-woord via NDJSON (`/api/ask/stream` →
   `RbAiClient.AskStreamAsync`).
7. **Agentic escalatie** (#107, `docs/BRAIN.md` §2.4): pas ná de retrieval
   beslist `AgenticGate.Decide` of de vraag mag door-redeneren over het
   brein (flag `ASK_AGENTIC` = off/auto/force). Faalt de agent → **vangnet**:
   de klassieke single-pass draait alsnog. De agent kan ontdekte verbanden als
   relatievoorstel achterlaten (#120, `AgenticRelationService`).
   De **aanpak-keuze** (#153) voedt dezelfde beslissing: een ingelogde vrager
   kiest per vraag Auto (gate beslist), Snel (nooit escaleren) of Grondig
   (agent forceren). Server-authoritatief: het `approach`-request-veld wordt
   alleen gehonoreerd voor een geauthenticeerde gebruiker
   (`RequestUserContext`), de flag blijft de meester (off ⇒ Grondig bestaat
   niet), foto's blijven op het vision-pad en Grondig kost een eigen
   dagquotum (`AppUser.DailyAgenticQuota`; teller = metric-rijen met
   `EscalatedBy = "user"`). Niet-gehonoreerde keuzes vallen terug op Auto met
   een reden in de respons-metadata (`Approach`/`ApproachReason`) — de UI
   toont daarop de "quota op — automatisch beantwoord"-melding; op het
   streamingpad reist de terugmelding al in het meta-frame mee.
8. **Degradatie** (#100): valt de embedding uit (Ollama weg / model niet
   gepulld), dan vervallen alle vector-kanalen en draait de vraag door op FTS +
   naam/mechaniek/lexicaal — nooit een kale 500. Valt rb-ai uit, dan geeft
   `RbAiClient` null en toont `AskService` `UnavailableAnswer`.

### 6.2 De scan-pipeline

`IngestService.ScanAsync`: eerst de geplande bronnenlijst filteren op
`Enabled && IgnoredAt == null` (#180) — **`Enabled`** is "tijdelijk uit",
**`IgnoredAt`** (+ `IgnoreReason`) is een bewuste, blijvende beoordeling
("dit levert niets op"); beide slaan de scan-lus over, maar zijn
onafhankelijke velden (een genegeerde bron mag `Enabled = true` houden). Een
gerichte handmatige rescan via `sourceId` bypasst dit filter net zoals hij
`Enabled` al bypaste. **Bereik van "genegeerd"** (#180-review, finding 7):
dezelfde `IgnoredAt == null`-poort zit ook in de verwerkende consumers —
claims-mining (`ClaimMiningService`), clarify-mining
(`ClarificationMiningService`), ban-/errata-extractie
(`BanErrataSyncService`), regelsectie-indexering (`RuleChunkPipeline`) en
het kennis-gaten-rapport (`KnowledgeGapsService`, beide bronsignalen) —
geen LLM-/embed-kosten en geen aandachtssignalen meer voor een bron die per
beoordeling niets oplevert. Bewust NIET gefilterd: alle weergave-/lookup-
plekken (dossier, overzichten, joins), de dedupe-sets van scout en
feed-crawl (een genegeerde bron moet juist bekend blijven zodat hij nooit
opnieuw wordt voorgesteld of aangemaakt), de graph-projectie (Source-knopen
dragen provenance van bestaande claims/rulings), de patch-notes-retractie
(datahygiëne op bestaande corrections) en de Rules Hub-bans-extractie (vaste
seed-bron). De near-duplicaat-samenvoeging
(`FeedCrawlService.MergeNearDuplicateSourcesAsync`) slaat een groep met een
genegeerde rij erin volledig over (run_log-melding, zelfde
veiligste-variant als de #175-uitzondering) — een merge zou de bewuste
negeer-beslissing stil ongedaan kunnen maken. Per bron dan: fetch →
boilerplate-strip (incl. de playriftbound "Related Articles"-carousel sinds
#205, `TextUtils.StripBoilerplate`) → hash → bron-type-classificatie (#188
increment 2, alleen trust-1-bronnen zonder LLM-classificatie of met een
heuristische; een "admin"-override wordt nooit geherclassificeerd —
`ClassifyContentKindAsync`, `SourceContentKind`) → diff → AI-classify →
store + `run_log`, met flip-flop-suppressie en een naclassificatie-ronde
(#58) voor changes die eerder zonder classificatie zijn opgeslagen.
**Strip-versionering + stille rebaseline** (#205-review, findings 1/3):
`TextUtils.BoilerplateVersion` (const, historie in de docstring) versioneert
het strip-gedrag; `Source.StripVersion` legt vast met welke versie
`LastHash` berekend is. Elke strip-wijziging verandert de gestripte tekst —
en dus de hash — van élke bron tegelijk; zonder versionering zou één
verbetering een golf junk-"changes" over het hele register geven (de diff
toont alleen de weggevallen boilerplate). Wijkt `StripVersion` af (null =
rij van vóór het veld), dan REBASELINET de scan stil: verse Document-rij
met de opnieuw gestripte inhoud (mining-markers `ClaimsMinedAt`/`ClarifiedAt`
reizen mee van de vorige versie — inhoudelijk hetzelfde artikel, dus geen
her-mine-kosten), `LastHash` + `StripVersion` bijgewerkt, run_log-detail
"boilerplate-rebaseline v{n}", en GÉÉN diff/Change. Elke bron rebaselinet zo
exact één keer, automatisch, ongeacht wie de eerste scan triggert. Een échte
inhoudelijke wijziging rond de bump komt via "rebaseline eerst, diff daarna"
binnen (twee scans, gedocumenteerd); valt hij exact in het ene
rebaseline-venster, dan absorbeert de baseline hem — hash-only kan
strip-ruis niet van echte delta scheiden binnen één scan, en dat venster is
één scan-tick. **One-shot patch-notes-Change (#205):** heeft een trust-1
patch-notes-bron nog GEEN niet-editoriale `Change` én nog geen
one-shot-memo (`PatchNotesOneShotChange.IsCandidate`, Domain), dan behandelt
de scan de volledige inhoud als delta — op het gewone pad óók als de hash
ongewijzigd is t.o.v. `LastHash` (de vroege "unchanged"-kortsluiting wijkt
daarvoor), en óók op het rebaseline-pad (de Vendetta-backfill valt anders
precies in dat gat: haar eerste post-deploy-scan is meteen haar rebaseline).
Het minten schrijft een **run_log-memo** (kind `oneshot-patchnotes`, Ref =
sourceId — zelfde grootboek-idioom als `SetReleaseService`) dat de guard
sluit onder zijn eigen output (#205-review, findings 4/5/9): wordt de
geminte Change (meteen of via de #58-naclassificatie later) als "editorial"
gelabeld, dan blokkeert het memo een eeuwige her-mint-lus. Dat dekt zowel
een gloednieuwe bron als de backfill van een bron die vóór deze fix al
zonder Change gescand was; een terugkerende patch-notes-pagina
(core-rules-patch-notes) raakt deze tak na haar eigen eerste scan nooit
meer, want die heeft dan al een niet-editoriale Change. De
`ScanScheduler`
(BackgroundService) draait elk uur een scan van
de bronnen die aan de beurt zijn (cadence), stuurt web-push bij high-severity,
her-indexeert regels en bans bij nieuwe/gewijzigde documenten, checkt de
set-release-keten, en draait dagelijks kaart-sync + embeddings, nachtelijk
claims- en relatie-mining, wekelijks de bronnen-scout en elke 3 uur de
Piltover-decks-verversing (#15 fase 3, spoor C: hergebruikt de bestaande
"decks"-job/`DeckIngestService` ongewijzigd via hetzelfde
`TryStartPeriodicJobAsync`-patroon als relaties/scout — de ~10k-deck-backfill
loopt zo binnen enkele dagen leeg via het run_log-grootboek, waarna hetzelfde
venster alleen nog nieuwe/gewijzigde decks ophaalt).

Changeconsolidatie (#206, `ChangeConsolidationService`, zie §5) draait
bewust NIET binnen `ScanAsync` zelf maar als losse jobstap
`consolidatechanges` ná `classify` in het Ingest-pad — zo blijft `ScanAsync`
onaangeraakt en is de stap ook los te draaien (bv. terugwerkend op een
bestaand paar). Daarnaast draait hij UURLIJKS mee in de
ScanScheduler-periodiek (review-fix finding 5: de uurlijkse scan maakt de
duplicaten, dus de consolidatie mag niet alleen van het handmatige
ingest-pad afhangen) — via dezelfde `TryStartPeriodicJobAsync`-mechaniek
als relations/scout/decks/clarify, declaratief in
`ScanScheduler.JobSchedules` (getest: elke naam bestaat in de JobCatalog).
Goedkoop: zonder verse changes levert de kandidaat-poort niets op en de
pair-memo's voorkomen dubbele LLM-toetsen. Bekende beperking:
`PushService.NotifyHighSeverityAsync`
vuurt al binnen de `scan`-job zelf (vóór `consolidatechanges` in het pad),
dus een net-geconsolideerd paar kan in theorie nog twee pushmeldingen voor
hetzelfde event opleveren — geen incident, wel een bewuste, niet
opgeloste follow-up (feed-presentatie raakt hier de meldingen-laag niet).

Nachtrun (#245): naast de interval-schedules hierboven start `ScanScheduler`
sinds #245 binnen een KLOK-venster (default 00:00–11:00 lokaal, Europe/Amsterdam;
env-overschrijfbaar via `NIGHTLY_START_HOUR`/`NIGHTLY_END_HOUR`/`NIGHTLY_TZ` in de
VM-`.env`) de job `nachtrun`: de volledige ONGECAPTE kennis-keten in één
JobRunner-slot — `all` (met ongecapte mechaniek-mining) → `breinentiteiten` (#250)
→ `breinmine-interacties`
→ `breinmine-predicaten` → `breinprojectie` → `reason`. De mining-services krijgen
een optionele `deadline` (het venster-einde) en stoppen daar netjes; hun watermark
bewaart de voortgang, dus de resterende backlog volgt de volgende nacht. De
klok-logica leeft in `NightlyWindow` (Domain, puur/getest) i.p.v.
`Scheduling.IsWindowDue` — de grote run moet 's nachts vállen, niet "X uur sinds de
vorige run". Maximaal één keer per lokale kalenderdag (`NightlyWindow.RanToday` op
het run_log-grootboek); de single-job-gate (`JobRunner.TryStart`) voorkomt
dubbelstart en houdt het slot vast tot de deadline. Overdag blijven de losse jobs
gecapt (`DefaultMaxFocusCards`/`DefaultMaxSubjects` = 40 in de mining-services) —
`nachtrun` is de enige ongecapte route, ook handmatig te starten (beheer → Brein →
"Volledige nachtrun"); handmatig buiten het venster draait zonder deadline
(volledige drain).

**Noodrem `NIGHTLY_ENABLED`** (#249/#251): met `NIGHTLY_ENABLED=false` in de
VM-`.env` start `ScanScheduler` de nachtrun niet meer automatisch — bedoeld om de
nachtelijke keten te pauzeren zolang de extractie nog niet deugt, zonder code te
wijzigen of te deployen. De vlag zit bewust in `TryStartNightlyAsync`, niet in de
`JobCatalog`: **handmatig starten via de beheer-knop blijft altijd werken**. Default
is AAN, en alleen een expliciete uit-waarde (`false`/`0`/`no`/`off`,
hoofdletterongevoelig) schakelt uit — een typfout in de `.env` mag de keten niet
stilletjes stilleggen. Een ongeldig VENSTER laat de pauze-keuze intact
(`NightlyRunSettings.FromEnvironment`, getest).

### 6.3 De graph-sync

`GraphSyncService.SyncAsync` projecteert Postgres naar Neo4j binnen **één
transactie** (rollback bij fout — geen half leeggeruimde graph). Het schrijft
`Card`/`Set`/`Domain`/`Tag`/`Mechanic` + facet-relaties, en sinds #104 de
kennislagen: `RuleSection` (+`PART_OF`), `Concept` (+`EXPLAINS`), `Claim`
(+`ABOUT`/`SUPPORTED_BY`, alleen accepted/unreviewed), `Source`, `Erratum`
(+`SUPERSEDES`), `Change` (+`AFFECTS`), plus de dynamische
`RELATES_TO {kind, trust, explanation, status}`-relaties via de reviewpoort
(`RelationProjection`) — de #199 v1-triage-aanbeveling
(`Relation.Recommendation`/`RecommendationReason`/`RecommendedAt`) is bewust
GEEN edge-property en beïnvloedt `RelationProjection.ShouldProject` niet: een
aanbeveling is geen status. Sinds #191 ook `Ruling` (+`ABOUT`/`SUPPORTED_BY`,
alleen `status=verified`) voor geverifieerde rulings/clarificaties —
dezelfde ABOUT-resolutie als `Claim` (`RulingTopicMapper`, Scope→topic via
`RulingsTopics`), gematcht op `SourceRef` t.o.v. `Source.Url`
(`SourceScout.UrlCandidates`, gedeeld met het bron-dossier #171); `kind`
(clarify/chat/review-note/other, uit `Provenance`) onderscheidt de
herkomst. Elke knoop draagt een `ref`-property volgens de
`BrainRef`-conventie. Wees-opruiming verwijdert kaarten/facetten die geen
canonieke printing meer zijn (#57).

Sinds fase 0a (#233) projecteert dezelfde transactie ook de provenance-tak:
`MiningRun`- en `Assertion`-knopen met `(:Assertion)-[:WAS_GENERATED_BY]->(:MiningRun)`
en `(:Assertion)-[:DERIVED_FROM]->(bron-knoop op `ref`)`. Idempotent herbouwbaar
uit Postgres (bron van waarheid); de DERIVED_FROM-doelen (Source/RuleSection/
Card/…) bestaan al vóór deze stap, dus de label-loze ref-match resolveert
(zelfde patroon als `RELATES_TO`). De provenance-shape-garantie (elke Assertion
draagt beide edges) leeft in de Postgres-schrijfpoort, niet in een Neo4j-
constraint.

Sinds fase 2 (#226) projecteert dezelfde transactie ook de reïficatie-tak
(`InteractionProjection.BuildProjectionRows`, puur + getest): niet-verworpen
`Interaction`-knopen (`:Interaction:Concept`) met `HAS_ROLE {role}`-edges naar de
agent/patient-fillers, `REQUIRES_CONDITION`-edges naar `:Condition`-knopen, een
optionele `GOVERNED_BY`-edge naar de RuleSection, en — alleen voor verankerde
(promoted/verified) interacties — de gedenormaliseerde
`RELATES_TO {kind, window, actorStatus, costDelta, tier, reifiedOnly,
source:'interaction'}`-qualifier-cache. Die cache is NOOIT de bron: ze is volledig
herbouwbaar uit de reïficatie, en bij ≥2 condities per as of een patient-rol
markeert `reifiedOnly` dat consumenten de `Interaction` moeten lezen. Rejected
interacties leven alleen als `RejectionTombstone` (herstelpad), niet als knoop.
Let op: dit is een additieve uitbreiding op dezelfde transactionele rebuild als de
provenance-tak — nog niet geverifieerd tegen een live Neo4j (geen lokale instance),
wel via dezelfde batched-UNWIND/dictionaries-only-patronen als de bewezen
Assertion-projectie.

Changeconsolidatie (#206) is bewust NIET in deze projectie verwerkt: de
`Change`-query hierboven selecteert nog steeds ALLE rijen, dus zowel een
primaire als een geconsolideerde secundaire krijgen elk hun eigen `Change`-
knoop + `AFFECTS`-edges, en `Change.ConsolidatedWithId` wordt geen
graph-property. Consolidatie is feed-presentatie (welke kaart de gebruiker
ziet), geen kennisrelatie — de graph blijft de volledige, ongefilterde
brontrail tonen.

### 6.4 De reasoner (redeneer-run)

`ReasoningService.RunAsync` (job `reason`, logisch ná `graph`) geeft Neo4j zijn
eerste echte lees-reden: hij *leidt af* i.p.v. *op te slaan* (fase 3, #227, §5).
VASTGELEGDE BESLISSING: **één engine, Neo4j-native** (Cypher; géén C#-Datalog). De
run opent met een deterministische `MiningRun` (kind "reasoning", geen LLM/embedding)
als provenance-anker, en verloopt in drie stappen. **(1)** Afgeleide edges opruimen:
`MATCH ()-[r]->() WHERE r.derived = true DELETE r` — afgeleide edges zijn nooit bron,
ze worden elke run herberekend (basisfeiten blijven ongemoeid). **(2)** Monotone
inferentie: elke regel uit `InferenceRuleRegistry.All` draait als idempotente,
batched Cypher-MERGE (dictionaries-only params) die de afgeleide edge tagt met
`derived=true`, `derivedByRule=<regel-id>` en de run-provenance
(`runId`/`model='deterministic'`/`derivedAt`). **(3)** Bounded contradictie-detectie:
elk read-only `ContradictionDetector`-patroon RETURNt treffers; die worden via
`ConflictRouter` naar het juiste kanaal (misvattingen/reviewqueue/escalatie) en door
`ToConflict` naar `ReasoningConflict`-rijen in Postgres vertaald — idempotent op de
`patternId|subject|counter`-dedupe-sleutel, zodat een herhaalde run geen dubbele
rijen maakt.

Neo4j-uitval is een verwacht pad: de graaf-stappen zijn best-effort
(`Neo4jException`/driver-fout → de run doet niets en meldt "graph niet beschikbaar"),
Postgres blijft leidend en de afgeleide edges zijn bij de volgende run herberekenbaar.
Net als de fase-2-reïficatie-projectie is de **live-Cypher-executie nog niet tegen een
draaiende Neo4j geverifieerd** (geen lokale instance): de regel- en patroon-generatie
uit de ontologie, de derived-edge-tagging en de treffer→conflict-vertaling zijn puur
en getest (`InferenceRuleRegistryTests`, `ContradictionDetectorTests`), de executie is
integratie-follow-up. Sommige inferentie-regels (isa-overerving, property-chains)
veronderstellen bron-edges die de huidige projectie nog niet materialiseert
(bv. `Mechanic-[:GOVERNED_BY]->RuleSection`, class-anchor-labels) — die projectie-
uitbreiding hoort bij dezelfde follow-up; de regels staan er al, correct getagd.

De **OWL2-RL-nachtaudit** (`OntologyConsistencyAudit`, job `owlaudit`) is per beslissing
een **skeleton**: geen OWL/Turtle-runtime, maar een pure zelf-toets van de afgedwongen
schema-bron (`OntologySchema`) op acycliciteit, vervulbare disjointness en
niet-danglende domain/range. Optioneel en nooit in de "alles"-keten.

### 6.5 De brein-projectie

`BreinProjectionService.ProjectAsync` (job `breinprojectie`, logisch ná `graph`)
projecteert de brein-lagen die `GraphSyncService` NOG NIET dekt idempotent naar
Neo4j (fase live-graph, #227, §3.5): `CanonicalEntity` (fase 1),
`MechanicPredicate` (fase 5) en `OntologyVersion` (fase 6). Het is bewust een
**aparte service + eigen transactie + eigen job**, volledig ADDITIEF naast de
`graph`-sync — die transactie/job wordt NIET aangeraakt (minimaliseer risico; de
job is handmatig getriggerd en breekt de site niet). De rij-/param-/sleutel-opbouw
is puur en getest (`BrainProjection`, `BrainProjectionTests`); de service is de
dunne IO-schil eromheen (zelfde arbeidsdeling als `InteractionProjection` ↔
GraphSyncService).

Geprojecteerd (MERGE op de canonieke `ref`, idempotent herbouwbaar uit
Postgres = SoT): `:CanonicalEntity {ref, kind, canonicalLabel, brainRef,
altLabels, status, definition, createdByRun}` (alle statussen, óók merged
tombstones — herstelpad-historie) met `MERGED_INTO`-edges (tombstone →
overlevende); `:MechanicPredicate {ref, predicate, objectToken, status,
createdByRun}` (candidate + reviewed, rejected overgeslagen) met
`HAS_PREDICATE`-edges vanaf de subject-`:CanonicalEntity`; `:OntologyVersion
{ref, version, fingerprint, bumpKind, notes, current, appliedAt, createdByRun}`
op SemVer geordend met een `PRECEDES`-keten en een `current`-vlag op de hoogste
versie. Elk owned label kent zijn eigen wees-opruiming (`MATCH (n:Label) WHERE
NOT n.ref IN $refs DETACH DELETE n`), zodat de projectie een exacte spiegel van
Postgres blijft.

**KRITIEK — ref-namespace-scheiding.** De owned-node-refs dragen een EIGEN prefix
(`entity:` / `predicate:` / `ontologyversion:`) die NIET in het `BrainRef`-alfabet
zit, en de projectie linkt NIET naar GraphSyncService-eigen knopen
(Card/Mechanic/MiningRun/…). Dat is bewust en dubbel gemotiveerd: (a)
GraphSyncService matcht `DERIVED_FROM`/`RELATES_TO` label-LOOS op `ref` — zou een
brein-knoop de `mechanic:`-ref van een bestaande `:Mechanic`-knoop delen, dan werd
zo'n match ambigu en maakte hij dubbele edges; (b) GraphSyncService `DETACH
DELETE`t + `CREATE`t zijn eigen labels (MiningRun/Assertion/Interaction) elke
rebuild — een edge daarheen zou een latere `graph`-run weer weggooien. Provenance
rijdt daarom als `createdByRun`-property mee, niet als edge; de BrainRef-vorm
(`mechanic:`/`concept:`/`tag:`) staat als `brainRef`-property klaar voor
toekomstige entity-linking (fase 4), maar nooit als node-sleutel. De drie
ref-constraints staan in `GraphSchema` (idempotent, `IF NOT EXISTS`).

Neo4j-uitval is een verwacht pad: de hele projectie is best-effort (driver-fout →
de run doet niets en meldt "graph niet beschikbaar"; Postgres blijft leidend en de
projectie is bij de volgende run herbouwbaar), zelfde patroon als de graph-sync en
de reasoner. Net als die twee is de **live-Cypher-executie nog niet tegen een
draaiende Neo4j geverifieerd** (geen lokale instance): de rij-opbouw is puur en
getest, de echte write is integratie-follow-up (§8). Bewuste afbakening t.o.v. §3.5
en de #227-scope: `MiningRun`/`Assertion`/`Interaction`/`Condition` blijven bij
`GraphSyncService` (§6.3) — geen overlappende projectie — en `ALT_LABEL` is een
`altLabels`-property (scalaire strings zonder eigen identiteit/provenance) i.p.v.
een edge naar een `:Alias`-knoop (KISS/YAGNI; een triviale follow-up als
entity-linking het nodig heeft).

### 6.6 De brein-mining (tool-forced extractie)

De brein-mining (#226, §3.1/§3.4) is de eerste **live rb-ai-koppeling** van de
fase-2/5-extractie-vorm: waar `InteractionExtraction`/`MechanicPredicateExtraction`
(Domain, puur) tot nu toe alleen de VORM + de tweede-muur-parser leverden, halen
twee handmatige jobs nu daadwerkelijk gestructureerde kandidaten bij rb-ai en laten
ze door de fase-2-poort. Volledig ADDITIEF: de bestaande
`InteractionService`/`InteractionMiner` (lexicaal-paar-gebaseerd, conditie-loos) en
alle andere mining blijven ongemoeid.

**rb-ai-kant (tool-forced structured output).** `POST /extract/interactions` en
`POST /extract/predicates` (server.ts) krijgen kaart-/regeltekst + het
ontologie-vocabulaire (aangeboden refs, kind-/conditie-/rol-enums, Window/Status-
lexicon) van rb-api. `extractWithTool` (ai.ts) draait één geforceerde in-process
MCP-tool (`emit_interactions`/`emit_mechanic_predicates`) waarvan het zod-schema —
gebouwd door de PURE `extract.ts` uit dat vocabulaire — de enum-poorten
dichttimmert: het model KAN geen ref/kind/window buiten de aangeboden set noemen.
De tool-handler vangt de gevalideerde argumenten in een closure; de kandidaten
reizen dus via de tool-input, niet via de antwoordtekst. Uitval (tool niet
geroepen, timeout, run gefaald) → de endpoint antwoordt 500, wat `RbAiClient` als
AI-uitval leest (null, nette degradatie); een 200 met lege lijst betekent "geen
kandidaten" — dat onderscheid blijft bewaard.

**rb-api-kant (mining-orkestratie).** Drie jobs in `JobCatalog`. De twee
LLM-jobs staan bewust NIET in de "alles"-keten (LLM-zwaar, rb-ai-afhankelijk —
expliciete beheerdersactie, zelfde lijn als `graph`/`reason`/`claims`); de
deterministische entiteiten-stap ervóór is goedkoop en draait wél mee in de
nachtrun:

- `breinentiteiten` (`EntityResolutionService.RegisterExistingMechanicsAsync`,
  #250). Het **enige pad dat `CanonicalEntity`-rijen aandraagt**: de mining
  RESOLVEERT bewust alleen (leest), dus zonder deze stap blijft de entiteitenlaag
  leeg — live stond `canonicalEntities` op 0 terwijl 387 interacties naar
  `mechanic:{label}` verwezen, vond `breinmine-predicaten` nul subjects en bleven
  de mechanic-hovers zonder definitie. Bron: `Card.Mechanics[]` + geaccepteerde
  `MechanicKeyword`-termen; elke rij krijgt status `candidate` + `CreatedByRunId`
  (geen stille promotie naar `canonical` — dat blijft de review-poort). De
  definitie komt deterministisch uit de officiële regeltekst
  (`KeywordDefinition`, Domain/puur): alleen een `RuleChunk` die met de term
  ÓPENT telt als definitie, anders blijft het veld leeg (de hover degradeert al
  netjes, en een verzonnen definitie is erger dan geen). Idempotent — herhaald
  draaien levert `Created = 0`; bestaande entiteiten zonder definitie worden
  alsnog aangevuld zónder hun status te raken. Geen LLM, geen migratie.
- `breinmine-interacties` (`BreinInteractionMiningService`). Per bounded batch
  focus-kaarten: bouwt het aangeboden vocabulaire, haalt kandidaten via rb-ai, en
  laat elke kandidaat door `InteractionPromotionService` — schema ∧ (lexicaal ∨
  consensus) ∧ verdict, met de cold-start-tier voor emergente card×card-hypotheses.
  **Entity-resolutie (fase 1) draait VÓÓR kandidaat-creatie**: een
  keyword-surface-form wordt tegen de canonieke laag geresolveerd zodat
  "Deflecting"/"Deflect 2" op één ref landen (versla #2). De **lexicale poort**
  toetst tegen de RAUWE bron-tekst (het bewijsanker), niet tegen de ref-headers die
  de prompt draagt. Feit + provenance (`Assertion` met `DERIVED_FROM` = de
  bronkaart) worden **atomair** door de promotie-service gepersisteerd; deze job
  voegt geen eigen graaf-write toe.

  **Herijkt in #249 (`PromptVersion` → `breinmine-interactions-v2`).** Een meting
  op 383 live interacties liet zien dat 264 (69%) kaart↔EIGEN-keyword was — een
  feit dat al gratis en deterministisch bestaat (`GraphSyncService.MechanicPairs`
  projecteert `Card.Mechanics[]` als `HAS_MECHANIC`-edges, en de keywords staan
  gebracket in de kaarttekst) — terwijl mech↔mech, het eigenlijke doel, op 5
  (1,3%) bleef en 77% geen enkele conditie droeg. Oorzaak: de aanbieding bood
  vooral een kaart mét haar eigen keywords aan, en de lexicale poort beloonde
  precies die tautologie (de kaart ÍS de ene rol; haar keyword staat in haar eigen
  tekst). Drie samenhangende wijzigingen:
  1. **kaart↔eigen-keyword wordt niet meer geminded** — het paar wordt ná de parse
     en vóór de promotie overgeslagen (apart geteld als `SkippedKnown`, zichtbaar
     in het run-detail), met een guard in de promotie-poort als tweede slot
     (`InteractionTautology`, Domain/puur). **Geen grafsteen**: er is niets
     verworpen dat later gegrond kan blijken, en een tombstone zou een latere
     échte gekwalificeerde interactie op dezelfde sleutel blokkeren.
  2. **de aanbieding is herzien** — de keyword-refs van de HELE gedeelde-mechaniek-
     buurt (focus + partners) i.p.v. alleen die van de focus-kaart, plus
     `RuleChunk`s die ≥2 aangeboden keyword-labels noemen als **bewijstekst**. Die
     regelsecties zijn nadrukkelijk GEEN offered ref: de HAS_ROLE-range is
     Card/Keyword, een `RuleSection` kan geen agent/patient zijn. Zo kunnen
     mech↔mech-paren überhaupt ontstaan én een officieel anker hebben.
  3. **de lexicale poort is verscherpt** — rollen moeten verschillende entiteiten
     zijn, en het bewijs moet een RELATIE uitdrukken: beide rollen verankerd in
     ÉÉN bewijs-eenheid én minstens één van beide TEXTUEEL (`InteractionEvidence`
     met `EvidenceAnchor` None/Identity/Textual). Twee identiteits-ankers ("deze
     kaart is deze kaart") tellen niet meer als steun.

  De **deterministische graph-projectie blijft ongemoeid**: kaart→mechanic-edges
  bestaan gewoon door, ze komen alleen niet meer uit een dure LLM-omweg
  (regressietest `GraphMechanicProjectionTests` op `GraphSyncService.MechanicPairs`).
- `breinmine-predicaten` (`BreinPredicateMiningService`). Per canonieke
  mechanic/keyword-entiteit (het subject IS al geresolveerd) haalt getypeerde
  predicaten (`triggers_on`/`prevents`/`grants`/`requires_target` + object-token)
  uit de definitie-/kaarttekst, en legt ze als `MechanicPredicateAssertion` in
  status `candidate` vast — mét `CreatedByRunId` als 0a-provenance en de unieke
  dedupe-sleutel als hard slot. Een LLM-verdict promoveert hier NIETS: elk predicaat
  wacht op menselijke review (voedt de `HypothesisEngine` pas als `reviewed`).

Beide jobs zijn **bounded per run en idempotent** (de promotie-dedupe-sleutel resp.
de predicaat-dedupe-sleutel + de reeds-gepredikeerd-filter), en **degraderen netjes**:
rb-ai null → dat item wordt overgeslagen (Failed++), er wordt GEEN half feit
geschreven, en de job rondt af. **Geen migratie** — er wordt uitsluitend naar
bestaande tabellen (`Interaction`/`InteractionCondition`/`Assertion`/`MiningRun`/
`MechanicPredicate`) geschreven. De orkestratie + parsing + poort-koppeling zijn als
pure .NET-logica getest met een gemockte rb-ai (`BreinInteractionMiningServiceTests`,
`BreinPredicateMiningServiceTests`); de echte LLM-extractie is verifieerbaar bij de
eerste run (integratie-follow-up, §8). Het qualifier-lexicon (Window/Status) is een
seed (`InteractionQualifierLexicon`) die review/evolutie uitbreidt — een nieuwe set
mag nieuwe timing-windows/toestanden introduceren (CLAUDE.md: mee-evolueren).

---

## 7. Deploymentzicht

```mermaid
flowchart LR
    merge["merge naar main"] --> ci["v2-ci: test-gate<br/>(dotnet test · svelte-check · npm test)"]
    ci --> pub["publish: 3 GHCR-images<br/>:latest + :commit-SHA"]
    pub --> dep["v2-deploy (workflow_run)"]
    dep --> gate{"admin-job actief?"}
    gate -->|ja: wacht max 20 min| gate
    gate -->|nee| up["SSH → VM: compose pull + up<br/>IMAGE_TAG = commit-SHA"]
    up --> verify["verify: /health van rb-api + rb-web"]
```

Keten (`.github/workflows/v2-ci.yml`, `v2-deploy.yml`,
`deploy/server-setup-v2/docker-compose.yml`):

1. **Test-gate.** De `test`-job draait `dotnet test`, `svelte-check` + `npm
   test` + `npm run build` (rb-web) en `typecheck` + `test` (rb-ai). De
   publish-job hangt hieraan (`needs: test`) — geen ongeteste images.
2. **Publish met SHA-pinning.** Per service wordt een image gepusht met
   `:latest` én `:<commit-SHA>`. De publish-job serialiseert via een
   concurrency-group per service (#45, #86).
3. **Deploy via SSH.** `v2-deploy.yml` triggert op de voltooide CI
   (`workflow_run`) en pint de `head_sha` van die publish als `IMAGE_TAG`
   (geëxporteerd op het SSH-commando — de VM-`.env` blijft stateless).
   Serialisatie via concurrency-group `v2-deploy` (#82).
4. **Admin-job-gate.** Vlak vóór `compose up` pollt de deploy de admin-status op
   de VM en wacht tot ~20 min zolang er een job draait — een deploy herstart
   rb-api en zou een lopende job afbreken (#95). Fail-safe: is rb-api
   onbereikbaar (crash-loop), dan wordt de gate met een notice overgeslagen
   zodat een fix-forward kan landen.
5. **Verify.** Na `up` wacht de deploy tot rb-api (`/health`) én rb-web echt
   antwoorden (retry ~3 min), anders faalt de run zichtbaar met `ps` + logs.

Topologie op de VM (`docker-compose.yml`): centrale **Caddy** (extern netwerk)
reverse-proxyt `riftbound-v2.bo3.dev` naar rb-web; alle services hebben
memory-limits, healthchecks en log-rotatie (10m×3). **Watchtower staat
expliciet uit** op de v2-services (`com.centurylinklabs.watchtower.enable:
false`) zodat er één updatemechanisme is (#45). Datavolumes als `/mnt/data`-
binds voor Postgres, Neo4j en Ollama (het Ollama-mount-herstel is #101).

Migraties draaien bij opstart met korte retry (Program.cs) — na een VM-reboot
kan rb-api eerder starten dan Postgres klaar is.

---

## 8. Dwarsdoorsnijdende concepten

- **Kennislagen & trust** (`docs/KNOWLEDGE.md`). De kennispiramide (officieel >
  geverifieerde rulings > primer > community-claims met corroboratie/trust >
  meta) wordt in élk koppelvlak expliciet gelabeld; het antwoordformat scheidt
  "Regelbasis" van "Community-consensus" (`AskService.BasePrompt`,
  `ClaimRetrieval`).
- **Temporele precedentie** (#168) — een tweede, orthogonale precedentie-as
  naast trust: `Precedence.Compare<TDate>` (Domain, generiek over
  `DateTimeOffset`/`DateOnly`) vergelijkt twee (TrustTier, datum)-sleutels —
  TrustTier blijft primair, een ontbrekende datum sorteert als oudste (nooit
  geraden), bij gelijke tier wint de nieuwste datum. Datums komen uit
  bestaande bronnen zonder gokken: `Source.PublishedAt` uit de bron-feed-
  artikeldatum (`FeedCrawlService`, alleen het AutoApprove-pad), `Source.
  UpdatedAt` bij een échte content-wijziging (`IngestService.ScanOneAsync`,
  zelfde detectiemoment-aanname als `Change.DetectedAt`), en `Erratum.
  EffectiveFrom` afgeleid van de errata-bron (`UpdatedAt ?? PublishedAt`,
  `BanErrataSyncService`). Drie toepassingen, alle bovenop bestaande
  ordening/fusie gehangen — geen nieuw retrieval-kanaal: (1)
  `CardDetailService.ErrataForCardAsync` kiest de NU-geldende errata-tekst
  op volledige precedentie-sortering, met `DetectedAt` als laatste tie-break
  zolang `EffectiveFrom` nog niet overal bekend is; (2) `AskService` past
  `Precedence.ReorderTiedByTier` toe op de al RRF-gefuseerde citatie-lijst —
  een stabiele tie-breaker die alleen binnen een aaneengesloten reeks van
  gelijke TrustTier herschikt op recency, de fusie-/relevantierangorde zelf
  blijft ongemoeid (bewust minimale AskCoreAsync-voetafdruk); `Citation`
  draagt `PublishedAt`/`UpdatedAt` voor de "geldig sinds"/"laatst
  bijgewerkt"-weergave; (3) `AdminOverviewService.ErrataAsync` berekent per
  kaart met errata uit meerdere bronnen een supersede-kandidaat
  (`SupersededByErratumId`) — puur gelezen/berekend, geen eigen status-kolom,
  geen automatische verwijdering; het beheer toont het als signaal.
- **Wie mag de antwoord-beïnvloedende laag schrijven** (#166) — een
  `Correction` met `Status = verified` telt direct mee in `/ask` (self-learning
  override-kanaal) en `/rulings`; wie dat rechtstreeks mag zetten is
  server-authoritatief, nooit uit de request-body. `ChatRulingService`
  (in-chat-rulings vanuit `/ask`, `POST /api/ask/ruling`) en `ReviewNoteService`
  (#124, beheerder-notitie → ruling) zijn de twee schrijfpaden achter een
  échte beheerder: `AdminAuthFilter.IsAdmin` (echte `ADMIN_PASSWORD`-check,
  X-Admin-Key) geeft direct `verified` + embed; een ingelogde gebruiker
  (`RequestUserContext.User`, via `UserQuotaFilter`/X-User-Token) krijgt altijd
  `unverified` (pending) — nooit geëmbed, nooit direct zichtbaar in
  `/ask`/`/rulings`, tot een beheerder het bestaande verify-pad
  (`admin/corrections/{id}/verify`) gebruikt. Anoniem wordt afgewezen (401)
  vóórdat de service wordt aangeroepen. Een bronverwijzing (`Correction.
  SourceRef` — URL door `UrlGuard`, of vrije citatie) is verplicht: een ruling
  zonder herkomst wordt geweigerd. Sinds #177 is er een derde, niet-menselijke
  schrijfroute: `ClarificationMiningService` zet `verified` zonder
  beheerdersactie, maar alléén voor concepten uit een `TrustTier == 1`-bron
  **die bovendien de hybride poort halen** — grounded (`ClarificationGrounding`:
  citaat écht in `Document.Content`; vangt een gehallucineerd citaat) én
  anchored (`ClaimTopicMapper.Resolve`: het onderwerp bestaat als kaart/
  mechaniek/§/primer-concept; vangt een verzonnen/fout anker dat anders stil
  aan een kaartpagina zou koppelen — de MEDIUM anker-bevinding). Haalt een
  concept de poort niet, dan gaat het `unverified` + `StatusReason` de
  reviewqueue in (`/corrections`), waar de beheerder verifieert (`/verify`) of
  afwijst (`/reject` — een `rejected`-tombstone die een volgende mining-run
  respecteert i.p.v. te heropenen; `Correction.StatusReason`, migratie
  `20260714195640_ClarificationAuthorityGate`). De poort is dus dubbel: het
  bronnenregister (wie een bron trust 1 maakt — een bestaande
  beheerdersbeslissing) én grounding+anchoring per concept; geen blanket-
  uitzondering op de anti-vergiftigingsgrens. Alleen voor een bron die al
  official is én een concept dat zowel bewijs (citaat) als een echt anker
  heeft, geldt hetzelfde direct-verified-patroon als `BanErrataSyncService`.
  Sinds #184 bestaat een vierde route, nog steeds machine-gecontroleerd: de
  beheerder laat een `unverified` clarify-item op een opmerking
  (`Correction.ReviewNote`, migratie `20260714224820_CorrectionReviewNote`)
  her-evalueren (`POST /corrections/{id}/reevaluate` →
  `CorrectionReevaluationService`) — dezelfde hybride poort draait opnieuw
  voor dat ene item, met optioneel een anker-correctie uit de opmerking
  (`ReviewNoteAnchor`, bv. "mechanic:Recall") die een fout-aangeankerd of
  onherkend onderwerp overschrijft. Geen directe Status-override door de
  beheerder: de poort blijft de scheidsrechter, alleen het onderwerp/de
  invoer verandert. Een gezette `ReviewNote` reist mee door een volgende
  normale her-mine — `ClarificationMiningService.StoreAsync` laat Status/
  StatusReason dan ongemoeid in plaats van de menselijke beoordeling stil
  terug te draaien (naast de al bestaande never-downgrade- en
  rejected-tombstone-regels); een `rejected`- of al `verified`-item
  degradeert/heropent via `/reevaluate` zelf ook nooit, alleen de opmerking
  wordt dan bewaard.
- **Concept-uitgelijnd chunken vs vaste-lengte-chunken slaat de vector plat**
  (#177) — de Core Rules-PDF wordt per §-sectie geknipt (`RuleChunkPipeline`):
  elk chunk is al één concept, dus de embedding erover is scherp. Een
  HTML-artikel zonder zo'n structuur (een FAQ/clarificatie-pagina) valt terug
  op de generieke lengte-chunker in `IngestService`/`RuleChunkPipeline` —
  vaste-lengte-slabs die toevallig meerdere, ongerelateerde
  verduidelijkingen mengen. Eén embedding over zo'n slab is het gemiddelde
  van alle concepten erin: een gerichte vraag over één ervan ("Legion =
  finalize") verdunt tegen de andere en haalt het chunk niet meer boven de
  relevantiedrempel. **Fijner knippen lost dit niet op** (je krijgt dan
  willekeurige, niet-concept-uitgelijnde grenzen in plaats van te brede) — de
  juiste fix is concept-extractie: rb-ai destilleert de discrete
  verduidelijkingen zelf (`ClarificationMiner`/`ClarificationMiningService`)
  en elk item krijgt zijn eigen, gefocuste embedding, net als een §-chunk dat
  al vanzelf heeft. De vaste-lengte-chunks van het artikel blijven daarnaast
  gewoon bestaan (volledigheid, page-context) maar dragen de retrieval niet
  meer alleen.
- **Het brein & BrainRef** (`docs/BRAIN.md`). Eén tekstuele identiteit
  (`card:…`, `section:sourceId/code`, `claim:…`) over pgvector, Neo4j én
  API-contracten (`BrainRef.cs`). De brein-API (`/api/brain/*`) biedt zes
  koppelvlakken; rb-ai's agentic taak bevraagt ze als MCP-tools.
- **GraphRAG-retrieval-laag** (fase 4, #228 — `RbRules.Domain/GraphRag/*`). De
  flat fan-out van `/ask` wordt vervangen door één `RetrievalOrchestrator` die
  de pure beslislogica orkestreert: `MentionDetector`/`EntityLinker`
  (gazetteer + fuzzy + embedding-cos + **co-mention-coherentie** als graaf-truc
  om homoniemen te breken; elke keuze → een `LinkDecision`-provenance,
  hergebruikt de fase-1-`CanonicalEntity`/aliassen), de **β(q)-router**
  (`BetaRouter`: `S_final = β·S_graph + (1−β)·S_comm`, `β(q) =
  sigmoid(w1·entity-dichtheid − w2·abstractie)` — entity-dicht → graph-kanaal,
  abstract → community-kanaal), de vier retrieval-modi
  (`ModeSelector` → Local/Global/Path/Drift + directe BanLookup, per §4-tabel),
  de **trust-gating** (`TrustGate`, beslissing #229: route op "is er officiële
  dekking?" — zo niet mag een goed-onderbouwde community-claim primair zijn
  **mét badge**; authority is tie-breaker/labeler, GÉÉN multiplicatieve
  annihilator; de echo-kamer-discount zit in `Corroboration.NoisyOr`, dedup op
  idee-niveau), de trust-vector (`Trust.cs`: authority·verification·
  corroboration·recency, λ-verval per tier), de **pad-scoring**
  (`PathScoring`/`PathCitations`: k-shortest op `1/(trust·confidence)` — het
  stevigst onderbouwde pad, niet het kortste; het pad *wordt* de citatie met
  `[[card:…]]`/`[[rule:…]]`/`[[interaction:…]]`-widget-markers; `NoPath` →
  eerlijk geen interactie i.p.v. hallucineren), de **context-bundeling**
  (`ContextBundler`: trust-geordend, MMR per laag, harde token-afkap van
  onderaf — community/meta vallen eerst weg — met machine-leesbare labels
  `[OFFICIEEL]`/`[COMMUNITY trust=… corrob=…]`), en de **begrotings-poort**
  (`RetrievalGuard`, beslissing #232: HARD latency-budget → terugval naar
  Local-only; k-shortest alléén op een warme, vooraf-geprojecteerde GDS-named-
  graph). Elk antwoord produceert een immutable `AnswerTrace` (§6/#236) die
  vastlegt welke subgraaf/paden/edges/trust-gewichten-toen het antwoord droegen
  ("verantwoord dit antwoord"). **Al deze logica is PUUR en getest zonder
  Neo4j/pgvector**; de daadwerkelijke Neo4j/GDS/live-pgvector-queries lopen via
  poorten (`IGazetteerSource`, `INodeContextSimilarity`, `INodeAdjacency`,
  `IGraphRetriever` in `RetrievalContracts.cs`). Fase 4 bouwt nog géén
  hypothese-motor (fase 5).
- **Brein-GraphRAG-retrieval in `/ask`** (fase ask-retrieval, #228 —
  `BreinRetrievalService`, `BreinContextFormatter`, `BreinRetrievalGate`,
  `RbRules.Infrastructure/GraphRag/*`). De `RetrievalOrchestrator` is bedraad in
  de bestaande `AskService.AskCoreAsync` **achter een DEFAULT-UIT feature-flag**
  (`BREIN_RETRIEVAL_ENABLED`, `BreinRetrievalSettings.FromEnvironment`). Flag UIT
  (de default, en de meeste constructors geven de service niet eens mee) ⇒ `/ask`
  draait EXACT zoals voorheen: géén brein-call, géén extra latency, géén
  gedragswijziging — de poort schakelt de hele laag uit vóórdat er ook maar één
  adapter geraakt wordt. Flag AAN ⇒ `BreinRetrievalService.EnrichAsync` draait de
  orchestrator (naast de bestaande lees-kanalen, zodat het overlapt i.p.v.
  serieel latency toe te voegen) en `BreinContextFormatter` voegt één
  trust-gelabeld `BREIN-CONTEXT`-blok (subgraaf-chunks + `[cit:N]`-pad-
  onderbouwing + gating-beslissing + evt. terugval-reden) ná de bestaande
  kennispiramide-blokken aan de prompt toe; de retrieval produceert een
  `AnswerTrace` die AskService best-effort in `answer_trace(_support)` persisteert
  (zichtbaar in de Brein-verkenner, #236). **Nette degradatie is hard**: elke
  brein-fout (Neo4j/pgvector weg, timeout) → `EnrichAsync` slikt hem, logt en geeft
  null terug → `/ask` valt terug op de bestaande flow, NOOIT een 500; alleen een
  echte client-abort bubbelt door. Een benchmarkrun (#158) blijft eveneens buiten
  schot (isolatie). De vier poort-adapters (`PostgresGazetteerSource`,
  `PgVectorNodeSimilarity`, `Neo4jNodeAdjacency`, `BreinGraphRetriever`) draaien
  tegen de live Neo4j + pgvector en zijn een **integratie-follow-up** (niet in CI —
  verifieerbaar bij de eerste run met flag aan); elke adapter degradeert bij uitval
  naar leeg/neutraal. De wiring, de flag-gating, de terugval en de
  AnswerTrace-opbouw zijn PUUR en getest (`BreinRetrievalTests`,
  `AskServiceBreinRetrievalTests`, mock-adapters).
- **Hypothese-motor & trust-vector** (fase 5, #229 — `RbRules.Domain/*`,
  `RbRules.Domain/GraphRag/TrustConflict.cs`). De kandidaatgeneratie voor
  interacties gaat van LEXICALE overlap (fase 3) naar GETYPEERD property-
  antagonisme. Elke mechanic/keyword (`CanonicalEntity`) draagt gemined+gereviewde
  `MechanicPredicateAssertion`'s (`triggers_on`/`prevents`/`grants`/
  `requires_target`; extractie-vorm `MechanicPredicateExtraction`, tool-forced als
  fase 2). De `HypothesisEngine` indexeert die predicaten geïnverteerd op
  (predicaat, token) en past alléén complementair-vervullende paren
  (`triggers_on(X,exhaust) ∧ prevents(Y,exhaust) ⇒ nonbo(X,Y)`) — O(n·k) i.p.v.
  blind N², met `deck_domain_compatible` als prune. Elke `InteractionHypothesis`
  draagt haar deterministische bewijs (regel-id + antecedent-tuples) en gaat naar
  GERICHTE LLM-verificatie; `HypothesisPromotion` koppelt haar aan de ONVERANDERDE
  fase-2-poort, zodat een positief verdict ZONDER onafhankelijke lexicale/consensus-
  steun in `model_hypothesized_unruled` (cold-start) landt — nooit een stille
  promotie op enkel structuur+LLM (rode draad #236). `HypothesisYield` maakt de
  precisie-/kostenwinst MEETBAAR uit de data (blinde N²-baseline vs. werkelijk
  kandidaataantal, precisie tegen een gouden set — geen verzonnen vaste factor,
  kritiek B7). Een BEGRENSD residueel embedding-cosine-kanaal
  (`ResidualInteractionChannel`, laag-prioriteit, cosine-vloer + top-K + hard
  budgetplafond) pikt interacties zónder structurele signatuur op zonder terug te
  vallen in de N²-scan. De trust-vector wordt afgerond: `ProvenanceCluster` leidt de
  idee-niveau onafhankelijkheids-sleutel (thread ≻ auteur ≻ site) af die
  `Corroboration.NoisyOr` compleet maakt (echo-kamer-dedup), en
  `TrustConflictResolver` beslecht conflicten CONTEXT-afhankelijk (cross-tier →
  authority-veto; within-tier-temporeel → recentste-gezaghebbende via SUPERSEDES;
  detectie-botsing → vroegste-detectie via ALIAS_OF — bewust de #168/#206-precedentie
  met de per-context juiste tie-break-richting), elk met een expliciete
  `TrustDecision`. **Al deze logica is PUUR en getest**; de live rb-ai-mining, de
  Neo4j-projectie en de persistentie van de Decision-knopen zijn een bewuste
  integratie-follow-up. Fase 5 bouwt nog géén governance/eval (fase 6/7).
- **Governance, levenscyclus & schema-evolutie per set** (fase 6, #230 —
  `RbRules.Domain/Ontology/OntologyVersion.cs`, `SchemaProposal.cs`,
  `RbRules.Domain/KnowledgeLifecycle.cs`, `ErrataLifecycle.cs`,
  `ModelUpgradeInvalidation.cs`; `RbRules.Infrastructure/OntologyGovernanceService.cs`,
  `KnowledgeLifecycleService.cs`). De ontologie is een first-class, **semver**-
  geversioneerd artefact (`SemVer`, bump-regels: patch = nieuwe instanties,
  minor = additief relatietype/subklasse, major = klasse-split/disjointness-
  wijziging). `OntologySnapshot` reduceert `OntologySchema` (fase 0b, de ENIGE
  schema-bron) tot een ordening-stabiele **structuur-vingerafdruk**; de
  **has-pending-ontology-poort** (`OntologyChangeGate`) toetst die code-vingerafdruk
  tegen de checked-in `OntologyBaseline` — puur, €0, geschikt als CI-gate, exact
  spiegelbeeld van `has-pending-model-changes`. Een nieuwe set die een onbekend
  keyword/relatietype meebrengt breekt niets: mining zet het als `:Proposed` in de
  **staging-namespace** (`StagingNamespace`, retrieval-zichtbaar, lage weging, kan
  niets harden). Promotie vereist deterministisch bewijs (`SchemaProposalGate`: ≥N
  officiële kaarten ÉN een verankerende Core-Rules/glossary-sectie) → reviewqueue →
  versioned migratie-Activity (`OntologyVersionRecord`); een LLM-vermoeden hardt
  nooit alléén een schema-wijziging, en een nieuw gekwalificeerd relatie-voorstel
  wordt default gereïficeerd (`RelationProposalPolicy`) — een eigen edge-type
  alleen bij hoge frequentie + retrieval-waarde, via review. De **kennis-
  levenscyclus** krijgt één canoniek toestand-vocabulaire (`LifecycleState`:
  active/stale/deprecated/superseded/tombstoned/restored) met bewaakte transities
  (nooit hard-delete, heropenen alléén via een expliciete `Restored`-stap), een
  tier-bewuste **staleness-evaluator** (`StalenessEvaluator`, λ per tier: officieel
  vervalt niet op leeftijd, meta agressief; triggers op leeftijd/model/embedding-
  upgrade/corroboratie-daling/errata/negatieve-ask-signalen), en een
  geconsolideerd, herstelbaar **`LifecycleEvent`-log** dat de verspreide fase-1/2-
  tombstones overkoepelt (`KnowledgeLifecycleService`). De **errata-mid-set-flow**
  (`ErrataLifecycle`) zet een geërraterde ruling via SUPERSEDES op `superseded`
  (blijft bestaan voor dossier-historie) en invalideert de afhankelijke feiten/
  eval-cases naar `stale` (koppeling aan het eval-scaffold #231/#235:
  forbidden_claim-verval). De **gerichte model-upgrade-invalidatie MÉT kostengate**
  (`ModelUpgradeInvalidation`, BESLISSING #232) selecteert bij een model-bump
  uitsluitend de puur-LLM-ongesteunde feiten (geen menselijke goedkeuring, geen
  onafhankelijke corroboratie — precies de §6-Cypher) en her-mint ze **incrementeel
  met een budgetplafond** op het abonnement-token; nooit een blinde N²-re-mine.
  Bitemporaliteit blijft **licht** (kritiek B8): valid-time + transaction-time zitten
  al op `Assertion`, niet overal. **Al deze logica is PUUR en getest** (`Ontology
  GovernanceTests`, `KnowledgeLifecycleTests`, `GovernanceServiceTests`; de
  service-schil op InMemory-DbContext); de live Neo4j-projectie van de
  `:Proposed`/`:Superseded`/`:Tombstone`-labels, de daadwerkelijke code-migratie bij
  promotie en het her-minen van de schaduw-mine-batch zijn bewuste integratie-
  follow-ups. Fase 6 bouwt nog géén eval-industrialisatie; die volgt in fase 7
  (hieronder — de errata-mid-set-flow koppelt er via `ErrataEvalExpiry` op aan).
- **Degradatiepaden** — AI-uitval is een verwacht pad: `RbAiClient` geeft null,
  de aanroeper degradeert (`docs/CONVENTIONS.md`, `AskService`, `RbAiClient`).
  Neo4j-uitval maakt `neighbors`/`path` een nette Problem-response terwijl de
  Postgres-koppelvlakken blijven werken (`BrainEndpoints`).
- **EF-vertaalbaarheid** — alleen bewezen vertaalbare LINQ; naam-matching en
  lexicale filters in SQL, afstand-caps bewust in-memory (`AskService`
  `CardsNamedIn`/`CardContextAsync`, `docs/CONVENTIONS.md`).
- **Migratie-discipline** — migraties zijn heilig: elk schemaverschil via
  `dotnet ef migrations add`, nooit handmatig muteren; een migratie wordt tot de
  echte delta gestript (de les van PR #91; zie ook `DesignTimeFactory`,
  `Migrations/`).
- **Prompts zijn code** — systeem-prompts staan als const bij de service met
  expliciete structuur-eisen; server-side addenda (`RESEARCH_CONTRACT`,
  `AGENT_ADDENDUM`) zijn niet door de aanroeper te omzeilen
  (`AskService.BasePrompt`, `QuestionRouter.StructureFor`, `rb-ai/src/ai.ts`).
- **Afgeleide kennis in de brontaal (Engels)** (#187) — een dwarsdoorsnijdend
  onderscheid tussen twee talen in dezelfde codebase: UI en `/ask`-antwoorden
  blijven Nederlands (`AskService.BasePrompt`), maar wat het LLM zelf
  extraheert/synthetiseert — `ClaimMiner.ExtractionSystemPrompt`,
  `PrimerService`'s systeemprompt, `RelationMiner`'s `explanation`-instructie
  en rb-ai's `AGENT_ADDENDUM` (dezelfde relatie-`explanation`, via de
  agentic-terugkoppeling, #120), de relatie-kind-labels
  (`RelationMiner.SeedKinds` — Engels, met de vier oude NL-labels als
  uitstervende legacy zodat een Relation-rij die de deploy→wipe-window nog
  draagt geldig blijft; de wipe raakt de `RelationKind`-reviewstate niet), en
  de claim-toets-redenering (`OfficialCheck`/`ClaimJudge` — de `reason` die als
  `Claim.StatusReason` de weerleg-/misvattingstekst in `/ask` voedt, #125) —
  levert voortaan Engels, dicht bij de officiële bewoording: geen vertaalstap
  tussen brontekst en opgeslagen
  kennis, dus geen vertaalverlies of -drift, en de embeddings van bron en
  afgeleide laag liggen in dezelfde taalruimte (bge-m3 is weliswaar
  meertalig, maar eentalige consistentie is scherper dan cross-lingual
  matching). `ClarificationMiner`/`ClarificationMiningService` volgen
  hetzelfde patroon (#185, parallel spoor) — bewust niet in dit issue
  aangeraakt. Een bestaande Nederlandse afgeleide laag wordt niet in-place
  vertaald (een tweede LLM-stap over al opgeslagen tekst, met eigen
  hallucinatie-risico) maar via `KnowledgeRegenerationService.WipeAsync`
  schoongeveegd en met de nieuwe prompts herbouwd: één transactie verwijdert
  `claim` (cascadeert naar `claim_source`), ALLE `correction`-rijen
  (issue-comment 2026-07-14: ook de weinige door mensen ingevoerde/
  geverifieerde — Nederlands en zeldzaam, expliciet opgegeven voor een schone
  start), `knowledge_doc` met kind="primer" en `relation`, en reset
  `Document.ClaimsMinedAt`/`ClarifiedAt` (anders blijft de laag na de wipe
  permanent leeg — die markers overleven de wipe, want `Document` zelf is
  bron, geen afgeleide laag; `KnowledgeDoc.RelationsMinedAt` heeft geen reset
  nodig, de primer-rijen zelf zijn weg). Nooit aangeraakt: `source`/
  `document`/`rule_chunk`, `card`, `errata`, `ban_entry`, `deck`/`deck_card`
  (bron of feitelijke data, al Engels) — bewezen met een test die exact die
  tabellen seedt en na de wipe ongewijzigd telt. De job
  (`JobCatalog`: `regenerateknowledge`) zit bewust NIET in `RunAllAsync`
  ("Alles bijwerken" bevat primer/claims/clarify/relations toch al niet) en
  chaint bewust GEEN automatische her-generatie — een expliciete,
  destructieve beheerdersactie (eigen gewaarschuwd paneel met confirm-stap in
  rb-web) die de coördinator zelf ná de deploy uitvoert, waarna de
  bestaande primer/claims/clarify/relations-jobs los getriggerd worden.
- **Sanitize vóór `{@html}`** — tekst wordt ge-escaped vóór markdown-parse/
  icoon-injectie; link-URL's zijn gewhitelist (`rb-web/src/lib/markdown.ts`,
  `rbtokens.ts`, `docs/CONVENTIONS.md`).
- **Observability** — elke achtergrond-actie logt naar `run_log`; `AskMetric`
  meet echte antwoordduur, `AskTrace` legt per vraag de meegedane lagen +
  brein-stappen vast; JobRunner toont live voortgang (`docs/CONVENTIONS.md`,
  `AdminEndpoints`).
- **Rate-limiting & quota** (#42) — policies `llm` (per client-IP of
  sessietoken), `auth`, `webauthn` en `prewarm` in `Program.cs`;
  per-account-dagquota via `UserQuotaFilter`. Het dure agent-pad heeft een
  eigen rem (#153): zelf geforceerde Grondig-vragen tellen tegen
  `DailyAgenticQuota` (default 5/dag, per account instelbaar in het beheer);
  gate-escalaties tellen niet mee. Het kostenoverzicht splitst het
  agentic-pad op wie escaleerde ("agentic (gate)" vs "agentic (gebruiker)",
  `AdminOverviewService.UsersAsync`).
- **Capaciteit & latency van de AI-keten** (#154/#155) — de beschermings-
  stapel is gelaagd: per-IP/token-rate-limit (`llm`) → dagquota per account
  (`UserQuotaFilter`) → globale sessie-cap in rb-ai (`AI_MAX_CONCURRENCY`,
  default 3; agentic weegt 2; wachtrij max 30 s, daarna 429 → bestaand
  degradatiepad in `RbAiClient`) → de VM zelf (8 GB; een idle SDK-subprocess
  kost orde-grootte honderden MB's RSS — exacte cijfers volgen uit productie-
  metingen, niet uit deze PR). Latency: de /ask-paginalaad stuurt een
  fire-and-forget prewarm-signaal (rb-web load → `/api/ask/prewarm` →
  rb-ai `/prewarm`) waarop de warme pool één cheap-sessie voorboot; de
  query-rewrite-call (statisch systeemprompt) claimt die en haalt zo de
  SDK-subprocess-boot van het kritieke pad — lokaal geverifieerd (zonder
  geldige token) dat `query()` met streaming input het CLI-subprocess start
  en idle laat wachten totdat de eerste user-message binnenkomt, zonder dat
  er vóór dat moment een model-call plaatsvindt; de exacte boot-duur (orde
  seconden) en of idle echt 0 tokens kost, bevestigt zich pas met de
  fase-instrumentatie van #152 (aiMs) op de productie-VM — dit issue bewijst
  zich met cijfers of gaat terug (issue #154). De sessie-opties liggen bij de
  SDK vast op `query()`-moment, dus warm werkt alleen bij byte-gelijke opties
  — de antwoord-call (systeemprompt per vraagtype) blijft koud. Warm bestaat
  alleen rond activiteit (TTL 10 min, signaal-gedreven).
- **Privacy-concept: IP-hashing i.p.v. rauw IP** (#157) — waar rb-api "zelfde
  IP" moet herkennen (anonieme ask-geschiedenis) bewaart het nooit het
  client-IP zelf: `UserQuotaFilter` stempelt op élk request (ook zonder
  sessietoken) een HMAC-SHA256-hash (`IpHashing.Hash`, secret uit
  `ASK_IP_HASH_SECRET`) op `RequestUserContext.IpHash`, met exact hetzelfde
  IP-patroon als de rate-limiter (`X-Client-Ip`-header ?? `RemoteIpAddress`).
  `AskService` stempelt die hash op `AskTrace.IpHash` naast `UserId`;
  `AskHistoryService` leest de eigen historie op `user_id` (ingelogd) of
  `ip_hash` (anoniem) — nooit op een aanroeper-gestuurde id. Ontbreekt het
  secret, dan blijft `IpHash` overal null: stille degradatie, nooit een
  crash.
- **Best-effort achtergrondwerk** — `JobCatalog` registreert jobs als één
  switch-vrije catalogus; `RunAllAsync` ("Alles bijwerken") draait elke stap
  best-effort in volgorde.
- **Benchmark voedt de kennisbank niet** (#158) — de judge-benchmark draait
  exact dezelfde retrieval/prompt/agentic-gate als een normale vraag, via
  `AskService.AskOptions { Benchmark = true }`: één vlag door de
  ask-aanroep die élk leer-/meetneveneffect onderdrukt — geen
  `ask_trace`/`ask_metric`-rij (dus buiten de duurstatistiek en het
  kennis-gaten-rapport, die op die tabellen leunen) en geen agentic-
  relatie-terugkoppeling (#120). Claims en geverifieerde rulings worden door
  `AskCoreAsync` sowieso alleen gelézen, nooit geschreven, dus die blijven
  toch al buiten schot. `BenchmarkService` boekt zijn eigen
  `benchmark_run`/`benchmark_result`-rijen, strikt gescheiden van de
  kennisbank-tabellen; bewezen met een servicetest die 0 rijen in
  ask_trace/ask_metric/relations verwacht (`AskServiceBenchmarkIsolationTests`).
- **Model-sweep-override reist mee, isolatie blijft hard** (#174) —
  `AskOptions.Model` (alleen zinvol samen met `Benchmark = true`) reist via
  `RbAiClient` als optioneel `model`-veld in de `/ask`-payload naar rb-ai, dat
  het als expliciete modeloverride aan de SDK-`query()` meegeeft
  (`buildQueryOptions({..., model})` in `ai.ts`) — zonder override blijft
  rb-ai's eigen `MODEL[task]` (cheap/hard/research/agentic) gelden. De
  override slaat de warme-sessiepool (#154) bewust over: die pool is altijd
  op `MODEL.cheap` voorverwarmd, dus een claim zou de override stilzwijgend
  negeren. Een onbekend model crasht niets: AskService/RbAiClient degraderen
  een rb-ai-fout al naar `RbAiClient.UnavailableAnswer` zonder exception —
  die vraag komt gewoon als onscoorbaar resultaat de sweep in, de rest draait
  door. De isolatietest (`AskServiceBenchmarkIsolationTests`) blijft ongewijzigd
  van toepassing: `Model` verandert niets aan welke tabellen wel/niet
  geschreven worden, alleen welk model het antwoord genereert.
- **Eval-industrialisatie & meta** (fase 7, #231, brein-epic #223 — LAATSTE
  fase) — bouwt op het eval-scaffold voort tot de volledige meet-industrie.
  Het **scaffold** (nog altijd de kern): `EvalCase` (de meeteenheid — vraag +
  `EvalQueryType` (Factoid/Inference/Comparison/Temporal) + `GoldSupport`
  (recall-noemer) + `GoldConditionSupport` (de conditie-dragende deelverzameling
  voor path-recall) + `ExpectedCitations` + `ForbiddenClaims` + levenscyclus),
  `EvalRunResult` (geabstraheerde run-uitkomst: opgehaalde/geciteerde/
  geproduceerde ids — géén graaf-koppeling), `EvalScoringService` (pure
  Relevancy/Recall/F1/CitationPrecision/ContradictionRecall) en
  `EvalGateEvaluator` (de deterministische **Ring-A**-poort: citation-validity
  100% + nul actieve forbidden claims). Twee Kritiek-mitigaties zijn ingebakken:
  **cold-start-shadow** (een `EvalStatus.Shadow`-case scoort en wordt
  gerapporteerd maar blokkeert de gate nooit — een half-gereviewde nieuwe set
  breekt de CI van `main` niet, B4) en **errata-invalidatie** op twee niveaus
  (case-niveau `SupersededByErratum`/`ValidUntil` → overslaan; claim-niveau
  `ForbiddenClaim.SupersededByErratum` → een door een erratum waar-geworden
  claim telt niet meer als contradictie, C). De voorbeeld-gouden-set staat als
  seed in `rb-api/RbRules.Tests/Fixtures/poracle-eval-seed.json` (via `EvalSeed.Parse`); het echte
  corpus komt in Postgres `eval_case` met rb-ai-kandidaten uit set/errata-diffs.
  **Fase 7 legt daar bovenop** (alles PURE Domain, `RbRules.Domain/Eval*.cs`;
  KRITIEK — live-graaf/rb-ai/pgvector niet in CI):
  - **Ring B/C-scoring** (`RetrievalQualityScoring`, `EvalRing`/`EvalMetricNames`,
    `EvalHarness`): naast de kale set-recall meet Ring B **path-recall op
    gekwalificeerde interacties** (structuurverlies, faalmodus 3 — een pad dat de
    `window=showdown`-conditie mist scoort < 1.0), **citation-support/
    groundedness** (geciteerd ∈ opgehaalde subgraaf), en **answer-faithfulness**
    via geabstraheerde judge-verdicten (`JudgedClaim`, SUPPORTED/CONTRADICTED/
    NOT_IN_CONTEXT) **mét deterministisch vangnet** (een SUPPORTED claim die naar
    ongehaalde support citeert wint niet — de structurele check verslaat de
    judge). Ring C voegt **answer-consistency onder parafrase** toe (paarsgewijze
    Jaccard over de claim-sets). `EvalHarness` bindt de scorers per ring en
    vertaalt case-runs naar `ClassifiedSample`s. De judge zelf is een integratie-
    follow-up (rb-ai niet in CI); de scoring is €0 en volledig getest.
  - **Baseline-diff-per-klasse-gate** (`EvalBaseline`, `BaselineDiffGate`):
    vergelijkt het huidige per-(question_class × metric)-gemiddelde tegen een
    vastgelegde baseline en blokkeert bij een regressie (`mean < baselineMean −
    kσ`, default 2σ) op ENIGE meetellende klasse — sluipende degradatie die het
    gemiddelde verbergt wordt zo gevangen. Shadow-samples worden apart
    geaggregeerd en gerapporteerd maar gaten nooit; een klasse zonder baseline
    kan niet gaten (cold-start op metriek-niveau); een deterministische metriek
    (σ 0) mag niet zakken (de harde citation-validity-gate als diff uitgedrukt).
  - **Auto-gegenereerde eval-cases uit set-diffs** (`SetDiffCaseGenerator`):
    nieuwe kaart → Factoid, nieuw keyword/mechanic → Inference, erratum →
    Temporal mét de oude bewoording als `ForbiddenClaim` — ALTIJD in
    `EvalStatus.Shadow` (cold-start), deterministische ids (idempotente
    reviewqueue). De koppeling fase 6 → fase 7 zit in `ErrataEvalExpiry`: een
    fase-6 `ErrataLifecycle.Plan` laat matchende forbidden_claims (claim-niveau)
    en hele cases (case-niveau) vervallen — forbidden_claim-verval.
  - **Ops-observability** (`ObservabilityReport`/`ObservabilityRollups`,
    `CommunityStability`, inzicht #236): queryable admin-tegel-rollups —
    mining-precisie per (soort × model) uit `MiningRun`, kosten/latency per
    retrieval-modus uit fase-4-`AnswerTrace`, community-modularity/stabiliteit
    (label-onafhankelijk, Leiden hernummert), plus hergebruik van de bestaande
    fase-1-snapshots (`GraphDrift`, `CanonicalDriftSnapshot`) en de fase-5-
    `HypothesisYield` — geen duplicatie.
  - **Deck-integratie CO_OCCURS** (`DeckCoOccurrence`, #15): kruis-valideert
    structureel voorspelde combo-paden (fase 5) met de echte Piltover-meta —
    per paar co-decks/support/lift + een corroboratie-rate (precisie van de
    structuurvoorspelling tegen de meta) als meetbaar signaal.
  - **Persistentie** (`EvalBaselineRecord`/`EvalRunRecord`, migratie
    `EvalIndustrialization231`): `eval_baseline` (één rij per ring × klasse ×
    metric, uniek geïndexeerd — de gate diff't tegen precies één cel) en
    `eval_run` (rollup-samenvatting per gate-run, voor "sluipende degradatie over
    runs"). De baseline-diff-gate zelf blijft puur; deze tabellen dragen alleen
    de runtime-baseline en run-historie. De live retrieval-runs, de LLM-judge en
    de graaf-metrieken (Leiden-modularity, echte latency/token-metering) zijn
    bewuste integratie-follow-ups; fase 7 levert de pure, geteste meet-kern.

---

## 9. Architectuurbeslissingen (ADR's)

Kort, in ADR-stijl. De issue-historie is de belangrijkste bron.

### ADR-1 — AI via een interne sidecar op het abonnement
**Context.** rb-api mag geen per-token API-key dragen (`docs/AI_AUTH.md`).
**Besluit.** Een aparte, alleen-intern bereikbare rb-ai-container draait de
Claude Agent SDK op `CLAUDE_CODE_OAUTH_TOKEN`; rb-api praat er via HTTP mee.
**Gevolg.** LLM-uitval = null-degradatie in `RbAiClient`; AI nooit publiek
exposed. `rb-ai/src/server.ts`, compose.

### ADR-2 — Postgres source of truth, Neo4j als herbouwbare projectie
**Context.** Pad-/buurvragen worden in SQL onhandig; er was tot #104 geen
lees-consument van Neo4j (`docs/BRAIN.md` §1.3).
**Besluit.** Postgres blijft de waarheid; Neo4j en alle brein-afgeleiden zijn
projecties, altijd volledig herbouwbaar in één transactie.
**Gevolg.** Drift wordt gemeten (kennis-gaten-rapport), niet vermeden;
Neo4j-uitval is degradeerbaar. `GraphSyncService`, `KnowledgeGapsService`.

### ADR-3 — Strikte lagen Api → Infrastructure → Domain
**Besluit.** Domain is puur en unit-testbaar; Infrastructure doet I/O; Api is
alleen compositie + dunne endpoints. **Gevolg.** Nieuwe vraagtypes/jobs/bronnen
zijn uitbreidpunten (switch/lijst), geen herschrijvingen. `docs/CONVENTIONS.md`.

### ADR-4 — Deploys pinnen op commit-SHA (#86, #45)
**Context.** Twee parallelle publishes kunnen `:latest` in de verkeerde
volgorde zetten (PR #83 miste daardoor tijdelijk productie).
**Besluit.** Publish pusht `:latest` én `:<SHA>`; de deploy pint de `head_sha`
van zijn triggerende publish. **Gevolg.** Deploys hangen niet meer van
`:latest`-timing af. `v2-ci.yml`, `v2-deploy.yml`.

### ADR-5 — Deploys serialiseren en verifiëren (#82, #45)
**Besluit.** Concurrency-group `v2-deploy` (cancel-in-progress: false) +
verplichte healthcheck-verify na `compose up`. **Gevolg.** Geen racende runs met
containernaam-conflicten; een deploy die niets verifieert bestaat niet.

### ADR-6 — Admin-job-gate vóór `compose up` (#95, #45)
**Besluit.** De deploy pollt de admin-status en wacht tot ~20 min op een lopende
job. **Gevolg.** Een deploy breekt nooit stilletjes een lopende admin-job af;
fail-safe overslaan bij onbereikbare rb-api zodat fix-forward kan landen.

### ADR-7 — Eén updatemechanisme, Watchtower uit (#45)
**Besluit.** Push-to-deploy is leidend; Watchtower-labels op de v2-services
staan op `false`. **Gevolg.** `pull`/`up` racet niet met een Watchtower-update.
Restrisico: de Watchtower-daemon draait op de VM nog wél (zie §11).

### ADR-8 — Migratie-discipline: strippen tot de echte delta (PR #91)
**Context.** Een te brede/handmatig aangepaste migratie brak een productie-
deploy. **Besluit.** Migraties via `dotnet ef migrations add`, gestript tot de
werkelijke schemadelta; snapshot nooit hand-patchen. **Gevolg.** Voorspelbare
opstart-migraties. `docs/CONVENTIONS.md`, `Migrations/`.

### ADR-9 — Data-volumes op de datadisk expliciet gemount (#101, #82)
**Context.** Een compose-recreate wiste het gepullde bge-m3-model doordat de
Ollama-bind-mount per ongeluk was weggevallen; elke embedding faalde.
**Besluit.** `/mnt/data/…`-binds voor Postgres, Neo4j én Ollama expliciet in
compose. **Gevolg.** Recreates houden data en model. `docker-compose.yml`.

### ADR-10 — Agentic ask achter een gate met vangnet (#106/#107)
**Besluit.** Single-pass is de norm; agentic escaleert alleen bij een
kwalificerende vraag achter flag `ASK_AGENTIC`, met een klassieke single-pass
als vangnet en meting per pad. **Gevolg.** Kosten/latency onder controle,
nooit een slechter antwoord. `AgenticGate`, `AskService`, `rb-ai/src/ai.ts`.

### ADR-11 — Provenance als schema-invariant, dubbel bewaakt (#233)
**Context.** Afgeleide feiten (relaties, interacties, embeddings) droegen geen
herkomst — faalmodus #4 uit de brein-architectuur. **Besluit.** Elk afgeleid
feit hangt aan een gereïficeerde `Assertion` met verplichte `WAS_GENERATED_BY`
(→`MiningRun`) én `DERIVED_FROM`; de shape wordt dubbel afgedwongen — de pure
`AssertionProvenanceGuard` plus een `RbRulesDbContext.SaveChanges`-poort die 'm
altijd draait. Postgres blijft de bron van waarheid (ADR-2); de Neo4j-projectie
is idempotent herbouwbaar en een relatie-existentie-constraint (Enterprise-only)
is bewust niet de garantie. Wat "nieuw werk" runtime afdwingt is de
schrijfpoort: elke `Assertion` zónder complete provenance faalt hard op
`SaveChanges`. Daarnaast is er een deterministische Ring-A-audit (€0, geen LLM,
`ProvenanceAuditService.AuditAsync`) die afgeleide feiten zónder Assertion en
embeddings zonder herkomst telt, gesplitst in "nieuw" (moet 0 zijn) en "legacy
backfill"; die audit is een aparte, herhaalbare uitspraak — nog niet aan een
job/CI-stap gekoppeld (dat is latere fase-bedrading). **Gevolg.** Een `Assertion`
zonder herkomst is onmogelijk; een afgeleid feit dat helemáál geen Assertion
krijgt is niet door de schrijfpoort te vangen, maar wordt door de Ring-A-audit
zichtbaar gemaakt i.p.v. stil gedoogd. Legacy-feiten worden geïnventariseerd
voor backfill. `Provenance.cs`, `ProvenanceAuditService`, `GraphSyncService`,
`GraphSchema`.

### ADR-12 — Gekwalificeerde relaties altijd gereïficeerd, promotie via een deterministische poort (#226)
**Context.** Gekwalificeerde relaties (COUNTERS/MODIFIES/GRANTS/REQUIRES) dragen
condities (window/status/cost); als kale edge of vrije-tekst-`Explanation` gaat
die structuur verloren (faalmodus #3). **Besluit.** Elk zo'n feit leeft als
gereïficeerde `Interaction` (Postgres = SoT) met rollen agent/patient en losse
`Condition`-knopen; een kale gekwalificeerde edge wordt door
`OntologyValidationService` geweigerd, de gereïficeerde vorm afgedwongen. Promotie
loopt door een **deterministische poort** (`InteractionPromotionGate`): nooit
LLM-alleen — `schema ∧ (lexicaal ∨ consensus≥N) ∧ verdict`. Twee harde regels:
(a) een levende `RejectionTombstone` blokkeert stil-heropenen, herstel is een
expliciete beheerdersactie; (b) een emergente card×card-hypothese zonder steun
wordt NIET verworpen maar getierd als `model_hypothesized_unruled` (cold-start,
kritiek Risico 1), nooit stil weggegooid. De `RELATES_TO`-qualifier-cache is een
gedenormaliseerde projectie, nooit de bron. **Gevolg.** "Deflect countert Assault
alleen in een showdown" is queryable i.p.v. begraven in proza; geen promotie of
verwerping zonder deterministisch bewijs + memo + herstelpad (rode draad #236).
De live rb-ai-extractie (tool-forced `emit_interactions`) is inmiddels bedraad in de
handmatige job `breinmine-interacties` (`BreinInteractionMiningService`, §6.6): rb-ai
levert de kandidaten, entity-resolutie draait vóór creatie, en de promotie-service
persisteert atomair — de echte LLM-extractie blijft integratie-verifieerbaar bij de
eerste run. `ReifiedInteractions.cs`, `InteractionPromotionGate.cs`,
`InteractionProjection.cs`, `InteractionExtraction.cs`, `InteractionPromotionService`,
`BreinInteractionMiningService`.

### ADR-13 — Redeneer-laag: één engine, Neo4j-native; afgeleide edges nooit bron (#227)

**Context.** De graaf moest *leesbaar* worden — inheritance, property-chains en
tegenspraken afleiden i.p.v. alles apart minen. De lenzen botsten op de techniek
(stratified Datalog vs. Cypher vs. OWL-RL). **Besluit.** **Één engine, Neo4j-native:**
Cypher-MERGE voor monotone inferentie, bounded `WHERE NOT EXISTS` voor contradictie —
**géén apart C#-Datalog** en **geen OWL-runtime in de hot-path** (onze edges zijn
gekwalificeerd; OWL zou reïficatie/blank-nodes afdwingen → structuurverlies, en
.NET-OWL-tooling is dun). De inferentie-regels worden DETERMINISTISCH uit de ontologie
gegenereerd (`InferenceRuleRegistry` uit `OntologySchema` — de ÉNE schema-bron): geen
met-de-hand-lijst die uit sync raakt. Drie harde regels: (a) **afgeleide edges zijn
nooit bron** — ze dragen `derived=true`+`derivedByRule`+run-provenance en worden bij
elke run gewist en opnieuw gematerialiseerd, nooit als Postgres-feit gepersisteerd
(SoT = de basisfeiten); (b) een reasoner-regel draagt **nooit een LLM-oordeel** — puur
deterministisch (`model='deterministic'`); (c) een gedetecteerde tegenspraak levert
**geen edge maar een `ReasoningConflict`-rij** (Postgres = SoT), gerouteerd naar
misvattingen-kanaal/reviewqueue/escalatie en idempotent op een dedupe-sleutel — een
beslissing levert nooit onzichtbare state (rode draad #236). OWL2-RL blijft als
**optionele nachtaudit-skeleton** (`OntologyConsistencyAudit`) die de afgedwongen
schema-bron zelf toetst. **Gevolg.** "Welke regels gelden voor deze Deflect-kaart?"
wordt één graaf-hop; een `:Unit:Spell`-knoop (kaart-sync-schade à la #150) valt op als
disjointness-tegenspraak. Neo4j zit niet in CI — de live-Cypher-executie is
integratie-follow-up (best-effort, degradeerbaar), de pure regel-/patroon-generatie en
conflict-vertaling zijn getest. EF-migratie `Reasoner227`.
`RbRules.Domain/Reasoning/*` (`InferenceRuleRegistry`, `DerivedEdgeProvenance`,
`ContradictionDetector`, `ReasoningConflict`, `OntologyConsistencyAudit`),
`ReasoningService`.

### ADR-14 — Ontologie als semver-artefact met has-pending-gate; kostengegate model-upgrade (#230, #232)

**Context.** De ontologie moet met elke set meegroeien zonder dat een nieuwe
set-mining stil het schema verbouwt (faalmodus 2/3) of een model-bump een blinde,
peperdure N²-re-mine ontketent. **Besluit.** De ontologie is een **semver**-
geversioneerd first-class artefact. `OntologySchema` (fase 0b, de ENIGE schema-bron)
wordt tot een ordening-stabiele **structuur-vingerafdruk** gereduceerd; de
**has-pending-ontology-poort** (`OntologyChangeGate`) toetst die puur tegen een
checked-in `OntologyBaseline` — het exacte spiegelbeeld van EF's
`has-pending-model-changes`, geschikt als CI-gate zonder DB/Neo4j/LLM. Een
schema-wijziging is een **event**, geen instantie: een onbekend keyword/relatietype
uit een nieuwe set landt eerst als `:Proposed` in de **staging-namespace** (lage
weging, kan niets harden) en promoveert alléén via deterministisch bewijs
(`SchemaProposalGate`: ≥N officiële kaarten ÉN een verankerende sectie) → reviewqueue
→ versioned migratie — nooit op een LLM-vermoeden alleen (rode draad #236). Bump-regels:
patch = instanties, minor = additief relatietype/subklasse, major = klasse-split/
disjointness-wijziging. De **kennis-levenscyclus** consolideert de verspreide fase-1/2-
tombstones tot één herstelbaar `LifecycleEvent`-log (tombstoning i.p.v. hard-delete
overal); errata deprecieert via SUPERSEDES (ruling blijft bestaan) en invalideert
afhankelijke feiten/eval-cases. Een **model-upgrade** her-mint (BESLISSING #232)
uitsluitend de puur-LLM-ongesteunde feiten, **incrementeel met een budgetplafond** op
het abonnement-token — feiten met menselijke goedkeuring of onafhankelijke steun
blijven staan. **Gevolg.** Een nieuwe set breekt niets; schema-drift valt in CI om;
een model-bump kost een begrensde batch i.p.v. de hele graaf. De live Neo4j-projectie
van de staging-/tombstone-labels, de code-migratie bij promotie en het her-minen zelf
zijn integratie-follow-ups; de versionering, poorten, levenscyclus-services en
kostengate zijn puur en getest. EF-migratie `Governance230`.
`RbRules.Domain/Ontology/OntologyVersion.cs`, `SchemaProposal.cs`,
`RbRules.Domain/KnowledgeLifecycle.cs`, `ErrataLifecycle.cs`,
`ModelUpgradeInvalidation.cs`, `OntologyGovernanceService`, `KnowledgeLifecycleService`.

---

## 10. Kwaliteitsscenario's

Concreet en toetsbaar. "Verwacht" = het gedrag dat de code garandeert.

| # | Scenario (trigger) | Verwacht gedrag | Verankerd in |
|---|---|---|---|
| Q1 | Ollama down tijdens `/ask` | Vector-kanalen vervallen, degradeert naar FTS + naam/mechaniek/lexicaal; nooit een 500; trace toont "embedding-uitval" | `AskService` (#100), `AskServiceDegradationTests` |
| Q2 | rb-ai onbereikbaar | `RbAiClient` geeft null; `/ask` toont `UnavailableAnswer`, Ok=false; scan/classify slaan de LLM-stap over | `RbAiClient`, `IngestService` |
| Q3 | Neo4j down | `/api/brain/neighbors` en `/path` geven een nette Problem-response; `search`/`node`/`evidence`/`contradictions` blijven werken | `BrainEndpoints`, `docs/BRAIN.md` §2.3 |
| Q4 | Admin-job draait tijdens een merge | De deploy wacht met `compose up` tot de job klaar is (of tot ~20 min) | `v2-deploy.yml` (#95) |
| Q5 | Twee snelle merges achter elkaar | Publishes/deploys serialiseren; elke deploy pint zijn eigen SHA en verifieert zichzelf | `v2-ci.yml`, `v2-deploy.yml` (#82/#86) |
| Q6 | Interactievraag met 2+ kaartnamen (flag `auto`) | `AgenticGate` escaleert naar het brein; faalt de agent, dan levert het vangnet het single-pass-antwoord | `AgenticGate`, `AskService` (#107) |
| Q7 | Community-bron blokkeert datacenter-IP | Lege/gedeeltelijke oogst is een verwacht resultaat, gelogd in `run_log`, geen job-fout | `docs/KNOWLEDGE.md`, `SourceScoutService` |
| Q8 | Community-claim spreekt officiële § tegen | Claim wordt niet als kennis gepresenteerd; officieel wint altijd; weerlegde claims alleen via `contradictions`, gelabeld | `AskService.BasePrompt`, `GraphSyncService` (scope) |
| Q9 | VM-reboot, Postgres nog niet klaar | rb-api retriet de migratie kort en begrensd; anders faalt de start hard en vangt de deploy-verify het | `Program.cs`, `docker-compose.yml` healthcheck |
| Q10 | Regressie in domeinlogica | Elke productie-bug krijgt eerst een regressietest; CI is de poort (test-gate vóór publish) | `docs/CONVENTIONS.md`, `RbRules.Tests/`, `v2-ci.yml` |
| Q11 | Eén parallel retrieval-kanaal van `/ask` gooit (bv. de misvattingen-query faalt) | Dat kanaal levert leeg + een marker in de trace (`kanaal-uitval: ...`); de overige kanalen en het antwoord blijven ongemoeid, nooit een 500. Sequentieel (zonder factory) vs. parallel (met factory) leveren byte-voor-byte dezelfde prompt | `AskService` (#152), `AskServiceParallelRetrievalTests` |

---

## 11. Risico's & technische schuld

- **Dubbel deploymechanisme (rest-risico, #45).** Watchtower-labels staan uit op
  de v2-services, maar de Watchtower-daemon draait op de VM nog wél. Zolang de
  labels correct staan is er één effectief mechanisme; #45 (ops-hardening)
  staat nog open.
- **Gedeprecte PoP in de repo-root.** De oude Next.js-PoP (`src/`,
  `docker-publish.yml`) is nog aanwezig maar vervangen; verwarringsrisico bij
  navigatie/CI. Alleen `docker-publish.yml` is nog handmatig triggerbaar.
- **Lege kennislaag 2 in productie (#92/#93).** Claims-extractie faalde stil
  (#93) en documenten werden te vroeg als gemined gemarkeerd (#92); de
  claims-reviewqueue kan daardoor leeg blijven. Het brein werkt zonder claims,
  maar de claim-knopen blijven leeg tot dit is opgelost (`docs/BRAIN.md` §1.5).
- **Neo4j is jong als lees-consument.** Pas sinds #104/#105 wordt de graph
  gelezen (brein-API); drift tussen Postgres en Neo4j wordt gemeten, niet
  vermeden (`KnowledgeGapsService`).
- **Reasoner-executie nog niet live geverifieerd (#227).** De fase-3-inferentie-
  regels + contradictie-patronen zijn puur getest, maar de Cypher draaide nog niet
  tegen een echte Neo4j (geen CI/lokale instance) — zelfde schuld als de fase-2-
  projectie. Bovendien veronderstellen enkele regels (isa-overerving, property-
  chains) bron-edges/class-anchor-labels die `GraphSyncService` nog niet
  materialiseert; die projectie-uitbreiding + een integratietest tegen een echte
  Neo4j is de openstaande follow-up (`ReasoningService`, `InferenceRuleRegistry`).
- **Openstaande architectuurrakende issues.** O.a. #122 (periodieke
  zelfverrijking in de scheduler), #121 (echte token-metering), #124/#125
  (reviewqueue-/misvattingen-laag), #127 (publieke databank), #15 (decks:
  deck-browser + legaliteit live, "populair in N%"/scheduler-tick/meta-laag
  in `/ask` nog in golf 1/2). Er zijn PR's onderweg; de exacte stand staat in
  #55 (masterplan) en #60 (handoff).
- **Kosten/latency van agentic.** Een geëscaleerde vraag kan van ~10s naar
  30-90s gaan; gemitigeerd door de gate, maxTurns/tool-cap/harde timeout en
  Sonnet i.p.v. Opus, maar meten vóór verbreden blijft nodig (`docs/BRAIN.md`
  §4).

---

## 12. Begrippenlijst

| Term | Betekenis |
|---|---|
| Riftbound | League of Legends TCG waar deze companion over gaat |
| Rules Hub | Officiële regelpagina op playriftbound.com; bron van Core/Tournament Rules-PDF's |
| Core / Tournament Rules | De twee normatieve regel-PDF's (laag 0) |
| Ban / Erratum | Verboden kaart / officiële tekstcorrectie op een kaart |
| Ruling | Scheidsrechter-oordeel; geverifieerde rulings zijn gezaghebbend (laag 0b) |
| Primer | Gegenereerde spelbegrip-concepten (laag 1), review door beheerder |
| Claim | Geparafraseerde community-bewering met corroboratie en trust (laag 2) |
| Kennispiramide | Voorrangsvolgorde officieel > rulings > primer > community > meta |
| BrainRef | Canonieke tekstuele identiteit (`card:…`, `section:…`) over pgvector + Neo4j + API |
| Brein / brein-API | Unified vector+graph-kennismodel + de zes koppelvlakken `/api/brain/*` |
| Agentic ask | Meer-beurten AI-pad dat zelf het brein bevraagt via MCP-tools |
| GraphRAG | Retrieval waarbij de getypeerde graaf de index is: entity-linking → β(q)-router → Local/Global/Path/Drift-modi → trust-gating → bundeling (fase 4, #228) |
| β(q)-router | Weegt het graph- vs. community-kanaal: entity-dicht → graph, abstract → community (`BetaRouter`) |
| AnswerTrace | Immutable auditspoor per /ask: welke subgraaf/paden/trust-gewichten-toen het antwoord droegen (§6/#236) |
| Mechanic-predicaat | Getypeerde mechanic-eigenschap (triggers_on/prevents/grants/requires_target) op een `CanonicalEntity`; het structurele signaal voor de hypothese-motor (fase 5, #229) |
| Hypothese-motor | Abductieve kandidaatgeneratie: complementair property-antagonisme ⇒ gerichte interactie-hypothese (O(n·k) i.p.v. blind N²), naar LLM-verificatie (`HypothesisEngine`, fase 5) |
| Cold-start (model_hypothesized_unruled) | Tier voor een emergente card×card-hypothese die de LLM bevestigt maar die geen officiële/onafhankelijke steun heeft — geparkeerd voor micro-review, nooit stil weg (fase 2/5) |
| Residueel kanaal | Begrensd, laag-prioriteit embedding-cosine-kanaal voor interacties zonder structurele signatuur (cosine-vloer + top-K + budgetplafond, `ResidualInteractionChannel`, fase 5) |
| TrustDecision | Expliciete, context-afhankelijke conflict-resolutie-knoop (cross-tier-veto / supersede / alias) met memo — nooit een hard-delete (`TrustConflictResolver`, fase 5) |
| RRF | Reciprocal Rank Fusion; fuseert vector- en full-text-ranglijsten |
| bge-m3 | Meertalig embeddingmodel (1024-dim) dat lokaal via Ollama draait |
| Canonieke printing | De naamloze basis-printing van een kaart; alt-arts zijn varianten (#57) |
| Set-release-keten | Geautomatiseerde keten die bij een nieuwe set alle afgeleiden bijwerkt |
| Cadence | Scan-interval per bron |
| Sidecar | rb-ai: de interne AI-container op het Claude-abonnement |
| Reïficatie | Een gekwalificeerde relatie als eigen knoop (`Interaction`) i.p.v. kale edge, zodat condities niet verloren gaan (fase 2, #226) |
| `model_hypothesized_unruled` | Cold-start-trust-tier: emergente card×card-hypothese zonder officiële/community-steun — geparkeerd, niet weggegooid |
| RejectionTombstone | Grafsteen op een verworpen interactie die stil-heropenen blokkeert; opheffen is een expliciete beheerdersactie (herstelpad) |
| Brein-mining / tool-forced extractie | De handmatige jobs `breinmine-interacties`/`breinmine-predicaten` (#226, §6.6): rb-ai levert via een geforceerde tool-call (`emit_interactions`/`emit_mechanic_predicates`) ontologie-begrensde kandidaten die door de fase-2-poort resp. als review-kandidaat landen; degradatie = null (geen half feit) |
| Reasoner / redeneer-laag | Neo4j-native inferentie-run (fase 3, #227): leidt edges af (isa-closure, property-chain, symmetrie, subproperty-collapse) en detecteert tegenspraken — één engine, Cypher, geen Datalog |
| Afgeleide edge | Een door de reasoner gematerialiseerde edge (`derived=true`+`derivedByRule`+provenance); nooit bron van waarheid, elke run herberekend |
| `ReasoningConflict` | Postgres-rij voor een door de reasoner gedetecteerde tegenspraak (claim↔officieel, botsende rulings, disjointness), gerouteerd naar misvattingen/reviewqueue/escalatie |
| Eval-ring (A/B/C) | De drie CI-ringen van de eval-harness (fase 7, #231): A deterministisch/€0/elke PR, B LLM-judge op de kern-set, C volledig+meta nachtelijk |
| Baseline-diff-per-klasse | Eval-gate die per (question_class × metric) tegen een vastgelegde baseline diff't (`mean < baselineMean − kσ`) i.p.v. een absolute drempel — vangt sluipende degradatie die het gemiddelde verbergt (fase 7, #231) |
| CO_OCCURS-signaal | Kruisvalidatie van structureel voorspelde combo-paden (fase 5) met de echte Piltover-deck-meta: co-decks/support/lift + corroboratie-rate (fase 7, #231/#15) |

---

## Onderhoud (het anker van dit document)

Dit document is levende documentatie. Elke PR die een van onderstaande
wijzigingen raakt, **werkt dit document in dezelfde PR bij** — net als de
`docs/CONVENTIONS.md`-regel dat conventiewijzigingen via PR gaan.

| Soort wijziging | Werk bij |
|---|---|
| Nieuw endpoint of endpointgroep | §5 (bouwsteen: modules/endpointgroepen) en §6 (runtime, als er een nieuwe flow bij komt) |
| Nieuwe datastore of externe dependency | §3 (context/externe systemen), §5 (datastores) en §7 (deployment/compose) |
| Nieuw taaktype of AI-koppelvlak in rb-ai | §5 (rb-ai-modules), §6 (runtime) en zo nodig §2 (randvoorwaarden) |
| Nieuwe conventie of dwarsdoorsnijdend patroon | §8 (dwarsdoorsnijdende concepten) |
| Deploy-/CI-/compose-wijziging | §7 (deployment) en zo nodig §11 (risico's) |
| Belangrijke architectuurkeuze (met issue/PR) | §9 als nieuwe ADR, met verwijzing naar de issue/PR |
| Nieuw kwaliteitsscenario of degradatiepad | §10 (kwaliteitsscenario's) |
| Nieuwe Riftbound-/projectterm | §12 (begrippenlijst) |

Bij twijfel: voeg liever één regel toe dan het document te laten verouderen.
Een wijziging die geen enkel hoofdstuk raakt, is zeldzaam — controleer dan
minstens of §11 (risico's/schuld) nog klopt.
