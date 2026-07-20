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
   bij de officiële bewoording (docs/CONVENTIONS.md).
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
