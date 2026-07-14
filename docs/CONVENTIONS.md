# Code-conventies & engineering-principes — RB-Rules

Dit document is bindend voor alle code in deze repo (rb-api, rb-web, rb-ai).
Wijzigingen aan de conventies gaan via PR, net als code. Pragmatisme wint van
ceremonie: dit is een klein, zelfgehost project — elk patroon moet zich hier
concreet bewijzen.

## Kernprincipes

- **KISS / YAGNI** — bouw wat nu nodig is. Geen abstracties "voor later";
  een interface pas als er een tweede implementatie of een test-seam nodig is.
- **DRY, maar niet dogmatisch** — logica die twee keer voorkomt en samen moet
  evolueren verhuist naar één plek (Domain-helper of service). Twee keer
  toevallig dezelfde drie regels is geen duplicatie.
- **SOLID, praktisch toegepast**:
  - *SRP*: één service = één verantwoordelijkheid (CardSyncService haalt en
    groepeert kaarten; AskService beantwoordt vragen; niet mengen).
  - *Open/closed*: nieuwe vraagtypes/jobs/bronnen toevoegen zonder bestaande
    routes te herschrijven (QuestionRouter-switch, JobRunner-switch,
    SourceSeed-lijst zijn de uitbreidpunten).
  - *Liskov/Interface segregation*: kleine, gerichte contracten; geen
    god-interfaces. Concreet > abstract totdat het pijn doet.
  - *Dependency inversion*: afhankelijkheden via constructor-DI. Nooit
    `new HttpClient()` of service-locator in een method body.
- **Fouten zijn data** — pijplijnen zijn best-effort per stap: een haperende
  externe dienst (Ollama, rb-ai, Riot) stopt nooit de hele run, en de fout is
  altijd zichtbaar (run_log, job-detail, Problem-response met detail). Nooit
  een kale 500, nooit een lege catch die informatie weggooit die de beheerder
  nodig heeft.
- **De bron van waarheid is extern** — wij bewaren wat Riot publiceert (rauwe
  tokens, document-snapshots) en leiden weergave/afgeleiden daarvan af.
  Afgeleiden (embeddings, mechanics, uitleg-cache) zijn altijd opnieuw
  opbouwbaar en invalideren bij bronwijziging.

## Architectuur (rb-api)

Lagen, met afhankelijkheden strikt éénrichting:

```
RbRules.Api  →  RbRules.Infrastructure  →  RbRules.Domain
```

