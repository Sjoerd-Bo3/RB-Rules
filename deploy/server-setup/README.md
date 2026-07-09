# RB-Rules op de Server-setup VM (riftbound.bo3.dev)

Past in je bestaande patroon: eigen `compose/rb-rules/`, achter de centrale Caddy,
image uit GHCR met Watchtower-auto-update. Geen eigen Caddy, geen VM-builds.

## Eenmalig

**1. Image laten bouwen (GitHub Actions).**
De workflow `.github/workflows/docker-publish.yml` in de RB-Rules-repo bouwt bij
push naar `main` → `ghcr.io/sjoerd-bo3/rb-rules:latest`. Merge de PR naar main (of
draai de workflow handmatig) en wacht tot het image er staat. Zet het GHCR-package
op **public**, of zorg dat de VM GHCR-auth heeft (zoals voor je Nocturne-fork).

**2. DNS.** A-record: `riftbound.bo3.dev → 20.123.137.64`.

**3. Bestanden naar de VM.**
```bash
scp -r deploy/server-setup sjoerd@20.123.137.64:~/compose/rb-rules
ssh sjoerd@20.123.137.64
cd ~/compose/rb-rules
cp .env.example .env
```

**4. `.env` invullen.** `POSTGRES_PASSWORD`, `ADMIN_PASSWORD` (en optioneel
`CLAUDE_CODE_OAUTH_TOKEN`/`VOYAGE_API_KEY` voor Q&A). En `CADDY_NETWORK`:
```bash
docker inspect caddy -f '{{json .NetworkSettings.Networks}}'   # → netwerknaam
```

**5. Data-map + start.**
```bash
sudo mkdir -p /mnt/data/postgres/rb-rules /mnt/data/neo4j/rb-rules
docker compose --project-name rb-rules up -d        # app + postgres
# (later, voor GraphRAG:)  docker compose --project-name rb-rules --profile graph up -d
```

**6. Caddy-route toevoegen.** Plak het blok uit `Caddyfile.snippet` in
`~/compose/caddy/Caddyfile` en herlaad:
```bash
docker exec caddy caddy reload --config /etc/caddy/Caddyfile
```
→ https://riftbound.bo3.dev (beheer op `/admin`).

**7. Lokaal embedding-model trekken** (gratis; nodig voor Q&A):
```bash
docker compose --project-name rb-rules exec ollama ollama pull nomic-embed-text
```

**8. Eerste vulling.**
```bash
docker compose --project-name rb-rules run --rm app npm run ingest
docker compose --project-name rb-rules run --rm app npm run sync:cards   # + card-graph
```

## Push-notificaties (optioneel)
Genereer VAPID-sleutels en zet ze in `.env` (`VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY`
/ `VAPID_SUBJECT`):
```bash
docker compose --project-name rb-rules run --rm app npx web-push generate-vapid-keys
```
Daarna `docker compose --project-name rb-rules up -d`. Spelers zetten push aan met
de 🔔-knop; test via /admin → "Testnotificatie sturen".

## Cron (op de VM)
```
0 7 * * *  cd ~/compose/rb-rules && docker compose --project-name rb-rules run --rm app npm run ingest     >> ~/rb-ingest.log 2>&1
0 6 * * 1  cd ~/compose/rb-rules && docker compose --project-name rb-rules run --rm app npm run sync:cards  >> ~/rb-cards.log 2>&1
0 8 * * 1  cd ~/compose/rb-rules && docker compose --project-name rb-rules run --rm app npm run digest      >> ~/rb-digest.log 2>&1
```

## Updaten
Vanzelf: push naar `main` → GH Actions bouwt → **Watchtower** pullt binnen het uur
en herstart `rb-rules-web`. Handmatig forceren:
```bash
docker compose --project-name rb-rules pull && docker compose --project-name rb-rules up -d
```

## Resourcenotie
De VM draait al Nocturne ×2 + SurfSense + Portfolio. RB-Rules' app + Postgres zijn
licht; **Neo4j staat daarom achter het `graph`-profiel** (uit by default). Zet 'm
pas aan als je de GraphRAG-laag gebruikt, en houd `htop`/`df -h` in de gaten.
