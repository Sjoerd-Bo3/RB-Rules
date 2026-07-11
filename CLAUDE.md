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
  centrale Caddy, /mnt/data-binds). Compose-file wordt door de workflow
  gesynct. Watchtower draait ook nog (dubbel mechanisme — zie issue #45).

## Kernfeatures (allemaal live)
Wijzigingen-feed met bron + voor/na-diff en flip-flop-suppressie ·
regels-browser (/rules, hoofdstuk-hiërarchie, §-permalinks, PDF-deeplinks
`#page=N`) · kaartbrowser met facetten + semantisch zoeken ·
variantgroepering op basisnaam ("Naam (Alternate Art)" = zelfde kaart;
canoniek = naamloze printing) · kaartdetail met similar-why, versies,
gekoppelde regels/errata · /ask met vraag-router (Ruling/Definitie/Kaart/
Legaliteit/Toernooi → eigen structuur + bronnen-bias), scheidsrechter-format
(Oordeel → Zekerheid → Uitleg → Regelbasis → Let op), uitklap-citaties met
ouderregels, betrokken kaarten, board-state-foto's (vision), widget-markers
`[[rule:…]]`/`[[card:…]]` → interactieve blokken, echte duurstatistiek ·
kaarttekst-icoontokens (`:rb_energy_1:` e.d.) als échte iconen (rbtokens.ts)
· self-learning (feedback → reviewqueue → geverifieerde rulings semantisch
in de prompt) · game-primer (12 concepten, draft → approve in admin) ·
levendige admin (jobs met live voortgang, "Alles bijwerken"-keten,
vraag-traces, reviewqueues) · PWA + web-push (VAPID in .env) ·
graph-verkenner (/graph).

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

## Valkuilen uit de praktijk (duur betaald)
- Riot's domein is **playriftbound.com**; PDF-links zijn opake Sanity-CDN-
  hashes — matchen op ankertekst ("Core Rules"). Gallery-JSON bevat
  set-facetten en token-kaarten met lege type-lijst (regressietests bestaan).
- Riftcodex/Mobalytics blokkeren datacenter-IP's (Cloudflare) — Riot-gallery
  is de betrouwbare kaartenbron vanaf de VM.
- Rules Hub wisselt per request de volgorde van artikellinks →
  flip-flop-suppressie in IngestService (hash-historie + lege-diff-guard).
- adapter-node: form-POSTs vereisen `ORIGIN`-env lokaal; `BODY_SIZE_LIMIT`
  is gezet voor foto-upload.
- iOS zoomt op form-controls < 16px (app.css-fix aanwezig).

## Waar het werk staat
- **#55** masterplan-draaiboek · **#60** actuele handoff (stand + volgorde)
- Openstaand o.a.: #59 service-extractie ronde 2, #41 doorvragen, #31
  streaming/voorlezen, #42 accounts+quota (+rate-limit quick-win), #22
  set-legaliteit, #45 ops-hardening, #50–#53 kennisbank-lagen, #57
  varianten toekomstvast, #58 classificatie-backfill, #38-rest mobile.
- Na elke deploy met datamigraties: admin → "Alles bijwerken" (en drafts
  reviewen bij primer-wijzigingen).
