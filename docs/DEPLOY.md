# Deploy — Azure-VM (publiek) én Mac mini (lokaal)

Beide draaien dezelfde gecontaineriseerde stack (`docker compose`). Verschil zit
alleen in het `public`-profiel (Caddy + TLS) en de geheugeninstellingen.

## Gemeenschappelijk
```bash
git clone <repo> RB-Rules && cd RB-Rules
cp .env.example .env
# vul .env: NEO4J_PASSWORD, DOMAIN (alleen Azure), en AI-auth (zie docs/AI_AUTH.md)
```
Bij `docker compose up` past de app automatisch het schema toe en synct het
bronnen-register (`docker-entrypoint.sh` → `npm run db:init`).

---

## Azure-VM (B2ms, ~8 GB) — publiek met HTTPS

1. **Netwerk (NSG):** open inkomend **80** en **443**. Laat 3000/5432/7474/7687
   dicht — die binden al op `127.0.0.1` (niet publiek).
2. **DNS:** A-record van je domein → publiek IP van de VM. Zet hetzelfde domein
   in `.env` als `DOMAIN`.
3. **Geheugen:** de defaults in `.env.example` (Neo4j heap 2g, pagecache 1g)
   passen op 8 GB. Niks aan te doen.
4. **Starten (met TLS):**
   ```bash
   docker compose --profile public up -d --build
   ```
   Caddy regelt automatisch een Let's Encrypt-certificaat voor `DOMAIN`.
5. **Check:** `https://<jouw-domein>` → wijzigingen-feed. PWA installeerbaar.

---

## Mac mini — lokaal (en optioneel op afstand)

1. **Starten:**
   ```bash
   docker compose up -d --build
   ```
   App op `http://localhost:3000` (de `public`/Caddy-service blijft uit).
2. **Geheugen:** 8 GB+ → defaults zijn prima. Kleinere mini? Zet in `.env`
   `NEO4J_HEAP_MAX=1g` en `NEO4J_PAGECACHE=512m`.
3. **Op afstand benaderen + HTTPS (voor PWA-camera/mic):** een Mac mini thuis
   heeft meestal geen publiek IP — gebruik **Cloudflare Tunnel** (gratis):
   ```bash
   brew install cloudflared
   cloudflared tunnel --url http://localhost:3000
   ```
   Geeft een HTTPS-URL zonder poorten/IP bloot te stellen.

---

## Dagelijkse scan (change-tracker) automatiseren
Cron op de host (draait de ingest in een wegwerp-container):
```
0 7 * * *  cd /pad/naar/RB-Rules && docker compose run --rm app npm run ingest >> ingest.log 2>&1
```

## Updaten
```bash
git pull
docker compose up -d --build          # Mac mini
docker compose --profile public up -d --build   # Azure
```

## Handige checks
```bash
docker compose ps                     # status van de services
docker compose logs -f app            # app-logs
docker compose run --rm app npm run ingest   # handmatige scan
```
