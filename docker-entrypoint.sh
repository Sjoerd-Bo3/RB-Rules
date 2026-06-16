#!/bin/sh
# Bij start: schema toepassen + bronnen-register syncen (idempotent), dan de app.
set -e

echo "→ db:init (schema + bronnen-register)…"
npm run db:init || echo "⚠ db:init faalde (DB nog niet bereikbaar?) — ga toch door"

exec "$@"
