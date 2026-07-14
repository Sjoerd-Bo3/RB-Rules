# RB-Rules — projectcontext voor Claude

## Wat dit is
Riftbound (League of Legends TCG) Rules Companion van Sjoerd (GitHub
`Sjoerd-Bo3`). Eén altijd-actuele bron voor regels, bans, errata, rulings en
kaarten — automatisch bijgehouden uit officiële bronnen, met een AI-vraagbaak.
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
1. **Nederlands** antwoorden; Engelse speltermen onvertaald.
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
  wél vanaf de VM) vult alleen aan en raakt bestaande kaarten niet aan —
  riftcodex-eerst conserveerde naamschade (#150). Riftcodex-site/Mobalytics
  blokkeren datacenter-IP's (Cloudflare).
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
  http/https- of www-variant per ongeluk een bewust-gedeelde bron op.

## Waar het werk staat
- Roadmap: **docs/PRD.md §6** (uit de open issues, in-flight PR's
  gemarkeerd) — dat is de actuele bron, niet dit bestand. Handoff: **#60**.
- Na elke deploy met datamigraties: admin → "Alles bijwerken" (en drafts
  reviewen bij primer-wijzigingen).
