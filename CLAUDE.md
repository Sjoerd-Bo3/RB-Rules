# RB-Rules — projectcontext voor Claude

## Wat dit is
Riftbound (League of Legends TCG) Rules Companion van Sjoerd (GitHub
`Sjoerd-Bo3`). Eén altijd-actuele bron voor regels, bans, errata, rulings en
kaarten — automatisch bijgehouden uit officiële bronnen, met een AI-vraagbaak.
Het product/merk heet sinds #216 **Poracle** (poro-mascotte, `PoroMark`-component
+ gele app-tegel); inhoudelijk blijft het een Riftbound-companion (omschrijving
en disclaimer noemen Riftbound onverkort).
Live: https://riftbound-v2.bo3.dev (het oude riftbound.bo3.dev is de
Next.js-PoP, vervangen; alleen `docker-publish.yml` handmatig triggerbaar).

**Einddoel** (docs/KNOWLEDGE.md): één samenhangend "brein" — alle kennis
(regels, kaarten, mechanieken, community-interpretatie, meta) vector- én
graf-gelinkt, met een brein-API waarmee AI-tools zelf redeneren, interacties
ontdekken en nieuwe features bouwen. De kennisbank moet mee-evolueren met
elke nieuwe set (nieuwe mechanieken, keywords, kaarten).

## Stack & architectuur
- **rb-api** — .NET 10 minimal API. Lagen strikt éénrichting:
  `Api → Infrastructure → Domain`. Endpoints per feature in
  `RbRules.Api/Endpoints/*.cs` (MapGroup/extension-patroon); Program.cs is
  alleen compositie. EF Core-migraties draaien bij opstart.
- **Data**: Postgres + pgvector (getypte `vector(1024)`, HNSW; embeddings via
  lokale **Ollama bge-m3** — dimensie/model-provenance is heilig) en **Neo4j**
  (getypeerde relaties; batched UNWIND, dictionaries-only params).
- **rb-ai** — TS-sidecar met de Claude Agent SDK op Sjoerds **abonnement**
  (`CLAUDE_CODE_OAUTH_TOKEN`, nooit API-key in rb-api). Alleen intern
  bereikbaar. AI-uitval = verwacht pad: null terug, nette degradatie.
