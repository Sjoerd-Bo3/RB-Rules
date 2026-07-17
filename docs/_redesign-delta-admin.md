# Redesign-delta — Beheer (#214)

Korte delta van de beheer-herbouw naar het goedgekeurde "Domains, eigentijds"-
design. Consolideer dit zelf in ARCHITECTURE.md/PRD.md — die zijn hier bewust
niet aangeraakt (evenmin app.css of de globale `+layout.svelte`).

## Nieuwe bestanden

### `rb-web/src/routes/admin/+layout.svelte`
Eigen beheer-shell (console) die de publieke zijbalk visueel vervangt binnen de
`/admin`-routes:
- Zijbalk met "← naar de site", merk "Riftbound [beheer]", nav met tel-badges,
  Gevarenzone in rood, en de thema-schakelaar onderaan.
- Nav-items linken naar echte bestemmingen: Overzicht (`/admin`), Jobs & paden
  (`/admin#jobs`), Reviewqueue (`/admin/overview/relaties`), Bronnen
  (`/admin#bronnen`), Kosten (`/admin/overview/gebruikers`), Vraag-traces
  (`/admin#traces`), Gevarenzone (`/admin#gevarenzone`).
- **Tel-badges** komen uit de al geladen `page.data` (status.counts.openCorrections
  → Reviewqueue; sources.length → Bronnen). Geen extra fetch; op detail-
  overzichten zonder die data tonen we simpelweg geen badge (nette degradatie).
- **Thema-schakelaar** hergebruikt de bestaande `useShell()`-store (alleen lezen/
  togglen — de store zelf is niet gewijzigd).
- Mobiel (<760px): eigen bovenbalk + hamburger → slide-over drawer met scrim,
  zelfde patroon als de publieke shell.
- **Onderdrukken van de globale chrome**: bij `onMount` wordt `admin-shell` op
  `<html>` gezet (en bij `onDestroy` verwijderd). De `:global`-regels in dit
  component (gated op `html.admin-shell`) verbergen de publieke `.sidebar`,
  `.topbar` en `.site-footer` en zetten de globale `.shell`-grid op één kolom.
  Zo blijft `+layout.svelte` ongewijzigd én lekt de onderdrukking nooit buiten
  het beheer (geverifieerd: terug naar `/` herstelt de publieke zijbalk).

### `rb-web/src/routes/admin/+layout.server.ts`
Levert alleen `{ authed }` aan de shell (volledige nav bij ingelogd, alleen merk-
chrome rond het login-scherm zo niet).

## Gewijzigde bestanden

### `rb-web/src/routes/admin/+page.server.ts`
- Extra parallelle fetch toegevoegd aan de bestaande `Promise.all`: graph-drift
  (`/api/admin/overview/gaps` → `.drift`) voor de Overzicht-drifttabel. Parallel,
  dus geen extra wachttijd; `.catch(() => null)` degradeert netjes. `drift` toegevoegd
  aan alle drie de return-vormen (unauthed / success / catch).
- **Geen** andere data-bindings of form-actions gewijzigd.

### `rb-web/src/routes/admin/+page.svelte` (volledig herbouwd, script-logica behouden)
- Alle interfaces, `$derived`/`$state`, JOBS/PATHS/TILES, live-polling, lazy
  dossiers/keyword-bewijs/trace-detail, en **alle form-actions** (login, logout,
  job, path, verify/reject/deleteCorrection, approve/deleteKnowledge,
  accept/rejectMechanic, toggle, ignore/unignoreSource, rescanSource) 1-op-1
  behouden.
- Nieuwe markup naar de mockup:
  - **Overzicht**: "Nu bezig"-paneel met live-indicator + indeterminate
    voortgangsbalk (er is geen numeriek %, dus geen nep-percentage; onder
    `prefers-reduced-motion` een statisch gevulde balk) + `running.progress`;
    pad-knoppen (`.pathbtn`, actieve = `.hot`); telling-tegels met domein-
    accentstreep en `.tnum`; rapport-links (gaten/setdekking/benchmark);
    graph-drift-tabel (uit de nieuwe `drift`-fetch) + recente runs (uit
    `jobRuns`).
  - **Bronnen** (`#bronnen`): source-cards met trust-badge (`trustLabel`),
    cadence-chip, herkomst, "levert niets op — negeren?"-kandidaat-hint,
    Actief-toggle, Negeer-met-reden, dossier-uitklap met rescan; genegeerde
    bronnen achter de "Genegeerd (N)"-knop met Terugzetten.
  - Reviewqueue/mechaniek-kandidaten/primer/traces/logs/gevarenzone: zelfde
    functionaliteit, herstyled in de nieuwe tokens.
  - Login-scherm: nette gecentreerde kaart binnen de shell.
- Alleen `var(--…)`-tokens; `.tnum` op tellingen/datums; brede tabellen in
  `.table-wrap`.

### `rb-web/src/routes/admin/overview/[kind]/+page.svelte`
- **Relatie-reviewqueue** (mockup-scherm 2) aangevuld: een informatieve
  aanbevelingsstrip (`.rec-strip` met accept/reject/unsure-tellingen uit
  `recommendationCounts`) en de bestaande bulk-acties herstyled als gekleurde
  `.bulkbar`-balken mét de fence-noot "groep ongewijzigd sinds laden". De strip
  is bewust géén filter (rb-api filtert op status, niet op aanbeveling — geen
  verzonnen endpoint); de fence blijft server-afgedwongen (asOf/expectedCount).
- Nieuwe `$derived` `unsureGroup` (naast de bestaande accept/reject) voor de
  twijfel-telling in de strip.
- Alle overige kinds en form-actions ongewijzigd. De pagina erft automatisch de
  nieuwe beheer-shell via `admin/+layout.svelte`.

## Gewenste tokens (niet toegevoegd)
Geen. Alles is met bestaande `app.css`-tokens gelukt (neutralen, `--accent(-ink/-soft)`,
`--ok/--warn/--err(+ -soft)`, `--dom-*`, `--radius(-lg)`, `--shadow-card`,
`--border(-strong)`). `color-mix(...)` op bestaande tokens waar tinten nodig waren.

## Afwijking van de mockup
- De mockup toont per bron een **contentKind**-chip (patch-notes/faq/other); die
  data zit niet op de sources-lijst (noch op het dossier). Om geen data te
  verzinnen tonen we de reële **cadence** als neutrale chip. Als contentKind
  gewenst is, moet rb-api dat eerst op `/api/admin/sources` meeleveren (aparte
  issue).
- De mockup-nav is een schone multi-sectie-IA; de echte app is één lange
  `/admin`-pagina + `/admin/overview/[kind]`. De nav mixt daarom in-page-ankers
  (Jobs/Bronnen/Traces/Gevarenzone) met echte subpagina-links (Reviewqueue-
  relaties, Kosten) — geen routes verzonnen.

## Verificatie
- `npm run check`: 0 errors, 0 warnings.
- `npm run build`: groen.
- `npx vitest run`: 62 passed (incl. `reviewBulk.test.ts`).
- Visueel (stub-API + login): Overzicht/Relaties/Bronnen op 1280 licht+donker,
  mobiel 390 (top bar + drawer). Horizontale overflow overal 0; publieke shell
  correct hersteld bij terugkeer naar `/`.
