#!/usr/bin/env bash
# Haalt Riots officiële kaarttekst-glyphs (rune's, energy-cijfers, might,
# exhaust) op en zet ze in rb-web/static/glyphs/. We tekenen deze iconen niet
# zelf: dit script neemt de bestanden byte-voor-byte over van Riots eigen CDN
# (#257). Her-ophalen als Riot de glyphs bijwerkt — het script is idempotent
# en overschrijft gewoon opnieuw.
#
# Bron: https://assetcdn.rgpub.io/public/live/riot-shared/player-experiences/riot-glyphs/rb/latest/
set -euo pipefail

BASE_URL="https://assetcdn.rgpub.io/public/live/riot-shared/player-experiences/riot-glyphs/rb/latest"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST_DIR="${SCRIPT_DIR}/../rb-web/static/glyphs"

# Geverifieerd: deze 22 bestanden geven 200 (energy_13 geeft 404 — de reeks
# stopt bij energy_12).
GLYPHS=(
	might
	exhaust
	rune_fury
	rune_calm
	rune_mind
	rune_body
	rune_order
	rune_chaos
	rune_rainbow
	energy_0
	energy_1
	energy_2
	energy_3
	energy_4
	energy_5
	energy_6
	energy_7
	energy_8
	energy_9
	energy_10
	energy_11
	energy_12
)

mkdir -p "$DEST_DIR"

for name in "${GLYPHS[@]}"; do
	url="${BASE_URL}/${name}.svg"
	dest="${DEST_DIR}/${name}.svg"
	echo "Ophalen: ${name}.svg"
	curl -sS -f -o "$dest" "$url"
done

echo "Klaar: ${#GLYPHS[@]} glyphs in ${DEST_DIR}"
