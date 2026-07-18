# rb-web Style Contract — "Domains, eigentijds" (#214)

De bron van waarheid voor de visuele consistentie van de redesign. Elke route
gebruikt **uitsluitend** de tokens uit `src/app.css` — geen hardcoded hex.
Referentie-mockups: `scratchpad/rb-designs.html` (publiek), `rb-admin.html`
(beheer).

## 1. Tokens (app.css)

**Neutralen** — licht is standaard, donker is koele graphite (geen zwart). Op
drie niveaus gezet: `:root` (licht) · `@media (prefers-color-scheme: dark)` ·
`:root[data-theme="light"|"dark"]` (expliciete keuze wint in beide richtingen).

| rol | token | licht | donker |
|-----|-------|-------|--------|
| grond | `--bg` | `#f6f7f9` | `#0f1114` |
| paneel/kaart | `--surface` | `#ffffff` | `#181b21` |
| subtiel vlak | `--surface-deep` | `#eef0f3` | `#20242b` |
| tekst | `--text` | `#1b1e26` | `#e9ebf1` |
| gedimd | `--muted` | `#606673` | `#9aa0ad` |
| lijn | `--border` | `rgba(20,24,40,.11)` | `rgba(255,255,255,.10)` |
| lijn sterk | `--border-strong` | `rgba(20,24,40,.20)` | `rgba(255,255,255,.20)` |

**Actie/merk** — geel, ALLEEN voor primaire acties/actieve staat, nooit als
sfeerkleur: `--accent` `#f5c518`, `--accent-ink` `#20190a`, `--accent-soft`.

**Semantiek** (los van domein/merk): `--ok` `#2ea36a` (officieel/ok-stip),
`--err` (severity/gevaar; licht `#e5484d`, donker `#ff6169`), `--warn`.
Tint-achtergrond via `color-mix(in srgb, var(--x) 12-16%, transparent)`.

**Domeinen** (één plek, gelijk in beide thema's): `--dom-fury` `#e5484d`,
`--dom-body` `#ef7a35`, `--dom-mind` `#3e7bfa`, `--dom-calm` `#2ea36a`,
`--dom-chaos` `#b05fd6`, `--dom-order` `#f5c518`, `--dom-colorless` `#8b91a0`.
In TS: `domainColorVar(name)` (`$lib/changeCard`) → `var(--dom-…)` met
Colorless-terugval.

**Vorm**: `--radius` 10px, `--radius-lg` 13px. Schaduw `--shadow-card` (zacht
op licht, `none` op donker). Cijfers die uitlijnen: klasse `.tnum`
(`font-variant-numeric: tabular-nums`) — verplicht op tellingen/datums/stats.

## 2. Componentpatronen

- **Shell** (`+layout.svelte`): vaste zijbalk 212px links (desktop) met merk +
  conic-gradient domein-mark, zoekveld, gegroepeerde nav (Actueel · Kennis),
  onderaan Account/Beheer + thema-schakelaar. Mobiel (<760px): bovenbalk +
  hamburger → slide-over drawer met scrim. Rechterrail vanaf 1080px.
- **Rail** (opt-in via `useShell().rail`): `kind:'context'` (leespagina's:
  "op deze pagina / bron / gerelateerd") of `kind:'filters'` (lijstpagina's).
- **Filteren**: NOOIT horizontaal scrollende chips. Desktop → rail; mobiel →
  "Filter"-knop met teller opent bottom-sheet waarin chips **wrappen**, met
  Reset + "Toon N". Actieve filters als verwijderbare chips in de content.
- **Kaart** (`.panel`/ChangeCard): `--surface` + `--border` + `--radius-lg` +
  `--shadow-card`. Wijzigingskaart: 4px domein-randstreep links, kop = severity-
  pill + kind-chip + domein-chip + bron (groene ok-stip) + datum rechts.
- **Chip/pill**: pill = `border-radius:999px`. Severity-pill = `color-mix` err-
  tint. Kind-chip = `--surface-deep`. Domein-chip = `color-mix` van `--dom-*`.
  Actieve filterchip = domein-tint + `--text`.
- **Domein-streep**: 3–4px rand/streep in `var(--dom-*)` (ChangeCard links,
  stat-tegel + kaart-tegel boven).
- **Sidebar-item**: 6px rounded-square domein-stip (decoratief) + label; actief
  = `--surface-deep` + `--text` + 600.
- **Stat-tegel / kaart-tegel**: `--surface` + domein-accentstreep + groot
  `.tnum`-getal.

## 3. Do / Don't

- DO: alleen tokens; `.tnum` op cijfers; brede tabellen/diagrammen in
  `overflow-x:auto`-container; 0 horizontale overflow op 390/768/1280.
- DO: status = kleur + tekst (badge/pill met label), nooit alleen kleur.
- DO: zichtbare focus (`:focus-visible` globaal in app.css); respecteer
  `prefers-reduced-motion`.
- DON'T: geen emoji; geen hardcoded hex in routes; geen horizontaal
  filterscrollen; geel niet decoratief gebruiken.
