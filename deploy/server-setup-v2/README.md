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
	reverse_proxy rb-v2-web:3000
}
```
Bij pariteit: wijs `riftbound.bo3.dev` naar `rb-v2-web:3000` en zet de PoP uit.

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

## Auto-deploy
Push naar `main` → `v2-ci.yml` draait **eerst de tests** en publiceert dan
`ghcr.io/sjoerd-bo3/rb-rules-{rb-api,rb-web,rb-ai}:latest` → Watchtower pakt ze op.
