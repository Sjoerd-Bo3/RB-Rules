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
- **Bron-feeds** (#167) — index-pagina's (playriftbound.com/en-us/news/…) die
  periodiek op nieuwe artikel-URL's worden afgespeurd, vóór elke bron-scan
  (`IngestService`/`FeedCrawlService`). Drie officiële hoofdfeeds (rules-
  and-releases, de brede nieuws-hub met categoriefilter, de Rules Hub-
  artikelcarrousel), elk met een optioneel categoriefilter. AutoApprove zet
  een nieuw artikel direct als `Source` (met `FeedId`-herkomst, zichtbaar bij
  de bron als "stamt van: …") — maar **alleen** wanneer de feed én het artikel
  op een officieel Riot-domein staan (`OfficialDomains`: playriftbound.com,
  legacy riftbound.leagueoflegends.com); op elk ander domein routeert een
  nieuw artikel naar `SourceProposal` (reviewqueue), ook met AutoApprove aan.
  Zo fabriceert een typo/look-alike-domein nooit onbeheerd trust-1 official
  bronnen (het beheer weigert AutoApprove al fail-fast op een niet-officieel
  domein; de crawl handhaaft het nogmaals). Een handmatig verwijderde
  feed-bron krijgt een tombstone (rejected `SourceProposal`) zodat de crawl
  hem niet stil opnieuw aanmaakt. Idempotent op genormaliseerde URL (ook
  binnen één run, over feeds heen) en doof voor een per-request wisselende
  linkvolgorde — registreert alleen de artikel-URL, nooit zelf een PDF-link.
  Zelf uitbreidbaar in het beheer.
  *Route* `/admin/overview/feeds` · *endpoints*
  `GET/POST/PATCH/DELETE /api/admin/feeds`, `/api/admin/overview/feeds`.

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
- **Bronvolgorde kaart-sync** — de officiële Riot-gallery is leidend
  (onvoorwaardelijke upsert, ook voor namen en kaartvelden) en riftcodex
  vult daarna alleen aan — extra kaarten (JDG-promo's) en set-metadata,
  bestaande kaarten blijven onaangeraakt; `CARD_SOURCE=riot|riftcodex`
  blijven expliciete overrides en het job-detail telt per bron (#150).
- **Bronvorm-normalisatie (riftcodex)** — de kaart-sync normaliseert
  riftcodex-vormen bij binnenkomst naar de Riot-gallery-vorm: ster-id's
  ("sfd-239\*-221" → "sfd-239-star-221") en streepjes-namen ("Soraka -
  Wanderer" → "Soraka, Wanderer" — alleen waar de komma-basisnaam al als
  kaart bekend is; bij conflict op dezelfde printing wint de bestaande
  Riot-naam). Een idempotente reparatiestap in de sync voegt eerder
  ontstane dubbelen samen op (set, collector-nummer, variant-suffix) en
  hangt alle verwijzingen (bans, errata, interacties, rulings, relaties,
  claims, variant-verwijzingen) mee om; run_log meldt hoeveel (#144).
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
- **Aanpak-keuze per vraag (alleen ingelogd)** — Auto (de AgenticGate
  beslist), Snel (geforceerde single-pass, nooit escaleren) of Grondig
  (de brein-agent forceren), met eerlijke verwachting ("±2 min, telt
  zwaarder mee") en het resterende dagtegoed bij het formulier (#153).
  Server-authoritatief: anoniem is altijd Auto, de `ASK_AGENTIC`-flag blijft
  de meester en foto-vragen blijven op het vision-pad. Grondig kost een
  eigen dagquotum; is dat op, dan valt de vraag terug op Auto met een nette
  melding — de gebruikte aanpak en de reden reizen als metadata mee in de
  respons (en op het streamingpad al in het meta-frame). De keuze reist mee
  met doorvragen en overleeft de feedback-roundtrip.
- **UX** — voorbeeldvragen, klikbare citaties, geschiedenis en denk-feedback
  (duim omhoog/omlaag, die de self-learning-loop voedt). *Endpoint*
  `/api/corrections`.
- **Echte duurstatistiek** — antwoordduur wordt gemeten (`ask_metric`) en
  getoond (count/gemiddelde/mediaan/P90), sinds #152 uitgesplitst naar
  gemiddelde fase-verdeling (rewrite/embed/retrieval/AI) — de wachtindicatie
  op `/ask` toont waar de tijd zit. *Endpoint* `/api/ask/stats`.
- **Snellere retrieval** (#152) — de query-rewrite blokkeert de pipeline niet
  meer (draait parallel met de rewrite-onafhankelijke kanalen), de
  onafhankelijke retrieval-kanalen (vector, FTS, primer, rulings,
  kaartcontext, banlijst, claims, misvattingen) draaien concurrent op een
  eigen databasecontext per kanaal, en een kleine LRU-cache slaat de
  rewrite-call over bij een herhaalde/gelijksoortige vraag. Uitval van één
  kanaal blijft altijd gedegradeerd (leeg kanaal + trace-marker), nooit een
  500; de uitkomst (antwoord, citaties, prompt) blijft byte-voor-byte gelijk
  aan de oude seriële pipeline.
- **Eigen ask-geschiedenis** — uitklap-paneel "Mijn eerdere vragen" op `/ask`
  (dicht standaard): de laatste 20 eigen vragen met tijdstip, vraagtype en het
  antwoord (gerenderd zoals het origineel, met dezelfde sanitize/widgets),
  plus of de vraag agentic beantwoord werd. Scope is altijd de vrager zelf —
  ingelogd op het account (`user_id`), anders op de gehashte IP-koppeling
  (`ip_hash`, zie §4.6/§5) van het huidige request; geen id-parameter, dus
  geen enumeratie van andermans historie mogelijk (#157). *Endpoint*
  `/api/ask/history`.

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
  betrokken primer-docs en claims automatisch; kaart-embeddings invalideren
  bij naam- of tekstwijziging (beide zitten in de embeddingtekst, #150), de
  uitleg-cache bij tekstwijziging.
- **Brein-API** — zes koppelvlakken over de unified vector+graph-representatie,
  met per resultaat een laag- en trust-label; compose-intern (browser komt er
  via rb-web-proxy's). *Endpoints* `/api/brain/search`, `/api/brain/node/{ref}`,
  `/api/brain/neighbors/{ref}`, `/api/brain/path`, `/api/brain/evidence/{ref}`,
  `/api/brain/contradictions`.
- **Agentic ask** — voor kwalificerende vragen (interactievraag met ≥2
  kaartnamen, of lege retrieval) mag rb-ai als agent over het brein redeneren
  via de brein-tools, achter een feature-flag met vangnet naar single-pass; de
  brein-stappen staan in de trace. Ingelogde gebruikers kunnen de agent ook
  zelf forceren of juist uitsluiten via de aanpak-keuze op `/ask` (#153,
  §4.3), binnen een eigen dagquotum; de trace en het kostenoverzicht
  onderscheiden gate- en gebruikers-escalaties. Verbanden die de agent
  onderweg ontdekt komen als relatievoorstel in de reviewqueue (#120) — het
  brein verrijkt zichzelf al antwoordend, altijd achter de reviewpoort.
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
  (scan, feeds, cards, embed, mine, rules, bans, graph, primer, interactions,
  scout, classify, claims, relations, setrelease, decks, benchmark) draaien via
  `JobRunner` met live-voortgang en run_log. *Route* `/admin` · *endpoints*
  `/api/admin/jobs/{name}`, `/api/admin/status`, `/api/admin/logs`.
- **Bron-feeds beheer** (#167) — feeds zelf toevoegen/bewerken/aan-uitzetten/
  verwijderen, met per feed het aantal ontdekte bronnen en de laatste vangst;
  elke bron toont zijn herkomstfeed (klikbaar terug). *Route*
  `/admin/overview/feeds` · *endpoints*
  `GET/POST/PATCH/DELETE /api/admin/feeds`, `/api/admin/overview/feeds`.
- **Deck-ingest (Piltover Archive)** — job "decks" haalt publieke
  community-decks binnen via de PA-sitemap (#15, Piltover-first: géén eigen
  deckbuilder). Robots-compliant (alleen `/sitemap*` en `/decks/view/{uuid}`;
  hun `/api/` blijft onaangeraakt), throttled (~1,5 s) en gecapt per run met
  hervatting via het run_log-grootboek; her-fetch alleen bij een nieuwere
  sitemap-lastmod. Kaartregels koppelen via de variantgroepering aan onze
  canonieke kaarten (onbekend = zichtbaar signaal); elk deck draagt zijn
  bron-URL als attributie met deep-link terug. *Route*
  `/admin/overview/decks` · *endpoint* `/api/admin/overview/decks`.
- **Aanklikbare status-tegels** — elke teller opent een overzichtspagina.
  *Route* `/admin/overview/[kind]` · *endpoints* `/api/admin/overview/{cards,
  rulechunks, bans, errata, interactions, changes, claims, proposals, relations,
  users, gaps, setcoverage, benchmark, feeds}`.
- **Set-dekking** — tegel + overzichtspagina: per set het basistotaal,
  aanwezige én exact ontbrekende kaartnummers (compacte reeksweergave,
  bv. "12, 45–47, 203"), dekking %, variantentelling, afwijkende
  bron-totalen en laatste sync — allemaal afgeleid uit de riftbound-id's
  zelf ("ogn-074-298" = nr. 74 van 298). Onvolledige sets verschijnen
  bovendien als signaalregel in het kennis-gaten-rapport ("set X mist N
  nummers", met doorklik). *Route* `/admin/overview/setdekking` ·
  *endpoint* `/api/admin/overview/setcoverage` (#145).
- **Reviewqueues** — claims, relaties (+ kandidaat-kinds), mechaniek-kandidaten
  en bronvoorstellen accepteren/verwerpen, mét het bewijs per keuze (#123).
  *Endpoints* o.a. `/api/admin/claims/{id}/accept|reject`,
  `/api/admin/relations/{id}/accept|reject`,
  `/api/admin/relationkinds/{id}/accept|reject`,
  `/api/admin/mechanics/{id}/accept|reject`,
  `/api/admin/proposals/{id}/accept|reject`.
- **Vraag-traces** — per vraag welke kennislagen en brein-stappen meededen
  (#40), en sinds #143 het volledige gesprek: het definitieve antwoord (ook
  het streaming-slotframe; bij AI-uitval de eerlijke uitvalmelding) en een
  snapshot van de doorvraag-beurten (#41). De uitklap toont het als
  chatweergave — eerdere beurten, de vraag, de brein-stappen op hun plek en
  het antwoord gerenderd zoals op `/ask` — naast de bestaande metadata.
  *Endpoints* `/api/admin/asktraces` (slanke lijst, zonder antwoord/gesprek),
  `/api/admin/asktraces/{id}` (het gesprek, lazy bij het uitklappen).
- **Token-metering & kostenoverzicht** — echte input/output-tokens per vraag
  (rb-ai geeft usage door, geboekt op `ask_metric`), getotaliseerd per pad
  (cheap/hard/agentic, waarbij agentic splitst op gate- vs
  gebruikers-escalatie, #153) en per account in het kostenoverzicht (#121);
  de vraag-traces dragen dezelfde attributie als badge ("agentic (gate)" /
  "agentic (gebruiker)"). Quota per account (vragen/foto's/Grondig) zijn in
  het gebruikersoverzicht per rij bij te stellen.
- **Periodieke zelfverrijking** — relatie-mining nachtelijk en de
  bronnen-scout wekelijks in de scheduler-tick, met job-gate,
  run_log-vensters en degradatiepaden (#122).
- **Kennis-gaten-rapport** — geclusterde onzekere/lege-retrieval-vragen sturen
  de volgende harvest. *Endpoint* `/api/admin/overview/gaps`.
- **Judge-benchmark** (#158) — job "benchmark" draait een vaste, extern
  aangeleverde meerkeuzevragenset (de scheidsrechter-judge-test, seed
  `BenchmarkSeed`) door exact dezelfde `/ask`-pipeline (retrieval + prompt
  ongewijzigd), met per vraag de opties in de vraagtekst en de instructie om
  zich op één letter te committeren ("gecommitteerde keuze"); een
  deterministische parser (`BenchmarkPrompt.ParseChoice`) haalt de gekozen
  letter uit het antwoord — geen match ⇒ onscoorbaar, geen fout. **Isolatie
  is hard**: de run zet `AskOptions.Benchmark = true`, waarmee AskService élk
  leer-/meetneveneffect onderdrukt — geen `ask_trace`/`ask_metric`-rij en geen
  agentic-relatie-terugkoppeling (#120); de vragenset voedt de kennisbank dus
  nooit (zie ook §8 in ARCHITECTURE.md). Score = % correct over de vragen mét
  een bevestigde antwoordsleutel (`correct_index != null`); ongekeyde vragen
  draaien gewoon mee (bewijs voor de agent-kwaliteit) maar tellen niet mee in
  het percentage. *Route* `/admin/overview/benchmark` · *endpoints*
  `/api/admin/jobs/benchmark`, `/api/admin/overview/benchmark`.
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
  Drie dagquota per account (UTC-dag, geteld uit `ask_metric`): vragen,
  foto-vragen en zelf geforceerde Grondig-vragen (#153) — de accountpagina
  toont het verbruik en resterend tegoed van alle drie.
  *Routes* `/account`, `/account/verify` · *endpoints* `/api/auth/request`,
  `/api/auth/verify`, `/api/auth/me`, `/api/auth/logout`.
- **Privacy-nette IP-koppeling (#157)** — waar rb-api "zelfde IP" moet
  herkennen (anonieme ask-geschiedenis) bewaart het nooit het rauwe IP: een
  HMAC-SHA256-hash met een server-secret (`ASK_IP_HASH_SECRET`, VM-`.env`).
  Ontbreekt het secret of is het IP niet vast te stellen, dan blijft de hash
  leeg — stille degradatie, de vraag blijft gewoon werken, alleen de
  geschiedenis-koppeling valt weg. De UI meldt expliciet dat anonieme
  geschiedenis aan het IP/apparaat hangt en verdwijnt bij een IP-wissel;
  ingelogd hangt hij aan het account.
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
  zijn alleen compose-intern bereikbaar. IP-adressen worden nooit rauw
  opgeslagen — waar "zelfde IP" herkenbaar moet zijn (anonieme
  ask-geschiedenis, #157) staat alleen een HMAC-SHA256-hash met een
  server-secret; zonder secret degradeert de koppeling stil naar leeg.
- **Beheer-continuïteit.** Nooit mergen/deployen terwijl een live admin-job
  draait — de deploy herstart de container en breekt de job af. Lange operaties
  draaien via `JobRunner` (202/409 + voortgang), nooit synchroon in een request.
- **Kosten & latency onder controle.** AI-werk (harvest/mining) draait
  nachtelijk, batched en gecapt op een goedkoop model; agentic ask heeft
  maxTurns, een tool-call-cap en een harde timeout en staat standaard achter een
  flag.
- **AI-capaciteitsbescherming van de 8GB-VM** (#154/#155). Een
  voorverwarmsignaal bij het laden van `/ask` (fire-and-forget, per-IP
  gelimiteerd) haalt de SDK-subprocess-boot van het kritieke pad voor de
  query-rewrite-call; een globale sessie-cap in rb-ai (`AI_MAX_CONCURRENCY`,
  default 3, agentic weegt 2) voorkomt dat een piek aan gelijktijdige vragen
  de VM leegtrekt — boven de cap wacht een vraag kort (max 30s) en degradeert
  daarna netjes (bestaand "AI weg"-pad), nooit een crash of een stille
  wurging van de VM.
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

**Decks (Piltover-first)**
- **#15** *(herscoped 2026-07-13; spoor 2 — deck-model + PA-ingest — in-flight
  op deze branch)* Géén eigen deckbuilder: we spiegelen Piltover Archive met
  attributie en deep-links terug. Sporen: (1) deck-codes — gemerged (PR
  #146: C#-port van RiftboundDeckCodes, Apache 2.0, Domain-laag; UI volgt in
  spoor 3), (2) deck-model + robots-compliant ingest via de PA-sitemap, (3)
  meta-laag & UI (deck-browser, legaliteitscheck, "populair in N% van recente
  decks", archetype-signalen als kennispiramide-laag 3). Onderzoek in
  `docs/ENGINE.md` §5.

---

## 7. Succesmetrieken

**Wat het product vandaag al meet**
- **Antwoordduur & tokens** (`ask_metric`, `/api/ask/stats`): count,
  gemiddelde, mediaan en P90 over de recente vragen — de latency die de
  gebruiker voelt — plus sinds #121 echte input/output-tokens per vraag, pad
  en account; sinds #152 ook de gemiddelde fase-verdeling
  (rewrite/embed/retrieval/AI) over de recentste traces mét timings, zodat
  duidelijk is wáár in de pipeline de tijd zit in plaats van alleen het
  totaal.
- **Feedback-/reviewdoorstroom**: denk-feedback op `/ask` → reviewqueue →
  geverifieerde correcties; het aantal geaccepteerde vs. openstaande claims,
  relaties en mechaniek-kandidaten in de overzichten is de gezondheidsmeter van
  de kennisbank.
- **Kennis-gaten-rapport** (`/api/admin/overview/gaps`): geclusterde onzekere
  antwoorden, negatieve feedback en lege-retrieval-vragen — meet waar de bank
  aantoonbaar tekortschiet in plaats van te raden.
- **Vraag-traces** (#40): per vraag welke kennislagen en brein-stappen meededen
  — controleerbaarheid als kwaliteitsmaat; sinds #143 mét het definitieve
  antwoord en de gespreksgeschiedenis, zodat route én uitkomst samen te
  beoordelen zijn.
- **Set-dekking** (`/api/admin/overview/setcoverage`, #145): per set het
  aandeel aanwezige basisnummers en de exacte ontbrekende nummers — meet of
  de kaartenbank compleet is in plaats van het aan te nemen; onvolledige
  sets zijn een signaal in het kennis-gaten-rapport.
- **Judge-benchmark** (`/api/admin/overview/benchmark`, #158): score (%
  correct) van de vaste scheidsrechter-vragenset over runs heen — een echte
  regressiemeter bij prompt-/retrieval-/modelwijzigingen, volledig
  losgekoppeld van de kennisbank (geen trace/metric/relatie-instroom) zodat
  het meten zelf de bank nooit vervuilt.

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

- **Een eigen deckbuilder/deck-editor** — blijft expliciet buiten scope, ook
  na de herscoping van #15 (Piltover-first): we gebruiken het werk van
  Piltover Archive, met prominente attributie en deep-links terug; decks
  bouwen of bewerken doe je dáár, niet hier.
- **Meta-/tactieklaag** (archetypes, staples, combo-frequentie) — piramide-laag
  3; hangt aan #15 (fase 3) en komt daarna.
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