- **Domain**: entiteiten, pure logica (parsers, routers, tekst-utils). Geen
  EF-, HTTP- of framework-afhankelijkheden. Alles hier is unit-testbaar
  zonder infrastructuur. Bewuste, enige uitzondering (#44): het kale
  `Pgvector`-pakket voor het `Vector`-datatype op entiteiten — dat is een
  datatype, geen I/O; EF Core en Npgsql zelf blijven buiten Domain.
- **Infrastructure**: services met I/O — EF Core (`RbRulesDbContext`), HTTP
  (RbAiClient, sync-services), Neo4j, Ollama. Publieke API van een service is
  een of twee methoden met een duidelijk resultaat-record.
- **Api**: compositie. `Program.cs` bevat alleen DI-registraties,
  migratie/seed en de `MapXxxEndpoints()`-aanroepen; endpoints leven per
  feature in `Endpoints/*.cs` als extension-methods op
  `IEndpointRouteBuilder` (officieel MapGroup-patroon). Request/response-
  records staan in `ApiContracts.cs`.

Endpoint-regels:
- Endpoints zijn dun: valideren, service aanroepen, projecteren. Query's van
  meer dan ~15 regels of logica met vertakkingen horen in een service.
- Publieke reads gebruiken projecties (`Select` naar anonieme types) — nooit
  hele entiteiten met embeddings serialiseren.
- Beheer-endpoints zitten uitsluitend onder de `/api/admin`-groep met
  `AdminAuthFilter`.
- Lange operaties draaien via `JobRunner` (202/409 + voortgang), nooit
  synchroon in een request.

## Data & EF Core

- **Migrations zijn heilig**: elk schema-verschil via `dotnet ef migrations
  add`; nooit handmatig schema muteren. Migraties draaien bij opstart.
- Tabellen/kolommen snake_case; vector-kolommen getypt op
  `EmbeddingConfig.Dimensions` met HNSW-index.
- Read-only query's: `AsNoTracking()`. Bulk-verwijderen:
  `ExecuteDeleteAsync` — maar nooit gemengd met getrackte entiteiten die
  daarna nog `SaveChanges` krijgen.
- LINQ dat naar SQL moet vertalen: alleen bewezen vertaalbare constructies
  (string-`Contains`, geen char-overload; geen eigen methodes in expression
  trees). Bij twijfel: test tegen Postgres of materialiseer bewust.
- Embedding-provenance: elke embedding slaat het modelnaam op; model-wissel
  = expliciete her-embed, nooit stilzwijgend mixen van dimensies.

## AI-gebruik (rb-ai / prompts)

- **Afgeleide/gesynthetiseerde kennis wordt in de brontaal (Engels) opgeslagen**
  (#187) — claims (`ClaimMiner`), de primer (`PrimerService`) en
  relatie-`explanation`s (`RelationMiner`, `rb-ai`'s `AGENT_ADDENDUM`)
  extraheren/synthetiseren in het Engels, dicht bij de officiële bewoording:
  geen vertaalstap, dus geen vertaalverlies, en consistente semantiek met de
  Engelse kaart-/regelbronnen zelf. **UI en /ask-antwoorden blijven
  Nederlands** — dat scheidt `AskService.BasePrompt` af, dat blijft
  ongewijzigd. (`ClarificationMiner`/`ClarificationMiningService` volgen
  hetzelfde patroon, #185.) Een bestaande Nederlandse afgeleide laag wordt
  niet in-place vertaald maar weggegooid en schoon herbouwd — zie
  `KnowledgeRegenerationService` (expliciete, destructieve admin-actie, nooit
  automatisch; raakt nooit de bron-/mensenwerk-tabellen).
- Alle LLM-verkeer via de rb-ai-sidecar (abonnement); rb-api kent geen
  API-keys. De sidecar is alleen intern bereikbaar.
- LLM-uitval is een verwacht pad: `RbAiClient` geeft `null` terug en de
  aanroeper degradeert netjes (geen classificatie, geen uitleg — geen crash).
- Prompts zijn code: systeem-prompts staan als const bij de service, met
  structuur-eisen expliciet uitgeschreven (zie de ruling-skill in
  AskService + QuestionRouter). Wijzigingen aan prompts gaan door review.
- Dure LLM-resultaten worden gecachet met een expliciete invalidatie-regel
  (similarity_explanation ↔ kaarttekst-wijziging).
- Wat de gebruiker ziet is altijd herleidbaar: citaten met §, kaartfeiten
  als bewijs, zekerheids-label.

## Frontend (rb-web, Svelte 5 / SvelteKit)

- Svelte 5 runes (`$props/$state/$derived/$effect`); `$effect` altijd met
  cleanup als er timers/subscripties in zitten; reset afgeleide state bij
  route-hergebruik (component blijft leven bij client-side navigatie).
- Data-ophalen server-side in `+page.server.ts` via de `api()`-helper;
  de browser praat nooit rechtstreeks met rb-api — altijd via een
  `+server.ts`-proxy als client-side fetch nodig is.
- Types voor API-responses staan bij de load die ze gebruikt; zodra twee
  routes hetzelfde type nodig hebben verhuist het naar `$lib/types.ts`.
- **Niets ongesaniteerd in `{@html}`**: tekst wordt ge-escaped vóór
  markdown-parse/icoon-injectie (`$lib/markdown.ts`, `$lib/rbtokens.ts`);
  link-URL's zijn gewhitelist.
- Ontwerptokens uit `app.css` (`var(--accent)` etc.); geen hardcoded kleuren
  in nieuwe componenten, geen emoji's in UI-tekst — status via kleur + tekst.
- Formulieren via form actions + `use:enhance`; fail-paden geven de bestaande
  paginastate terug (antwoord/citaties mogen niet verdwijnen door een fout).

## Foutafhandeling & observability

- Elke achtergrond-actie logt naar `run_log` (kind/ref/status/detail); de
  admin toont live voortgang via JobRunner.Progress.
- Meet wat de gebruiker voelt: `ask_metric` voor antwoordduur — geen
  verzonnen getallen in UI-teksten.
- Catch-blokken: óf herstellen met gedegradeerd gedrag, óf loggen en
  doorgeven. Een lege catch mag alleen waar de comment uitlegt waarom stilte
  correct is ("logging mag een job-afronding nooit blokkeren").

## Testen

- Domain-logica (parsers, routers, mappers, diff/tekst-utils) heeft
  unit-tests; elke productie-bug krijgt eerst een regressietest die hem
  reproduceert (zie ParseGallery_SkipsSetFacetItems,
  MapCard_HandlesEmptyTypeList, LineDiff_EmptyWhenOnlyReordered).
- Tests tegen echte data-vormen: fixtures spiegelen de live JSON/PDF-vormen
  van Riot, niet versimpelde verzinsels.
- CI is de poort: `dotnet test` + `svelte-check` + `tsc` moeten groen zijn
  vóór images gepubliceerd worden. Niet mergen op rood.

## Git & proces

- Kleine, thematische commits met een NL-onderwerpregel die het "waarom"
  meegeeft; PR-beschrijvingen benoemen gebruikerszichtbaar effect én
  eventuele na-deploy-stappen.
- Feature-werk op de werkbranch, PR naar main; merge naar main = deploy
  (CI publiceert images, deploy-workflow rolt uit).
- Secrets alleen via GitHub Secrets / VM-`.env`; nooit in code, logs of chat.
- **Levende documentatie (#134)** — elke PR die endpoints, datamodel,
  services, UI-routes of de deploy raakt, werkt `docs/ARCHITECTURE.md`
  (arc42) bij; elke PR die features of gedrag wijzigt, werkt `docs/PRD.md`
  bij. De Onderhoud-hoofdstukken in beide documenten zeggen per soort
  wijziging welke sectie. Geen doc-delta nodig? Motiveer dat kort in de
  PR-body. De PR-template bevat de checklist.
