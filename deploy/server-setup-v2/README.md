# RB-Rules v2 op de Server-setup VM

Draait **naast** de PoP tot pariteit (S0–S3); daarna wissel je de Caddy-route om.

## Setup
```bash
scp -r deploy/server-setup-v2 sjoerd@20.123.137.64:~/compose/rb-rules-v2
ssh sjoerd@20.123.137.64
cd ~/compose/rb-rules-v2 && cp .env.example .env   # vul in
sudo mkdir -p /mnt/data/postgres/rb-rules-v2 /mnt/data/neo4j/rb-rules-v2 /mnt/data/ollama/rb-rules-v2
docker compose --project-name rb-rules-v2 up -d
docker compose --project-name rb-rules-v2 exec ollama ollama pull bge-m3
```

## Caddy-route
Testfase (subdomein):
```
riftbound-v2.bo3.dev {
	encode gzip
	header {
		Strict-Transport-Security "max-age=31536000"
		X-Content-Type-Options nosniff
		X-Frame-Options DENY
		Referrer-Policy strict-origin-when-cross-origin
		# CSP-voorstel (issue #45) — bewust nog NIET actief; eerst het
		# testplan hieronder doorlopen (start met Report-Only). Gebaseerd op
		# een inventarisatie van wat de SvelteKit-app echt nodig heeft
		# (2026-07-12, live geverifieerd):
		#  - script-src 'unsafe-inline': SvelteKit-SSR zet per pagina één
		#    inline hydration-script neer (dynamische import van /_app-
		#    modules). Zonder nonce (kit.csp, zie hieronder) kan dit niet weg.
		#  - style-src 'unsafe-inline': style-attributen via Sveltes
		#    style:-directive (o.a. /graph-legenda) en app.html
		#    ("display: contents"); attributen zijn niet te noncen.
		#  - img-src: kaart-art komt van cmsassets.rgpub.io (Riot-CMS),
		#    de favicon is een data:-URI, de board-state-fotopreview op
		#    /ask is een blob:-URL. Riot kan bij een nieuwe set van CDN-host
		#    wisselen — breekt kaart-art na een set-release, verruim dan
		#    naar https: (en noteer de nieuwe host).
		#  - connect-src/worker-src/manifest-src 'self': alle fetches lopen
		#    via server-loads en +server.ts-proxy's (zelfde origin), de PWA
		#    (service worker + manifest) is eigen aanbod.
		#  - Geen externe fonts/scripts; form actions posten naar eigen
		#    origin; frame-ancestors 'none' vervangt X-Frame-Options.
		# Sterkere variant later: kit.csp in rb-web (SvelteKit genereert dan
		# nonces voor het hydration-script) zodat 'unsafe-inline' uit
		# script-src kan; de header verhuist dan naar de app en moet hier weg
		# (dubbele CSP = strengste wint, dat debugt beroerd).
		#
		# Content-Security-Policy "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https://cmsassets.rgpub.io; font-src 'self'; connect-src 'self'; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'"
	}
	reverse_proxy rb-v2-web:3000
}
```
Bij pariteit: wijs `riftbound.bo3.dev` naar `rb-v2-web:3000` en zet de PoP uit.

### CSP-testplan (vóór het aanzetten)
1. Zet de regel eerst als `Content-Security-Policy-Report-Only "…"` in de
   route (zelfde waarde) en reload Caddy — dan logt de browser overtredingen
   zonder iets te breken.
2. Loop met de devtools-console open alle kernflows na: home (push-toggle),
   /cards + kaartdetail (kaart-art = rgpub-host, similar-why), /ask mét
   foto-upload (blob-preview) en streaming-antwoord, /rules (PDF-deeplinks
   openen extern — geen CSP-impact), /graph (style:-kleuren), /account
   (passkeys), /admin (jobs + live voortgang).
3. PWA expliciet: hard-reload → Application-tab: service worker geregistreerd,
   manifest geladen, installeerbaar; daarna een web-pushmelding testen.
