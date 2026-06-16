#!/usr/bin/env bash
# Turnkey deploy voor de Riftbound Rules Companion.
# Draai dit OP je server/Mac (niet vanuit de cloud-sessie).
#
#   ./deploy/deploy.sh            # lokaal (Mac mini): app op http://localhost:3000
#   ./deploy/deploy.sh public     # Azure + HTTPS via Caddy (vereist DOMAIN in .env)
#   FIRST_RUN=1 ./deploy/deploy.sh   # ook eerste scan + kaart-sync draaien
set -euo pipefail
cd "$(dirname "$0")/.."

PROFILE="${1:-local}"
COMPOSE="docker compose"
[ "$PROFILE" = "public" ] && COMPOSE="docker compose --profile public"

command -v docker >/dev/null 2>&1 || { echo "✗ Docker is vereist (Docker Desktop / Engine)"; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "✗ 'docker compose' v2 is vereist"; exit 1; }

if [ ! -f .env ]; then
  cp .env.example .env
  echo "→ .env aangemaakt uit .env.example."
  echo "  Vul minimaal in: ADMIN_PASSWORD, NEO4J_PASSWORD$([ "$PROFILE" = public ] && echo ", DOMAIN")."
  echo "  Optioneel (AI/Q&A): CLAUDE_CODE_OAUTH_TOKEN of ANTHROPIC_API_KEY, en VOYAGE_API_KEY."
  echo "  Vul .env en draai dit script opnieuw."
  exit 1
fi

echo "→ Build & start ($PROFILE)…"
$COMPOSE up -d --build

echo "→ Even wachten tot de app het schema heeft toegepast…"
sleep 6

if [ "${FIRST_RUN:-0}" = "1" ]; then
  echo "→ Eerste regel-scan…";   docker compose run --rm app npm run ingest     || true
  echo "→ Eerste kaart-sync…";   docker compose run --rm app npm run sync:cards || true
fi

echo
echo "✓ Klaar. Status:"
$COMPOSE ps
echo
if [ "$PROFILE" = "public" ]; then
  echo "→ Bereikbaar op https://\${DOMAIN} (open NSG 80/443, DNS A-record → VM-IP)."
else
  echo "→ Bereikbaar op http://localhost:3000  ·  beheer: /admin"
fi
echo "→ Dagelijkse scan (cron):  0 7 * * *  cd $(pwd) && docker compose run --rm app npm run ingest >> ingest.log 2>&1"
echo "→ Wekelijkse kaart-sync:   0 6 * * 1  cd $(pwd) && docker compose run --rm app npm run sync:cards >> cards.log 2>&1"
