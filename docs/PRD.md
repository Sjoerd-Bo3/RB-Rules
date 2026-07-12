# Product Requirements Document — RB-Rules

Levend product-document voor de Riftbound Rules Companion (live op
<https://riftbound-v2.bo3.dev>). Het beschrijft *wat* het product is en voor
*wie*, wélke features vandaag op `main` staan, en waar het heen gaat. Het is
bewust gescheiden van het *hoe*: de architectuur staat in
`docs/ARCHITECTURE.md` (arc42, eveneens #134) met `docs/BRAIN.md` en
`docs/CONVENTIONS.md` als verdieping, de kennisvisie in `docs/KNOWLEDGE.md`.

Dit document is een anker: elke PR die features of gedrag wijzigt, werkt het in
dezelfde PR bij (zie het slothoofdstuk **Onderhoud**).

---

## 1. Visie & missie

**Missie.** Eén altijd-actuele, betrouwbare bron voor alles rond de regels van
Riftbound (League of Legends TCG): regels, bans, errata, rulings en kaarten —
automatisch bijgehouden uit officiële bronnen, ontsloten via een browsbare site
én een AI-vraagbaak die antwoordt als een scheidsrechter, met bronvermelding.

Waar losse fansites elk een stukje dekken (een banlijst hier, een rulings-tool
daar), is RB-Rules bewust één samenhangend systeem: dezelfde kennis voedt de
feed, de kaartpagina's, de regelbrowser én de vraagbaak, zodat een antwoord
altijd terugverwijst naar de officiële regel eronder.

**Einddoel (uit `docs/KNOWLEDGE.md` en `docs/BRAIN.md`).** Uitgroeien tot één
samenhangend "brein": alle kennis — regels, kaarten, mechanieken,
community-interpretatie, meta — zowel vector- (semantische nabijheid) als
graf-gelinkt (getypeerde relaties), ontsloten via een brein-API waarmee de
eigen ask-agent (en toekomstige AI-features) zélf redeneert, interacties
ontdekt en nieuwe features bouwt in plaats van een statische prompt te krijgen.

**Leidend principe — de kennispiramide.** Kennis is gelaagd en wordt in élk
koppelvlak expliciet gelabeld op betrouwbaarheid:

> officiële regels > geverifieerde rulings > primer (spelbegrip) >
> community-claims (met bron-trust en corroboratie) > meta/tactiek

Interpretatie mag een oordeel kleuren, nooit dragen. "Regelbasis" (officieel)
en "Community-consensus" blijven in het antwoord gescheiden.

**Evolutie is een eersteklas eis.** Elke nieuwe set brengt nieuwe kaarten,
mechanieken en community-kennis. Het systeem moet meegroeien: een set-release
is een event dat de hele keten triggert (card-sync → nieuwe-mechanieken →
claims-harvest → embeddings → graph-sync → primer-herziening), en het
mechaniek-vocabulaire groeit mee met de kaarten die verschijnen.

---

## 2. Doelgroepen & persona's

| Persona | Wie | Belangrijkste behoefte | Voornaamste oppervlak |
|---|---|---|---|
| **Speler** | Iemand die een potje speelt of leert | Snel een correct, begrijpelijk antwoord op een regelvraag; begrip van het spelverloop | `/ask`, `/rules`, `/primer` |
| **Judge / toernooiorganisator** | Scheidsrechter of TO | Een verdedigbaar oordeel mét regelbasis en zekerheids-label; actuele bans/errata; legaliteit | `/ask` (scheidsrechter-format), feed (`/`), `/rules` |
| **Deckbouwer / competitieve speler** | Speler die kaarten en interacties bestudeert | Kaarten vinden op eigenschap/betekenis, interacties en gelijkenissen zien, weten wat legaal is | `/cards`, `/cards/[id]`, `/graph` |
| **Beheerder / curator (Sjoerd)** | Eigenaar die de kennisbank onderhoudt | Overzicht en controle: jobs draaien, gemijnde kennis reviewen, antwoordkwaliteit en kosten volgen | `/admin` en subpagina's |

De **beheerder-persona is een volwaardig productoppervlak**, geen bijzaak: de
reviewqueues (claims, relaties, mechaniek-kandidaten, bronvoorstellen,
correcties) zijn de plek waar automatisch gemijnde kennis mens-geverifieerd de
piramide in stroomt. De kwaliteit van het publieke product hangt direct aan de
bruikbaarheid van dit beheeroppervlak.

---

## 3. Use-cases

Concrete scenario's per persona; ze verbinden de behoefte aan het oppervlak dat
hem vandaag bedient.

**Speler**
- *Ruling tijdens een potje.* "Kan ik met kaart X reageren op de showdown van
  Y?" → `/ask` geeft een oordeel met zekerheids-label, de betrokken kaarten als
  bewijs en de §-regelbasis met uitklap-citaat. Bij twijfel vraagt de speler
  door met behoud van context (#41).
- *Het spel leren.* Een beginner leest de game-primer (`/primer`) om
  beurtstructuur, resources en combat-flow te begrijpen voordat losse regels
  betekenis krijgen.
- *Regel opzoeken.* Via `/rules` door de hoofdstuk-hiërarchie bladeren of
  semantisch zoeken, en een §-permalink of PDF-deeplink (`#page=N`) delen.

**Judge / toernooiorganisator**
- *Verdedigbaar oordeel.* Het scheidsrechter-format (Oordeel → Zekerheid →
  Uitleg → Regelbasis → Let op) geeft niet alleen het antwoord maar de
  regelgrond en een eerlijke onzekerheidsmarge.
- *Legaliteit checken vóór een toernooi.* De feed (`/`) toont de laatste bans
  en errata met bron en voor/na-diff; de kaartpagina toont ban-status.
- *Board-state laten beoordelen.* Een foto van het speelveld uploaden bij een
  vraag; de vision-keten leest het bord en het antwoord redeneert erover.

**Deckbouwer**
- *Kaarten vinden op betekenis.* Semantisch zoeken ("alle gearkillers") en
  facetten (mechaniek/domein/tag/set) filteren de kaartbrowser.
- *Interacties en gelijkenissen begrijpen.* De kaartpagina toont "waarom lijken
  deze op elkaar", gekoppelde regels/errata en ontdekte interacties; de
  graph-verkenner laat kaart↔mechaniek↔regel-verbanden doorlopen.

**Beheerder / curator**
- *Kennis reviewen.* Nieuw gemijnde claims, relaties en mechaniek-kandidaten
  beoordelen in de reviewqueues — mét het bewijs (welke kaarten dragen een
  kandidaat, welke bronnen dragen een claim) — en accepteren of verwerpen.
- *Kennisbank bijwerken.* Na een deploy met datamigraties de "Alles
  bijwerken"-keten draaien en live de voortgang per stap volgen; primer-drafts
  goedkeuren.
- *Kwaliteit en kosten bewaken.* Vraag-traces bekijken (welke kennislagen
  deden mee, welke brein-stappen), de duurstatistiek en het
  kennis-gaten-rapport lezen om te sturen waar de volgende harvest heen moet.

---

## 4. Feature-inventaris (live op `main`)

Alles hieronder staat op `origin/main` en draait live. Per feature: wat het doet
en waar het leeft (route in rb-web / endpoint in rb-api). In-flight werk staat
apart in §6.

### 4.1 Regels & bronnen

- **Wijzigingen-feed** — de homepage toont automatisch gedetecteerde
  wijzigingen (bans, errata, regelupdates, set-releases) met bron, severity,
  voor/na-diff en een menselijke samenvatting/betekenis. Flip-flop-suppressie
  onderdrukt ruis van bronnen die per request de volgorde wisselen.
  *Route* `/` · *endpoints* `/api/changes`, `/api/sources`, `/api/bans`,
  `/api/sets/upcoming`.
- **Regels-browser** — hoofdstuk-hiërarchie van de Core/Tournament Rules met
  §-permalinks en PDF-deeplinks (`#page=N`), plus hybride (semantisch +
  full-text) zoeken door de secties.
  *Routes* `/rules`, `/rules/[code]` · *endpoints* `/api/rules/toc`,
  `/api/rules/search`, `/api/rules/section/{code}`.
- **Sectie-dossier** — per § de kennis die erop leunt: kaarten die naar de
  sectie verwijzen, primer-docs (EXPLAINS), community-claims en
  regelwijzigingen (AFFECTS, via het brein; Neo4j-uitval degradeert het
  dossier zonder changes). *Route* `/rules/[code]` · *endpoint*
  `/api/rules/section/{code}/dossier`.
- **Rulings-databank** — doorzoekbare collectie van geverifieerde correcties
  en officieel bevestigde claims, met trust-labels, bron en citaat per item,
  §-chips en permalink-anchors; hybride zoeken (best-effort embed → vector +
  FTS → RRF, degradatie naar alleen-FTS eerlijk gemeld).
  *Route* `/rulings` · *endpoint* `/api/rulings`.
- **Bans & errata** — gestructureerd opgeslagen per set, zichtbaar in de feed
  en gekoppeld aan kaarten. *Endpoint* `/api/bans`.

### 4.2 Kaarten

- **Kaartbrowser** — alle kaarten met facetten (mechaniek, domein, tag, set) en
  semantisch zoeken op betekenis; facetten zijn overal klikbaar naar een
  gefilterde browser. *Route* `/cards` · *endpoints* `/api/cards`,
  `/api/cards/facets`, `/api/cards/search`.
- **Kaartdetail** — het kaart-dossier: stats en tekst (met icoontokens),
  gekoppelde regels/errata, ontdekte interacties, en "similar-why" (semantische
  uitleg waarom kaarten op elkaar lijken) met versies/varianten.
  *Route* `/cards/[id]` · *endpoints* `/api/cards/{id}`, `/api/cards/{id}/rules`,
  `/api/cards/{id}/interactions`, `/api/cards/{id}/similar`,
  `/api/cards/{id}/similar/{otherId}/explain`.
- **Kaart-dossier (verdieping)** — op dezelfde pagina: geverifieerde rulings
  (kaart-scoped én naam-vermeldingen, met klikbare §-verwijzingen),
  geaccepteerde claims met trust-label en bron/citaat, relaties achter
  dezelfde reviewpoort als de graph-projectie, en de volledige ban-historie
  per variantgroep. *Endpoint* `/api/cards/{id}/dossier`.
- **Variantgroepering op basisnaam** — "Naam (Alternate Art)" telt als dezelfde
  kaart; de naamloze printing is canoniek, ook toekomstvast bij herdrukken
  (canonical-flip). Alleen canonieke printings gaan de graph in.
- **Kaarttekst-icoontokens** — tokens als `:rb_energy_1:` renderen als échte
  iconen (`$lib/rbtokens.ts`), veilig ge-escaped vóór injectie.

### 4.3 De vraagbaak (`/ask`)

- **Vraag-router** — classificeert de vraag (Ruling / Definitie / Kaart /
  Legaliteit / Toernooi) en kiest per type de antwoordstructuur en de
  bronnen-bias.
- **Scheidsrechter-format** — Oordeel → Zekerheid → Uitleg → Regelbasis → Let
  op, met de essentie in één oogopslag.
- **Citaties & bewijs** — uitklap-citaties tonen de regel plus de ouderregel
  voor context (#39); betrokken kaarten als bewijs; widget-markers
  `[[rule:…]]` / `[[card:…]]` worden interactieve blokken.
- **Board-state-foto's (vision)** — een foto meesturen; het antwoord redeneert
  over het herkende bord.
- **Streaming + voorlezen** — het antwoord komt woord voor woord binnen
  (NDJSON-proxy) met vangnet naar de niet-streamende route; een
  voorlees-knop leest het antwoord voor (speechSynthesis).
  *Routes* `/ask`, `/ask/stream` · *endpoints* `/api/ask`, `/api/ask/stream`.
- **Query-rewrite** — een goedkope voor-call normaliseert de zoekzin (typo's,
  synoniemen, NL→EN speltermen) vóór retrieval (#66).
- **Doorvragen met context** — follow-up-vragen behouden de volledige context
  (#41).
- **UX** — voorbeeldvragen, klikbare citaties, geschiedenis en denk-feedback
  (duim omhoog/omlaag, die de self-learning-loop voedt). *Endpoint*
  `/api/corrections`.
- **Echte duurstatistiek** — antwoordduur wordt gemeten (`ask_metric`) en
  getoond (count/gemiddelde/mediaan/P90). *Endpoint* `/api/ask/stats`.

### 4.4 Kennisbank / het brein

- **Kennispiramide** — officieel > geverifieerde rulings > primer > community >
  meta, expliciet gelabeld in de prompt en het antwoordformat.
- **Game-primer** — ~12 concept-docs (beurt, resources, combat, prioriteit,
  zones, scoren, keyword-gedrag) gedistilleerd uit de regels, met draft →
  approve in beheer; altijd als achtergrondblok mee in `/ask` en read-only op
  de site. *Route* `/primer` · *endpoint* `/api/knowledge`.
- **Claims-pipeline** — community-interpretatie als geparafraseerde claims met
  bron-trust, corroboratie (aantal onafhankelijke bronnen) en een officiële
  toets; geaccepteerde claims doen mee als eigen "Community-consensus"-kanaal in
  `/ask`.
- **Evolutie-raamwerk** — set-release-keten, groeiend mechaniek-vocabulaire
  (keyword-kandidaten → reviewqueue → re-mine) en een kennis-gaten-rapport dat
  meet waar de bank aantoonbaar niets weet.
- **Kennis-levenscyclus** — regelwijzigingen (change high/medium) hertoetsen de
  betrokken primer-docs en claims automatisch; embeddings en uitleg-cache
  invalideren al bij tekstwijziging.
- **Brein-API** — zes koppelvlakken over de unified vector+graph-representatie,
  met per resultaat een laag- en trust-label; compose-intern (browser komt er
  via rb-web-proxy's). *Endpoints* `/api/brain/search`, `/api/brain/node/{ref}`,
  `/api/brain/neighbors/{ref}`, `/api/brain/path`, `/api/brain/evidence/{ref}`,
  `/api/brain/contradictions`.
- **Agentic ask** — voor kwalificerende vragen (interactievraag met ≥2
  kaartnamen, of lege retrieval) mag rb-ai als agent over het brein redeneren
  via de brein-tools, achter een feature-flag met vangnet naar single-pass; de
  brein-stappen staan in de trace. Verbanden die de agent onderweg ontdekt
  komen als relatievoorstel in de reviewqueue (#120) — het brein verrijkt
  zichzelf al antwoordend, altijd achter de reviewpoort.
- **Dynamische relaties** — één generiek edge-type `RELATES_TO {kind, trust,
  explanation, status}` met een open, gereviewd vocabulaire; LLM-relaties gaan
  nooit rechtstreeks de graph in (Postgres is de bron, projectie na review).
- **Graph-verkenner** — interactieve kaart↔mechaniek↔regel-visualisatie.
  *Route* `/graph` · *endpoint* `/api/graph/neighbors`.
- **Self-learning** — negatieve/positieve feedback → reviewqueue →
  geverifieerde correcties (embedded, gezaghebbend) die semantisch terugkomen
  in de prompt.

### 4.5 Beheer (`/admin`)

- **Jobs met live voortgang** — de "Alles bijwerken"-keten en losse jobs
  (scan, cards, embed, mine, rules, bans, graph, primer, interactions, scout,
  classify, claims, relations, setrelease) draaien via `JobRunner` met
  live-voortgang en run_log. *Route* `/admin` · *endpoints* `/api/admin/jobs/{name}`,
  `/api/admin/status`, `/api/admin/logs`.
- **Aanklikbare status-tegels** — elke teller opent een overzichtspagina.
  *Route* `/admin/overview/[kind]` · *endpoints* `/api/admin/overview/{cards,
  rulechunks, bans, errata, interactions, changes, claims, proposals, relations,
  users, gaps}`.
- **Reviewqueues** — claims, relaties (+ kandidaat-kinds), mechaniek-kandidaten
  en bronvoorstellen accepteren/verwerpen, mét het bewijs per keuze (#123).
  *Endpoints* o.a. `/api/admin/claims/{id}/accept|reject`,
  `/api/admin/relations/{id}/accept|reject`,
  `/api/admin/relationkinds/{id}/accept|reject`,
  `/api/admin/mechanics/{id}/accept|reject`,
  `/api/admin/proposals/{id}/accept|reject`.
- **Vraag-traces** — per vraag welke kennislagen en brein-stappen meededen
  (#40). *Endpoint* `/api/admin/asktraces`.
- **Token-metering & kostenoverzicht** — echte input/output-tokens per vraag
  (rb-ai geeft usage door, geboekt op `ask_metric`), getotaliseerd per pad
  (cheap/hard/agentic) en per account in het kostenoverzicht (#121).
- **Periodieke zelfverrijking** — relatie-mining nachtelijk en de
  bronnen-scout wekelijks in de scheduler-tick, met job-gate,
  run_log-vensters en degradatiepaden (#122).
- **Kennis-gaten-rapport** — geclusterde onzekere/lege-retrieval-vragen sturen
  de volgende harvest. *Endpoint* `/api/admin/overview/gaps`.
- **Primer- & correctie-beheer** — drafts goedkeuren/intrekken; correcties
  verifiëren. *Endpoints* `/api/admin/knowledge/*`, `/api/admin/corrections/*`.
- **Bronnenbeheer** — bronnen met trust/rank toevoegen/verwijderen.
  *Endpoints* `/api/admin/sources`, `/api/admin/sources/{id}`.

### 4.6 Platform, accounts & PWA

- **PWA + web-push** — installeerbare app; web-push bij high-severity
  wijzigingen (VAPID). *Route* `/push` · *endpoints* `/api/push/vapid`,
  `/api/push/subscribe`, `/api/push/unsubscribe`.
- **Accounts** — login via e-mail (magic-link-verificatie) met per-gebruiker
  quota/kosteninzicht als basis; per-IP rate-limiting op de dure endpoints.
  *Routes* `/account`, `/account/verify` · *endpoints* `/api/auth/request`,
  `/api/auth/verify`, `/api/auth/me`, `/api/auth/logout`.
- **Passkeys (WebAuthn)** — inloggen zonder mailafhankelijkheid; passkeys
  beheren op de accountpagina. *Routes* `/account/passkey/*` · *endpoints*
  `/api/auth/passkey/register/*`, `/api/auth/passkey/login/*`,
  `/api/auth/passkeys`.
- **Mobile-first** — layout getest op 390/768/1280px; de iOS-auto-zoom op
  form-controls (< 16px) is opgelost via `app.css`.

---

## 5. Niet-functionele eisen

Bindende kwaliteitseisen; ze zijn uitgeschreven in `docs/CONVENTIONS.md` en
`CLAUDE.md`.

- **AI-/embedding-uitval is een verwacht pad, geen fout.** `RbAiClient` geeft
  `null` bij uitval en de aanroeper degradeert netjes (geen classificatie/geen
  uitleg — nooit een crash). `/ask` degradeert bij embedding-uitval naar
  FTS-only in plaats van een kale 500. Fouten zijn data: zichtbaar in run_log,
  job-detail of een Problem-response mét detail.
- **Pijplijnen zijn best-effort per stap.** Een haperende externe dienst
  (Ollama, rb-ai, Riot, Neo4j) stopt nooit de hele run; de fout wordt gelogd en
  de run gaat door. Neo4j-uitval degradeert per brein-koppelvlak; de site blijft
  volledig functioneel.
- **Mobiel eerst.** Getest op 390px met **0 horizontale overflow** (stub-API +
  Playwright-screenshots op 390/768/1280px).
- **Geen emoji's in de UI.** Serieus, strak ontwerp via de tokens in `app.css`
  (`var(--accent)` e.d.); geen hardcoded kleuren in nieuwe componenten. Status =
  kleur + tekst.
- **Trust-gelaagdheid expliciet gelabeld.** Officieel > geverifieerde rulings >
  primer > community > meta wordt in élk koppelvlak (prompt, antwoordformat,
  brein-tools, UI) benoemd; "Regelbasis" en "Community-consensus" blijven
  gescheiden. Interpretatie kleurt, draagt nooit.
- **Bron van waarheid is extern en Postgres is leidend.** Wij bewaren wat Riot
  publiceert; afgeleiden (embeddings, mechanics, Neo4j-projectie, uitleg-cache)
  zijn altijd herbouwbaar en invalideren bij bronwijziging.
  Embedding-provenance is heilig: model-wissel = expliciete her-embed, nooit
  stilzwijgend dimensies mengen.
- **Veiligheid & privacy.** Secrets nooit in code, logs of chat (GitHub Secrets
  / VM-`.env`); de browser praat nooit rechtstreeks met rb-api (server-loads of
  `+server.ts`-proxy's); niets ongesaniteerd in `{@html}`; rb-ai en de brein-API
  zijn alleen compose-intern bereikbaar.
- **Beheer-continuïteit.** Nooit mergen/deployen terwijl een live admin-job
  draait — de deploy herstart de container en breekt de job af. Lange operaties
  draaien via `JobRunner` (202/409 + voortgang), nooit synchroon in een request.
- **Kosten & latency onder controle.** AI-werk (harvest/mining) draait
  nachtelijk, batched en gecapt op een goedkoop model; agentic ask heeft
  maxTurns, een tool-call-cap en een harde timeout en staat standaard achter een
  flag.
- **CI is de poort.** `dotnet test` + `svelte-check` + `tsc` groen vóór images
  publiceren; elke productie-bug krijgt eerst een regressietest.

---

## 6. Roadmap

Uit de openstaande GitHub-issues, gegroepeerd op thema. **In-flight** =
openstaande PR.

**Kennisbank verdiepen**
- **#125** *(in-flight, PR #136)* Misvattingen-laag: verworpen/ontzenuwde
  claims als negatieve kennis in antwoorden ("hoe het wél én niet zit"), met
  weerlegging en bron.

**Beheer & reviewqueues**
- **#124** *(in-flight, PR #137)* Reviewqueues: verworpen/afgehandelde items
  uit de default-weergave, een archief voor álle queues, en
  beheerder-notities die doorwerken (optioneel als geverifieerde correctie).

**Ops & platform**
- **#45** Ops-hardening voor de 8GB-VM: memory-limits, healthchecks +
  deploy-verificatie, één updatemechanisme (Watchtower vs push-to-deploy),
  log-rotatie, migratie-retry bij opstart, secrets-hygiëne, CSP/security-headers.

**Documentatie & proces**
- **#134** Levende documentatie: arc42-architectuurdocument + dit PRD, verankerd
  in elke PR (dit document is deel van #134).
- **#55** Autonome-werkdag-draaiboek en **#60** actuele handoff — proces-issues
  die de stand en volgorde bijhouden (geen productfeature).

**Backlog (bewust gedeprioriteerd)**
- **#15** Decks: model, deck-code-import, ingest (Piltover Archive, melee.gg) en
  analyse (legaliteit/curve, hand-simulator, synergie/archetype, meta-explorer).
  Fundament voor de meta-/tactieklaag (piramide-laag 3). Onderzoek in
  `docs/ENGINE.md` §5.

---

## 7. Succesmetrieken

**Wat het product vandaag al meet**
- **Antwoordduur & tokens** (`ask_metric`, `/api/ask/stats`): count,
  gemiddelde, mediaan en P90 over de recente vragen — de latency die de
  gebruiker voelt — plus sinds #121 echte input/output-tokens per vraag, pad
  en account.
- **Feedback-/reviewdoorstroom**: denk-feedback op `/ask` → reviewqueue →
  geverifieerde correcties; het aantal geaccepteerde vs. openstaande claims,
  relaties en mechaniek-kandidaten in de overzichten is de gezondheidsmeter van
  de kennisbank.
- **Kennis-gaten-rapport** (`/api/admin/overview/gaps`): geclusterde onzekere
  antwoorden, negatieve feedback en lege-retrieval-vragen — meet waar de bank
  aantoonbaar tekortschiet in plaats van te raden.
- **Vraag-traces** (#40): per vraag welke kennislagen en brein-stappen meededen
  — controleerbaarheid als kwaliteitsmaat.

**Zinnige volgende metrieken**
- **Dekking**: aandeel `/ask`-antwoorden met een "Zeker/Redelijk zeker"-label
  vs. "Onzeker", en het aandeel met minstens één officiële §-regelbasis.
- **Kosten per vraag**: (geschatte) kosten in euro's bovenop de tokentotalen
  van #121, als budgetbewaking naast de puur informatieve tellingen.
- **Kennis-versheid**: doorlooptijd van set-release/erratum tot bijgewerkte
  primer/claims (invalidatie → hertoetsing → re-review).
- **Drift** (brein): aantallen per knooptype in Postgres vs. Neo4j, om een
  achterlopende projectie te meten in plaats van te raden.

---

## 8. Expliciet buiten scope

- **Deckbuilder / deck-analyse** — bewust geparkeerd in de backlog (#15,
  gedeprioriteerd); pas oppakken wanneer de kaart- en kennisbasis staat.
- **Meta-/tactieklaag** (archetypes, staples, combo-frequentie) — piramide-laag
  3; hangt aan #15 en komt daarna.
- **Accounts breder dan de passkeys-/e-mailbasis** — uitgebreid
  gebruikersbeheer, rollen, social login e.d. zijn geen doel; huidige accounts
  dienen quota/kosteninzicht, niet een sociaal platform.
- **Meertaligheid** — de UI is Nederlands (Engelse speltermen onvertaald); een
  meertalige site is geen doel.
- **Publieke brein-API** — de brein-koppelvlakken zijn compose-intern; extern
  ontsluiten (auth, quota) is een apart, later besluit.
- **Write-tools voor de AI-agent** — het brein is read-only voor AI; kennis
  muteren blijft via pijplijnen en reviewqueues.
- **Verplaatsing van de bron van waarheid naar Neo4j** of een aparte
  vector-store — Postgres blijft leidend, projecties blijven herbouwbaar.

---

## Onderhoud

Dit document is levend. **Elke PR die features of gedrag wijzigt, werkt dit PRD
in dezelfde PR bij** — net als de code zelf (verankering uit #134). Laat het
niet achterlopen: een feature zonder PRD-regel is niet af.

Welke wijziging raakt welke sectie:

| Soort wijziging | Werk deze sectie(s) bij |
|---|---|
| Nieuwe feature live gezet | §4 Feature-inventaris (+ §3 Use-cases als er een nieuw scenario ontstaat) |
| Bestaande feature gewijzigd of verwijderd | §4 Feature-inventaris (route/endpoint en beschrijving) |
| Nieuw persona of gebruikssituatie | §2 Doelgroepen (+ §3 Use-cases) |
| Issue aangemaakt of gesloten; PR geopend/gemerged | §6 Roadmap (verplaatsen, in-flight markeren of verwijderen) |
| Nieuwe of gewijzigde kwaliteitseis | §5 Niet-functionele eisen |
| Nieuwe/gewijzigde meetwaarde | §7 Succesmetrieken |
| Iets bewust níet doen | §8 Buiten scope |
| Visie of einddoel bijgesteld | §1 Visie & missie (en verifieer tegen `docs/KNOWLEDGE.md` / `docs/BRAIN.md`) |

Praktische regels:
- Een gesloten issue verdwijnt uit §6 zodra de feature in §4 staat; een
  in-flight PR blijft met de `(in-flight, PR #…)`-markering staan tot de merge.
- Verwijst een sectie naar een route of endpoint, controleer bij de wijziging
  of dat pad nog klopt (`rb-web/src/routes/`, `RbRules.Api/Endpoints/`).
- Feitelijk blijven: beschrijf wat op `main` staat. In-flight of gepland werk
  hoort in §6, nooit in §4.