4. Playwright-screenshotrun op 390/768/1280 (PR #47-aanpak) als
   regressiecheck naast de handmatige ronde.
5. Eén week schone Report-Only-logs → header omzetten naar afdwingend
   (`Content-Security-Policy`), zelfde flows nog één keer na.
6. Na elke nieuwe set-release even kaart-art checken (CDN-host-risico
   hierboven).

## Datamigratie vanaf de PoP
De v2-tabellen spiegelen het PoP-schema (snake_case, zelfde namen). Migratie:
```bash
docker exec rb-rules-postgres pg_dump -U rbrules --data-only \
  -t source -t document -t change -t conflict -t correction \
  -t card_set -t card -t rule_chunk -t run_log -t push_subscription rbrules \
  | docker exec -i rb-v2-postgres psql -U rbrules rbrules
```
Let op: embeddings NIET meenemen (ander model/dimensie in v2) — kolommen worden
opnieuw gevuld door de v2-embed-pijplijn.

## NEO4J_PASSWORD roteren
Het wachtwoord leeft op **drie** plekken: in de Neo4j-datadir
(`/mnt/data/neo4j/rb-rules-v2` — dit is het échte wachtwoord), in de
VM-`.env` (rb-api leest het bij start) en als GitHub Secret `NEO4J_PASSWORD`
(alleen gebruikt door een bootstrap-run van `v2-deploy.yml`, die de `.env`
overschrijft). Alle drie moeten mee, in deze volgorde:

1. **Check dat er geen admin-job draait** (werkafspraak: een lopende
   graph-sync raakt Neo4j en verliest zijn verbinding).
2. **Wijzig het wachtwoord in Neo4j zelf** (met spatie vóór het commando,
   zodat het oude/nieuwe wachtwoord niet in de shell-history belandt):
   ```bash
    docker exec rb-v2-neo4j cypher-shell -d system -u neo4j -p 'OUD' \
      "ALTER CURRENT USER SET PASSWORD FROM 'OUD' TO 'NIEUW';"
   ```
   Vanaf nu faalt de graph-kant van rb-api (best-effort: site en /ask
   blijven werken, /graph en graph-sync loggen fouten) — dat is verwacht
   tot stap 4.
3. **`.env` op de VM bijwerken**: `NEO4J_PASSWORD=NIEUW` in
   `~/compose/rb-rules-v2/.env`.
4. **rb-api en neo4j herstarten** zodat beide de nieuwe env lezen (rb-api
   houdt de driver als singleton — env wordt alleen bij start gelezen):
   ```bash
   docker compose --project-name rb-rules-v2 up -d rb-api neo4j
   ```
5. **Verifiëren**:
   ```bash
    docker exec rb-v2-neo4j cypher-shell -u neo4j -p 'NIEUW' "RETURN 1;"
   docker logs rb-v2-api --since 2m | grep -i neo4j   # geen warnings
   ```
   En in de site: /graph laadt een buurt-weergave.
6. **GitHub Secret `NEO4J_PASSWORD` bijwerken** (repo → Settings → Secrets):
   anders zet een toekomstige bootstrap-run het óúde wachtwoord terug in de
   `.env` terwijl Neo4j al het nieuwe heeft.

Valkuilen:
- `NEO4J_AUTH` in compose seedt het wachtwoord **alleen bij een lege
  datadir** — alléén `.env` wijzigen verandert een bestaande database dus
  niet; stap 2 is het echte roteren.
- Vermijd `'` en `$` in het nieuwe wachtwoord (quoting door drie lagen:
  shell → cypher-shell → compose-interpolatie).
- Nooit de datadir (`/mnt/data/neo4j/rb-rules-v2`) weggooien om "opnieuw te
  seeden" — dan is de hele graph weg en moet een volledige graph-sync
  draaien.

## Auto-deploy
Push naar `main` → `v2-ci.yml` draait **eerst de tests** en publiceert dan
`ghcr.io/sjoerd-bo3/rb-rules-{rb-api,rb-web,rb-ai}` met de tags `:latest` én
`:<commit-SHA>` → `v2-deploy.yml` synct de compose-file, pullt en herstart de
stack via SSH en verifieert de health-endpoints. De deploy **pint de
commit-SHA** van de publish die hem triggerde (IMAGE_TAG-export op het
SSH-commando), zodat een :latest-race tussen parallelle publishes nooit meer
bepaalt wat er uitrolt; handmatig `docker compose up -d` op de VM valt terug
op `:latest`. Deploys én publishes zijn geserialiseerd (concurrency-groups
`v2-deploy` en `v2-publish-*`). Watchtower staat op deze services expliciet
**uit** — push-to-deploy is het enige updatemechanisme (issue #45).
