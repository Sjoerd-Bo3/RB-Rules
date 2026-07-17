# Redesign-delta — publieke long-tail-routes (#214)

Losse notitie voor Sjoerd; consolideer zelf in ARCHITECTURE.md/PRD.md. Alle
wijzigingen gebruiken uitsluitend bestaande `var(--…)`-tokens uit `app.css` —
**geen nieuwe tokens nodig**, geen hardcoded hex toegevoegd (de bewuste `#fff`
op de rode danger-knop in `/account` is thema-veilig en blijft staan).

Design-systeem, shell, `app.css` en de al-afgeronde referentiepagina's zijn
**niet** aangeraakt.

## Per route

### `rules/+page.svelte` — regels-browser (index)
- Filter-rail (desktop) / bottom-sheet (mobiel) toegevoegd via `shell.rail`
  (`kind:'filters'`), volgens het feed-patroon. Filter = **Bron** (chips per
  `TocSource`), alleen gemount bij >1 bron.
- Actief bron-filter als verwijderbare chip in de content; boom filtert
  client-side op de gekozen bron. Semantisch zoeken en de §-permalinks
  ongewijzigd. Sectie-accordions kregen `--shadow-card` (sectiekaarten).

### `rulings/+page.svelte` — rulings-databank
- Onderwerp-type-filter verhuisd van een inline chip-rij naar de filter-rail /
  sheet (`kind:'filters'`, count = topic gezet). Actief filter als
  verwijderbare chip in de content. Items zijn nu `.panel`; tellingen/datums
  `.tnum`. Zoeken en paginering ongewijzigd.

### `decks/+page.svelte` — deck-browser (index)
- Domein- + sorteer-filters verhuisd naar de filter-rail / sheet als
  `.filter-form` (zelfde vorm als `/cards`). Actief domein-filter als
  verwijderbare chip; kaart-filter-banner blijft in de content. Deckkaarten
  zijn `.panel`; stats/count `.tnum`.

### `cards/[id]/+page.svelte` — kaartdossier
- Contextuele leesrail ("Op deze pagina", `kind:'context'`) met ankers naar de
  aanwezige dossier-secties (tekst, mechanieken, in-decks, interacties, regels,
  rulings, community-inzichten, relaties, ban-historie, vergelijkbaar) + een
  "Domein"-blok met getinte chip(s). Section-`id`'s toegevoegd waar ze nog
  ontbraken (`tekst`, `mechanieken`, `interacties`, `regels`, `vergelijkbaar`).
- **Domein-getint** via `domainColorVar(c.domains[0])` → `--card-dom`: 3px
  domein-rand boven de kaartafbeelding; domein-chips (ook de gedeelde-domein-
  chips bij "vergelijkbaar") getint met hun eigen `--dom-*` via `color-mix`
  i.p.v. de oude `--warn`-kleur.

### `decks/[id]/+page.svelte` — deck-detail
- Kleine domein-streep (3px, `domainColorVar(deck.domains[0])`) boven de
  decktitel; verder consistent gehouden (tokens/panels ongewijzigd).

### `primer/+page.svelte` — spelbegrip (leesdocument)
- Concept-TOC verhuisd van inline chips naar een contextuele rail
  ("Concepten", `kind:'context'`); leeskolom rustiger/breder (`max-width` 720,
  `line-height` 1.7).

### `account/+page.svelte` + `account/verify/+page.svelte`
- Al op-design; enige wijziging: `.tnum` op de drie "Gebruik vandaag"-regels.
  De bewuste `#fff` op de danger-knop is in beide thema's gecontroleerd op
  contrast (rood `--err`) en blijft staan.

## Verificatie
- `npm run check` 0 errors · `npm run build` groen · `npx vitest run` 62/62.
- Playwright-screenshots (390/768/1280, licht+dark) van alle herziene routes:
  **0 horizontale overflow** op alle 47 shots; filter-sheets openen en chips
  wrappen; logged-in `/account` + danger-knop gecontroleerd. Bewijs:
  `scratchpad/design-proof/longtail/`.