- **rb-web** — SvelteKit, Svelte 5 runes, adapter-node. Ontwerptokens in
  `app.css`. Browser praat nooit direct met rb-api (server-loads of
  +server.ts-proxy's).
- **Deploy**: merge naar main → CI (test-gate) publiceert 3 GHCR-images →
  `v2-deploy.yml` deployt via SSH naar de Azure-VM (~/compose/rb-rules-v2,
  centrale Caddy, /mnt/data-binds), **gepind op commit-SHA** en met een
  admin-job-gate in de workflow zelf. Compose-file wordt door de workflow
  gesynct; Watchtower staat uit voor de v2-stack (labels). Details:
  docs/ARCHITECTURE.md §7.

## Kernfeatures
De actuele, volledige feature-inventaris staat in **docs/PRD.md §4**
(bindend bijgehouden per PR — zie werkafspraak 10); hier alleen de
hoofdlijnen. Wijzigingen-feed met diff en flip-flop-suppressie ·
regels-browser met §-permalinks, semantisch zoeken en sectie-dossiers ·
kaartbrowser + kaartdetail met dossier (rulings, claims, relaties,
ban-historie) en variantgroepering · doorzoekbare /rulings-databank · /ask
met vraag-router, scheidsrechter-format, streaming + voorlezen, doorvragen,
board-state-foto's, query-rewrite, citaties/widget-markers en
misvattingen-kanaal · kennisbank/brein: kennispiramide, primer,
claims-pipeline, brein-API, agentic ask (flag) met relatie-terugkoppeling,
dynamische relaties, kennis-levenscyclus, graph-verkenner · beheer: jobs met
live voortgang, aanklikbare tegels, reviewqueues met bewijs/archief/notities,
vraag-traces, token-metering, kennis-gaten-rapport, periodieke
zelfverrijking · platform: PWA + web-push, accounts (magic-link + passkeys)
met quota en rate-limiting.

## Werkafspraken met Sjoerd (belangrijk!)
1. **Nederlands** antwoorden; Engelse speltermen onvertaald. Geldt voor UI en
   /ask-antwoorden — **afgeleide/gesynthetiseerde kennis (claims, primer,
   relatie-uitleg) wordt sinds #187 in de brontaal (Engels) opgeslagen**, dicht
   bij de officiële bewoording (docs/CONVENTIONS.md). **Opslag ≠ weergave**
   (#266, ADR-17): komt zulke kennis rechtstreeks als leespagina bij de
   bezoeker (nu alleen /primer), dan krijgt ze bij de GENERATIE een
   Nederlandse weergave naast de Engelse tekst — door dezelfde
   draft/approve-poort, met een glossarium-waarborg voor de speltermen, en
   nooit als vertaalstap bij het renderen. Officiële regel- en kaartteksten
   worden nooit vertaald (#189).
2. **Geen emoji's in de UI** — serieus, strak design via de tokens in
   app.css. Status = kleur + tekst.
3. **Nieuwe wensen tussendoor → eerst een GitHub-issue**, niet direct
   bouwen. Sjoerd wil alles getrapt terugvinden.
4. Autonoom werken is gewenst: eigen volgorde bepalen, PR's maken én na
   groene CI **zelf mergen**, zelf verifiëren (tests + typecheck + build +
   waar zinnig de site echt bekijken). Elke productie-bug krijgt eerst een
   regressietest.
5. **Nooit mergen/deployen terwijl een live admin-job draait** — de deploy
   herstart de container en breekt de job af.
6. Zelf testen kan: rb-web lokaal + stub-API + Playwright-screenshots op
   390/768/1280px (zie PR #47-aanpak); horizontale overflow moet 0 zijn.
7. Secrets nooit in code/chat; GitHub Secrets of VM-.env. Admin-API: rb-web
   `/admin?/login` (form-POST met origin-header) geeft een `rb_admin`-cookie;
   daarmee kan `/admin/status` gepolld en kunnen jobs gestart worden.
8. Conventies zijn bindend: **docs/CONVENTIONS.md** (KISS/YAGNI, pragmatische
   SOLID, endpoints dun → logica in services, EF-vertaalbaarheid — géén
   `Contains(char)`!, transacties rond rebuilds, sanitize vóór `{@html}`).
9. Kennisbank-visie en gelaagdheid: **docs/KNOWLEDGE.md** (officieel >
   geverifieerde rulings > primer > community-claims met bron-trust en
   corroboratie > meta; alles expliciet gelabeld in de prompt).
10. **Levende documentatie is bindend** (#134): elke PR die endpoints,
    datamodel, services, UI-routes of de deploy raakt, werkt
    **docs/ARCHITECTURE.md** (arc42) bij; elke PR die features of gedrag
    wijzigt, werkt **docs/PRD.md** bij. Beide documenten hebben een
    Onderhoud-hoofdstuk met de tabel *soort wijziging → sectie*. Geen
    doc-delta nodig? Motiveer dat in de PR-body. Dit geldt óók voor dit
    bestand: raak je een werkafspraak of valkuil, werk CLAUDE.md mee.

## Valkuilen uit de praktijk (duur betaald)
- Riot's domein is **playriftbound.com**; PDF-links zijn opake Sanity-CDN-
  hashes — matchen op ankertekst ("Core Rules"). Gallery-JSON bevat
  set-facetten en token-kaarten met lege type-lijst (regressietests bestaan).
- Kaart-sync: Riot-gallery is de **leidende** bron; de riftcodex-API (werkt
  wél vanaf de VM) vult aan — riftcodex-eerst conserveerde naamschade (#150).
  Sinds #270 staat die voorrang op één plek (`CardMerge`, ADR-15): leidend
  schrijft **onvoorwaardelijk** (ook leeg — ontbreekt een veld in Riots
  payload, dan hééft de kaart het niet), aanvullend vult **alleen lege
  velden**. De aanvul-pass slaat bestaande kaarten dus niet meer volledig
  over maar dicht hun gaten; beschadigen kan hij per constructie niet.
  "Leidend" is een rol in de run: valt Riot uit, dan is riftcodex leidend —
  anders bevriest de kaartenset zolang Riot plat ligt.
  Riftcodex-site/Mobalytics blokkeren datacenter-IP's (Cloudflare).
- **Wat een mapper mapt is geen bewijs van wat een bron levert** (#270): de
  aanname dat riftcodex geen alt-tekst/illustrator/orientation heeft kwam uit
  het lezen van `RiftcodexCardMapper`, maar hun API levert `media.artist`,
  `media.accessibility_text`, `orientation` en een `new`-vlag wél (kleuren,
  `mightBonus`, `effect` en `publicCode` niet). Bevraag bij twijfel de live
  bron; datzelfde gold voor de Riot-gallery, die per kaart veel meer
  meestuurde dan we bewaarden.
- Riot publiceert de icoon-glyphs voor de `:rb_…:`-tokens zelf, met
  bestandsnamen die 1-op-1 op de tokennaam mappen
  (`assetcdn.rgpub.io/…/riot-glyphs/rb/latest/{token}.svg`). Nooit zelf
  natekenen; ze staan gevendord in `rb-web/static/glyphs/`
  (`scripts/fetch-glyphs.sh`, ADR-16). Let op: ze zijn getekend voor een
  **donkere** UI (`might`/`exhaust` puur wit) en slibben dicht onder ~14px.
- **"LLM boven regex" (#188) is geen vrijbrief — meet eerst wat de bron al
  gedrukt heeft** (#211/#249, ADR-17). Riftbound-keywords staan letterlijk
  gebracket in de kaarttekst (`[Equip]`, `[Assault 2]`): meting over de 1429
  live kaartteksten gaf 31 keywords, állemaal in die vorm, met ~3% van de
  vermeldingen érgens zonder haken. Een LLM daarop loslaten is precies de
  verspilling die #249 aantoonde (69% herkauwde `Card.Mechanics[]`). Regel:
  lees wat gedrukt is, geef de LLM een **gesloten** vraag over de rest, en
  reken zijn antwoord deterministisch na (alleen toevoegen, nooit afnemen,
  nooit een term buiten het aangeboden lijstje). Magnitudes horen bij hun
  familie — "Assault 2"/"Assault 3" zijn beide `Assault`, nooit een eigen
  entiteit.
- Rules Hub wisselt per request de volgorde van artikellinks →
  flip-flop-suppressie in IngestService (hash-historie + lege-diff-guard).
- adapter-node: form-POSTs vereisen `ORIGIN`-env lokaal; `BODY_SIZE_LIMIT`
  is gezet voor foto-upload.
- iOS zoomt op form-controls < 16px (app.css-fix aanwezig).
- Piltover Archive (deck-ingest #15): alléén de sitemap en publieke
  `/decks/view/{uuid}`-pagina's — hun `/api/` is robots-disallowed en blijft
  onaangeraakt. Browser-UA verplicht (403 zonder); deck-data zit als
  RSC-flight in `self.__next_f.push`-chunks (parser: `PiltoverDeckPage`).
- Bron-feeds (#167): de rules-and-releases-, algemene nieuws- en Rules-Hub-
  index delen dezelfde kaartcomponent (`RiotNewsFeed`-parser dekt alle drie),
  maar ook de "smalle" feed toont af en toe een andere categorie tussendoor
  (CategoryFilter dus overal, niet alleen op de brede hub); sommige
  artikel-URL's missen het categorie-segment en een enkele kaart linkt extern
  (YouTube) — uitsluiten op host, niet op categorie.
- Bron-overlap (#175): meerdere `Source`-rijen kunnen bewust dezelfde
  letterlijke URL delen (de Rules Hub-PDF/HTML-drieling in `SourceSeed`, elk
  met een eigen Parser) — dat is GEEN near-duplicaat. De near-duplicaat-
  samenvoeging in `FeedCrawlService.MergeNearDuplicateSourcesAsync` telt een
  groep daarom alleen mee als élke rij een eigen (zwak-genormaliseerde)
  URL-vorm heeft; zit er ergens in de groep een letterlijk-gelijk paar
  tussen, dan blijft de hele groep ongemoeid — anders eet een toevallige
  http/https- of www-variant per ongeluk een bewust-gedeelde bron op. Een
  groep met een genegeerde bron erin (#180, `IgnoredAt`) wordt om dezelfde
  reden overgeslagen: een merge zou de negeer-beslissing stil ongedaan maken.
- Tie-break-richting is context-afhankelijk (#206): `Precedence.Compare`
  (#168) laat bij gelijk gezag de RECENTSTE datum winnen (welke tekst geldt
  NU) — changeconsolidatie (#206, `ChangeConsolidationPrimary.Wins`) wil bij
  gelijke trust juist de VROEGSTE detectie (wie meldde het gebeurtenis het
  eerst). Niet klakkeloos `Precedence.Compare` hergebruiken voor een nieuwe
  "wie wint"-vraag zonder te checken of de tie-break-richting ook klopt.
- **`TextUtils.StripBoilerplate` wijzigen? Bump `TextUtils.
  BoilerplateVersion`** (#205-review) — de gestripte tekst bepaalt de
  content-hash van élke bron, dus een stille strip-wijziging geeft één golf
  junk-"changes" over het hele register (de diff toont alleen de weggevallen
  boilerplate). Met de bump rebaselinet elke bron stil bij de eerstvolgende
  scan (`Source.StripVersion`-vergelijking in `IngestService`), zonder
  Change en zonder her-mine-kosten.
- **State die een client-side navigatie moet overleven hoort niet in
  component-`$state`** (#248) — een SvelteKit-component unmount bij navigatie,
  en neemt een lopende `fetch`/`ReadableStream` mee het graf in (op `/ask` was
  je zo je antwoord én je lopende search kwijt). Zulke state hoort in een
  module-level runes-store (`$lib/*.svelte.ts`), met drie randvoorwaarden:
  (a) module-state is tijdens **SSR gedeeld tussen alle bezoekers** — schrijf
  er dus nooit tijdens het renderen in, alleen vanuit browser-acties;
  (b) de store draait door buiten zijn eigen route, dus **absolute paden**
  (`/ask?/ask`, nooit `?/ask`) en geen `$app/navigation`-import — laat de
  pagina zulke route-gebonden dingen als haakje ophangen zolang zij leeft;
  (c) bij een reload breekt de browser de stream af en loopt je catch nog één
  keer — die zou "verbinding weg" over de "onderbroken door herladen"-
  momentopname schrijven, dus vanaf `pagehide` niets meer persisteren.
- **Een nieuwe env-vlag moet óók in de compose-`environment:`** (#268-follow-up)
  — Docker Compose geeft alleen door wat expliciet onder `environment:` van de
  service staat. Een variabele in de VM-`.env` is een *substitutie*-bron voor de
  compose-file, géén container-env. `NIGHTLY_ENABLED` (de noodrem op de
  nachtrun) stond alleen in de `.env` en bereikte rb-api dus nooit: `docker exec
  rb-v2-api printenv | grep -i nightly` gaf niets, terwijl we dáchten een
  noodrem te hebben. Voeg elke nieuwe vlag toe aan
  `deploy/server-setup-v2/docker-compose.yml` met een `${VAR:-default}` die het
  bestaande gedrag houdt, en verifieer met `printenv` op de VM.
  **Beter nog (#254): maak er geen env-only vlag van.** Een schakelaar die
  Sjoerd moet kunnen omzetten hoort in de beheerde instellingen-laag
  (`ManagedSettingsCatalog` + `ManagedSettingsService`, tabel `setting`): env
  blijft de bootstrap-default, de DB-override wint, en lezen gebeurt op het
  gebruiksmoment zodat een toggle direct werkt. Env-only is nog prima voor
  secrets en infra-adressen — niet voor gedrag dat je wilt kunnen bijsturen.
- **Werk parallelliseren = drie dingen tegelijk regelen, niet één** (#279).
  (a) `DbContext` is niet thread-safe: elke worker een eigen context uit de
  `IDbContextFactory` (patroon: optionele factory-parameter, zoals `AskService`
  #152 — zonder factory val je terug op sequentieel, zodat de EF-InMemory-tests
  ongewijzigd blijven werken). (b) Zoek naar **lees-dan-schrijf op een unieke
  index**: de interactie-promotie dedupet op `(AgentRef, PatientRef, Kind)` en
  twee kaarten kúnnen hetzelfde paar voorstellen — gelijktijdig concluderen ze
  allebei "bestaat nog niet" en de tweede knalt. Serialiseer zo'n poort met één
  slot; dat kost niets als de winst in een 40s-LLM-call zit. (c) De rb-ai-
  semaphore is **gedeeld met /ask**: batch-werk krijgt een deelcap
  (`AI_INTERACTIVE_RESERVE` blijft vrij) én wijkt in de wachtrij, anders ziet
  een bezoeker tijdens de nachtrun "AI weg". Reserve ≥ 2, want een agentic
  vraag kost 2 permits. Bijkomend: `AI_MAX_CONCURRENCY` en het memory-plafond
  van `rb-v2-ai` horen bij elkaar (~300-400 MiB RSS per gelijktijdige sessie) —
  verhoog nooit het één zonder het ander.
- **De Claude Agent SDK GOOIT niet als een run mislukt** (#281) — ze eindigt met
  een gewoon `result`-bericht (`subtype: "error_max_turns"`, `is_error`,
  `api_error_status`, `errors[]`) en retryt een mislukte API-call bovendien
  ZELF tot 10× met exponentiële backoff, elke poging gemeld als
  `{"type":"system","subtype":"api_retry",…}`. rb-ai las geen van beide, dus 22
  van de 40 mining-kaarten faalden zonder één logregel. Gemeten backoff: 0,5 →
  1,0 → 2,3 → 4,5 → 9,6 → 16,4 → 32,1 s — na zeven pogingen 37 s zonder één
  verwerkt token, dus een aanhoudende 429/529 loopt gegarandeerd in onze eigen
  90 s-timeout en komt naar buiten als een generieke 500. **Een harde timeout
  die korter is dan de retry-keten eronder verkleedt elke upstream-fout als
  "traag".** Lees bij elke SDK-lus dus het result-bericht én `api_retry` uit;
  `rb-ai/src/failure.ts` doet dat nu op één plek. Diagnostiek gaat verplicht
  door `safeDetail`/`redactSecrets` — groottes en aantallen loggen mag,
  prompt-inhoud en het token nooit.
- **Reken een hypothese na op de wandkloktijd** (#281) — 40 kaarten in 43 min
  met 18 successen laat maar één oplossing toe: 22 × ~85 s, oftewel de
  90 s-timeout. Zulke rekensommen sluiten hele klassen verklaringen uit vóór je
  gaat graven, en ze kosten een minuut.
- **"De limiet wordt niet geraakt" ≠ "het ligt niet aan de omvang"** (#281,
  duur betaald) — dezelfde analyse verwierp de payload-hypothese omdat de
  payload worst case ~1,5% van het contextvenster is en "prompt te lang" een
  niet-herhaalbare 400 in <1 s zou zijn. Beide feiten kloppen, de conclusie
  niet: de payload werd niet *afgewezen*, maar dreef wel de **latency**, en
  latency tegen een vaste timeout is precies wat uitval bepaalt. Een
  productie-experiment met identieke kaarttekst gaf 3 refs → 200 na 49,0 s en
  39 refs → 500 na 92,1 s (meting van vóór de fix; sindsdien is dat een 504 met
  `code: extract_timeout`). Toets een omvang-hypothese dus altijd óók op DUUR,
  niet alleen op limieten. Tweede fout in dezelfde analyse: 45 → 47 → 55% werd
  als "schommelt dus willekeurig" gelezen terwijl het monotoon stíjgt — en een
  gefaalde kaart krijgt geen watermark, dus zo'n klim is juist het handtekening-
  patroon van een grootte-afhankelijke fout. Lees een reeks van drie niet als
  ruis zonder ernaar te kijken.
- **Vocabulaire dat met de kennisbank meegroeit is een schaalklip** (#281/#288)
  — de brein-extractie stuurt per kaart het hele aangeboden refs-vocabulaire
  mee. Dat groeit met elke set die het brein leert, dus de faalkans stijgt mee
  met de kennis: geen vaste 55% maar een klim. De timeout ophogen verschuift de
  klip alleen (`AI_EXTRACT_TIMEOUT_MS` staat er als ops-noodrem, niet als fix).
  Bij elk "per item sturen we alles wat we weten"-patroon: begrens het budget,
  of stel de vraag op het niveau waar het antwoord kaart-onafhankelijk is.
- **Meet je eigen cap niet als "de LLM is overbelast"** (#279) — rb-ai's
  semaphore-afwijzing komt óók als 429 terug, maar mét
  `{"code":"concurrency_limit"}`. Zonder dat onderscheid telt zelf-veroorzaakte
  verdringing mee in de generieke rb-ai-uitval en verdraai je de verkeerde knop.
  `AiCallOutcome.ConcurrencyLimited` ("429 AI-slots vol") staat daarom náást
  `RateLimited`. Zelfde les als 503-vs-429 in #251: gelijk herstelgedrag ≠
  gelijke oorzaak.
- **Een leespad migreren zonder de DEKKING van de opvolger te meten is een
  stille blackout met groene tests** (#258). `CardInteractions` (oud) →
  `Interactions` (gereïficeerd, #226) leek een kwestie van de query omzetten:
  de nieuwe tabel is inhoudelijk beter, de oude is opgevolgd. Meting op
  productie: oud 103 rijen over 94 kaarten, nieuw 8 rijen over 5 kaarten —
  waarvan NUL gepromoveerde kaart↔kaart-paren. Filteren op de promotiepoort
  (inhoudelijk juist!) had het kaartdetail op nul interacties gezet, zonder
  ook maar één falende test: de tabel bestaat, de query klopt, hij is alleen
  leeg. Oorzaak was niet het ontwerp maar de vulling (18 van 1311 kaarten
  gemined, doordat de extractie op rb-ai-5xx strandt, #281). Regel: bij élke
  bron-wissel in een leespad eerst `count(*)` op beide kanten én de dekking
  over de entiteiten die de pagina toont; is de opvolger nog dun, dan een
  expliciet EINDIGE brug (union + fallback) mét het criterium waarop hij weg
  mag in de code, niet een "tijdelijke" union die er over een jaar nog zit.
  Bijvangst uit dezelfde ronde: een keten-stap mag niet klakkeloos een
  bestaande job hergebruiken als die job een ándere kostenprofiel-modus heeft —
  `rules` draait `force:true` (volledige her-chunk + her-embed, bedoeld na een
  parser-verbetering) en zou in de nachtelijke keten élke nacht de complete
  regelindex herbouwen; de keten hoort de incrementele `rules-index` te
  gebruiken.
- **Test-fixtures buiten de `rb-api/`-Docker-context breken pas de publish,
  niet de CI-testgate** (#238) — de CI-`test`-job draait `dotnet test` búiten
  Docker, dus een csproj-`<None Include>` die naar een pad búiten `rb-api/`
  wijst (bv. `..\..\docs\…`) slaagt daar, maar de `publish`-stap (die `dotnet
  test` ín de Dockerfile draait met context `rb-api/`) faalt met MSB3030
  ("could not copy … not found"). Publish draait alléén op main, dus het valt
  pas ná de merge op — de deploy wordt dan terecht overgeslagen (prod blijft
  intact). Houd test-fixtures binnen `rb-api/` (bv. `RbRules.Tests/Fixtures/`);
  verifieer twijfelgevallen met een lokale `docker build` van rb-api.

## Waar het werk staat
- Roadmap: **docs/PRD.md §6** (uit de open issues, in-flight PR's
  gemarkeerd) — dat is de actuele bron, niet dit bestand. Handoff: **#60**.
- Na elke deploy met datamigraties: admin → "Alles bijwerken" (en drafts
  reviewen bij primer-wijzigingen).
