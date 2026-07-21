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
| **Deckbouwer / competitieve speler** | Speler die kaarten en interacties bestudeert | Kaarten vinden op eigenschap/betekenis, interacties en gelijkenissen zien, weten wat legaal is | `/cards`, `/cards/[id]`, `/graph`, `/decks`, `/decks/[id]` |
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
- *Inspiratie opdoen bij community-decks en hun legaliteit checken.* De
  deck-browser (`/decks`) toont Piltover Archive-decks met facet op domein, een
  legaliteitsbadge, een filter op legaliteit en zoeken op deck-/legend-/
  championnaam (#265); de detailpagina laat precies zien welke kaart een
  probleem geeft (nog niet legale set of geband) en linkt terug naar de bron.
- *Een gedeelde deck-code uitlezen.* Een deck-code plakken op `/decks` (#264)
  laat zien welke kaarten erin zitten en of het deck legaal is, zonder dat er
  iets wordt opgeslagen.

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

**Merk & identiteit (#216).** Het product heet **Poracle** — een poro-mascotte
(gestileerde, crème poro; het herbruikbare `PoroMark`-component) plus het
woordmerk "Poracle". De site blijft inhoudelijk een Riftbound-companion (de
omschrijving en disclaimer noemen Riftbound onverkort). Het merkteken vervangt
de oude domein-rainbow-`mark` in de publieke shell, de beheer-shell en de
hero; geel (`--accent`) blijft het actie-accent en kleurt de app-tegel
(favicon/PWA-icons) — niet de kuif van de poro.

**Subtiele poro-animaties (#220).** `PoroMark` kent een opt-in `animate`-prop
(`false` standaard → statisch, dus bestaande gebruiken onveranderd): `'idle'`
(rustig ademen/bobben + af en toe knipperen — de merk-poro in de shell) en
`'wink'` (iets levendiger, met knipoog + lichte wiebel — de 404-illustratie).
De brand-link in de shell geeft de poro bij hover/focus een korte micro-bounce.
Alle beweging is klein en langzaam en staat **volledig stil bij
`prefers-reduced-motion: reduce`** (component-eigen `animation: none`, niet enkel
de globale duur-vangrail).

### 4.1 Regels & bronnen

- **Overzicht-dashboard** (#214) — de homepage `/` is een landingsdashboard:
  een zoek-hero (Enter → `/ask`), statistiek-tegels (kaarten · geverifieerde
  rulings · actieve bans · nieuwe wijzigingen, via `GET /api/stats`), een
  paneel "Recente wijzigingen" (compacte ChangeCards) en "Spring naar"
  (sectie-links met tellingen). *Route* `/` · *endpoint* `/api/stats`,
  `/api/changes`.
- **Wijzigingen-feed** — toont automatisch gedetecteerde wijzigingen (bans,
  errata, regelupdates, set-releases) met bron, severity, voor/na-diff en een
  menselijke samenvatting/betekenis; sinds #214 op de eigen route
  `/wijzigingen` (de root is het dashboard). Flip-flop-suppressie onderdrukt
  ruis van bronnen die per request de volgorde wisselen.
  **Changeconsolidatie (#206)**: meldt een officiële en een community-bron
  hetzelfde event (bv. dezelfde ban-update binnen 72 uur, met overlappende
  kaart-/sectiereferenties — een deterministische poort, `ChangeConsolidationGate`),
  dan toetst een LLM-call ("zelfde gebeurtenis?") of ze echt samenvallen; zo ja,
  dan wordt de meest gezaghebbende bron (hoogste TrustTier, bij gelijke trust de
  vroegste detectie) de primaire kaart en verdwijnt de andere uit de hoofdlijst
  als genestelde "bevestigd door {bron}"-badge — uitklapbaar naar de
  samenvatting, duiding én voor/na-diff van de bevestigende bron. Beide
  `Change`-rijen blijven bestaan (`Change.ConsolidatedWithId`) — dit is
  presentatie, geen inhoudelijke waarheid (die blijft bij de structured
  ban-/errata-precedentie, #168). Draait idempotent als jobstap
  `consolidatechanges` in het ingest-pad én uurlijks automatisch via de
  scheduler; een "nee"-oordeel wordt per paar onthouden (pair-memo in
  run_log), dus elk paar wordt hooguit één keer aan de LLM voorgelegd. Een
  foute koppeling is herstelbaar: "Ontkoppel" in het admin-overzicht maakt
  de secundaire weer een losse kaart en blokkeert her-consolidatie van dat
  paar blijvend. "Verwijder uit feed" op een primaire verwijdert ook haar
  bevestigingen (zelfde event; de UI-confirm meldt het aantal).
  Editorial-changes ("volgorde gewijzigd; inhoud ongewijzigd") verschijnen
  nooit als zelfstandige kaart op de publieke pagina (#207, read-time
  gefilterd; "unknown" blijft zichtbaar en een editorial als bevestiging
  van een echt event blijft werken) — het admin-overzicht toont ze wél.
  **Kaart-presentatie herzien (#210)**: één herbruikbaar `ChangeCard.svelte`
  (`rb-web/src/lib`) tekent de kaart in alle vier contexten (publieke feed,
  admin-overzicht "wijzigingen", sectie-dossier, bron-dossier — de laatste
  twee via een compacte `compact`-variant zonder bron/bevestigingen/acties);
  hiërarchie is kop (type+severity-badge, bron met officieel/community-
  trustlabel, datum) → kern (samenvatting, speler-impact als accentrand-
  blok) → voet (bevestigd-badges, voor/na-uitklap, admin-acties in een
  Svelte 5 snippet-slot: Verwijder op de feed, Ontkoppel per bevestiging in
  het admin-overzicht).
  **Design-refresh + domein-kleurcodering (#214)**: nieuwe visuele richting
  ("Domains, eigentijds") — koel-neutrale ontwerptokens, licht als standaard
  met een koele-graphite donker-variant (theme-aware `app.css`), geel puur als
  actie-accent. De ChangeCard krijgt een domein-randstreep in de canonieke
  domeinkleur (Fury/Body/Mind/Calm/Chaos/Order, terugval Colorless) plus een
  domein-chip; het domein wordt read-time uit de geraakte kaart(en) afgeleid
  via de gestructureerde ban-/errata-laag (alleen ban/errata dragen een
  domein, de rest is neutraal). De redesign rolt uit als een **samengestelde
  shell** (vaste zijbalk-nav links, content midden, contextuele rechterrail;
  mobiel: hamburger-drawer + filter-bottom-sheet zonder horizontaal scrollen)
  over alle publieke kernpagina's (dashboard, feed, regels + detail met
  leesrail, kaarten met domein-getinte tegels, vraagbaak). De **publieke
  long-tail** volgt sinds #214 hetzelfde patroon: `/rules` (filter-rail met
  bron-chips), `/rulings` (filter-rail met onderwerp-type) en `/decks`
  (filter-rail met domein + sortering) leveren hun filters via de
  rail/bottom-sheet met het actieve filter als verwijderbare chip; `/cards/[id]`
  en `/primer` krijgen een contextuele leesrail ("Op deze pagina" /
  "Concepten"), en kaart- én deckdetail een domein-tint. Het **beheer** is naar
  dezelfde taal herbouwd (eigen console-shell, zie §4.5).
  *Route* `/wijzigingen` · *endpoints* `/api/changes`, `/api/sources`,
  `/api/bans`, `/api/sets/upcoming`.
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
  FTS → RRF, degradatie naar alleen-FTS eerlijk gemeld). Elke ruling toont ook
  zijn "waar besloten"-bronverwijzing (URL of vrije citatie, #166), ook in het
  kaart-dossier en de reviewqueue.
  *Route* `/rulings` · *endpoint* `/api/rulings`.
- **FAQ-/clarificatie-concept-extractie** (#177) — een FAQ-/clarificatie-
  artikel (bv. de Unleashed Rules FAQ) wordt door de gewone scan-pipeline
  geknipt en geëmbed als vaste-lengte-slabs die meerdere losse
  verduidelijkingen mengen; één embedding over zo'n slab slaat de betekenis
  plat, dus een gerichte vraag ("Legion = finalize an item on the chain")
  haalt het chunk niet boven. **Bron-type is sinds #188 increment 2 een
  LLM-classificatie**: bij de scan van een officiële (TrustTier 1) bron
  vraagt rb-ai eenmalig een oordeel — "faq", "patch-notes" of "other" — op
  naam + URL + een kort content-fragment (Engelse prompt, #187-lijn),
  gepersisteerd op `Source.ContentKind` (+ `ContentKindSource`: "llm",
  "heuristic" of "admin", `SourceContentKind`). "faq" is beperkt tot Q&A-/
  clarificatie-ARTIKELEN — een rulebook, core rules PDF of how-to-play-gids
  legt óók regels uit maar is "other" (de prompt noemt die voorbeelden
  letterlijk); een gemengd of onzeker artikel (bv. "Rules FAQ and Patch
  Notes") is eveneens **"other"** (neutraal: niet gemined, niet geretract —
  de #185-tie-break "patch-notes wint" is met de operative-poort uit #188
  inc1 en de consensus-poort hieronder niet meer nodig). Wijkt het
  LLM-oordeel af van de heuristiek, dan komt er één run_log-regel met beide
  waarden (zichtbaarheid, geen blokkade). AI-uitval of een onbruikbaar
  antwoord degradeert naar de oude naam-/URL-heuristiek
  (`ClarificationSources.IsMatch`/`IsPatchNotesSignal`, nu het deterministische
  vangnet); een latere scan mag zo'n heuristische classificatie alsnog naar
  een LLM-oordeel optillen. Bronnen die nog niet (opnieuw) gescand zijn sinds
  deze increment vallen transitioneel terug op diezelfde heuristiek
  (`SourceContentKind.Resolve`). De beheerder kan de kind expliciet
  vastzetten of wissen via het bestaande source-PATCH-pad (`SourcePatch.
  ContentKind`; herkomst "admin" wordt nooit geherclassificeerd en telt als
  menselijke bevestiging; leeg = wissen ⇒ herclassificatie). **Patch-notes-
  bronnen doen sinds #185 níét meer mee** — een patch-notes-artikel is een
  regelwijziging (delta) en hoort in de wijzigingen-feed, niet als
  op-zichzelf-staande ruling; elke clarify-run trekt bovendien de vóór #185
  ten onrechte gemínede patch-notes-rulings terug
  (`RetractPatchNotesCorrectionsAsync`, verified én pending, idempotent) —
  met een **consensus-poort** op dit destructieve pad (#188-review): hard
  verwijderen alleen als de effectieve kind patch-notes is ÉN de
  deterministische heuristiek dat bevestigt (of de beheerder de kind
  expliciet vastzette); oneens ⇒ alles blijft staan + run_log-waarschuwing,
  en een wees-bron (Source-rij verwijderd) wordt nooit meer opgeruimd op
  alleen haar id — alleen gelogd voor handmatige beoordeling. Job
  "clarify" destilleert er via rb-ai discrete concepten uit (onderwerp +
  gefocuste verduidelijking + evt. §-verwijzing + citaat; de verduidelijking
  in het **Engels** opgeslagen, dicht bij de officiële bronbewoording, #186)
  en slaat elk op als ruling met een eigen,
  gefocuste embedding (alleen de verduidelijking, niet de hele slab) — zo komt
  het item wél boven bij een gerichte vraag, in `/ask`, `/rulings` en (bij een
  kaart-onderwerp) het kaartdossier. **Hybride autoriteitspoort** (autoriteits-
  review): auto-verified voor LLM-geparafraseerde tekst is te los, dus een
  concept wordt alleen direct `verified` als het én *grounded* is (het citaat
  komt écht in de brontekst voor — vangt een gehallucineerd citaat) én
  *anchored* (het onderwerp resolvet naar een bestaande knoop: kaartnaam,
  mechaniek-vocabulaire, §-code of primer-concept — vangt een verzonnen/fout
  anker dat anders stil aan een kaartpagina zou koppelen) én *informative*
  (geen kale aankondigingszin — "X is verduidelijkt/gewijzigd" — zonder de
  regel/definitie/interactie zelf, de vorm van de lege Legion-"ruling" uit
  #185). **Sinds #188 is de informativiteits-toets een LLM-oordeel**: de
  `ClarificationMiner`-extractie levert een `operative`-veld per item mee
  (het model onderscheidt "kondigt-een-wijziging-aan" van "beschrijft-de-
  wijziging" beter dan een regex — een adversariële review vond twee kanten
  waarop de oude heuristiek ernaast zat), met `ClarificationInformativeness.
  IsMetaOnly` als deterministisch vangnet wanneer dat oordeel ontbreekt
  (parse-gat, oude data) of uitvalt. Anders gaat het als
  `unverified` met een reden (`Correction.StatusReason`) de bestaande
  corrections-reviewqueue in, waar de beheerder het corrigeert, goedkeurt
  (`/verify`) of afwijst (`/reject` — een `rejected` tombstone die een
  volgende run respecteert, nooit heropent). Werkt met terugwerkende kracht:
  geen tijdvenster op de bronselectie, dus ook al-geïngeste FAQ-artikelen van
  vóór deze feature worden bij de eerste run meegenomen. Idempotent op
  documentniveau (`Document.ClarifiedAt`, #92/#93-patroon) én op conceptniveau:
  een her-mine dedupliceert een verduidelijking op (bron, Scope, Ref) +
  semantische nabijheid (embedding-poort, quote buiten de sleutel) en werkt de
  bestaande ruling bij (nooit degraderend) i.p.v. een tweede te stapelen — ook
  als de LLM bij een retry/cosmetische bronwijziging een parafrase teruggeeft
  (embedding-uitval degradeert naar een genormaliseerde exacte-tekst-toets).
  De eerste scan van een **FAQ-/clarificatie**-bron krijgt ook meteen een
  sjabloon-`Change` (type "clarification") zodat de aankomst zelf al in de
  wijzigingen-feed verschijnt (er is dan nog geen vorige versie om te diffen).
  **Patch-notes-bronnen (#205, herziening van #185)**: een terugkerende
  patch-notes-pagina (core-rules-patch-notes) blijft zonder sjabloon — haar
  duiding komt via de normale voor/na-diff vanaf de tweede scan, precies
  zoals #185 bedoelde. Maar een per-set patch-notes-ARTIKEL (bv. "Core
  Rules: Vendetta Patch Notes") is one-shot: het verandert na publicatie
  nooit meer, dus die tweede-scan-diff komt er nooit en de regelwijzigingen
  bleven daardoor structureel onzichtbaar. Guard i.p.v. "eerste scan": heeft
  een patch-notes-bron nog GEEN niet-editoriale `Change` én nog geen
  one-shot-memo (run_log kind `oneshot-patchnotes`, geschreven bij het
  minten — zo start een later als "editorial" geherclassificeerde one-shot
  nooit een her-mint-lus), dan behandelt de
  scan de volledige inhoud als delta (lege "voor"-versie, dezelfde
  classificatie/samenvatting als een echte diff — `ChangeType` uit de
  classifier, niet hardcoded). Dat dekt ook de BACKFILL van een bron die
  vóór deze fix al zonder Change gescand was (`PatchNotesOneShotChange`,
  Domain). Editorial sidebar-ruis (de "Related Articles"-carousel op
  playriftbound-artikelen, die van scan tot scan verandert zodra elders een
  nieuw artikel verschijnt) telt niet als "al verwerkt" én wordt sinds #205
  bovendien uit de hash/diff gestript (`TextUtils.StripBoilerplate`, zelfde
  patroon als de Rules Hub-flip-flop-suppressie). Strip-wijzigingen zijn
  geversioneerd (`TextUtils.BoilerplateVersion` + `Source.StripVersion`):
  een bron met een verouderde versie rebaselinet stil bij de eerstvolgende
  scan — nieuwe baseline zonder junk-Change en zonder her-mine-kosten, met
  de one-shot-candidacy er direct achteraan (ARCHITECTURE §6.2). *Job*
  `clarify` (handmatig of nachtelijk via `ScanScheduler`) · *endpoints*
  `/api/admin/jobs/clarify`, `/api/admin/corrections/{id}/reject`.
  **Bron, opmerking en her-evaluatie in de reviewqueue** (#184): elk
  correctie-item toont de bron-naam (resolvet voor clarify-mining-items via
  hun `Provenance`) als gesaniteerde (`UrlGuard`) klikbare link, en een
  opmerkingsveld (`Correction.ReviewNote`) dat traceerbaar bewaart wordt bij
  verifiëren/afwijzen/her-evalueren. "Opnieuw evalueren" draait de hybride
  poort hierboven opnieuw voor dát ene item — grounding/anchoring
  deterministisch, informativiteit via een lichte rb-ai-classificatie
  (`ClarificationInformativeness.JudgeSystemPrompt`, #188) die bij AI-uitval
  of onbruikbaar antwoord terugvalt op `IsMetaOnly` (nooit een harde 500) —
  een opmerking mag een anker-correctie bevatten (bv. "mechanic:Recall", "card:…",
  "section:402.3", `ReviewNoteAnchor`) die een fout-aangeankerd of onherkend
  onderwerp overschrijft, zodat een terecht item alsnog `verified` wordt
  zonder de LLM-extractie te herhalen (`CorrectionReevaluationService`). Een
  gezette opmerking reist mee: een volgende clarify-her-mine
  (`ClarificationMiningService.StoreAsync`) laat Status/StatusReason dan
  ongemoeid (nooit stilzwijgend teruggedraaid), en degradeert een al
  geverifieerde ruling sowieso nooit. *Endpoint*
  `/api/admin/corrections/{id}/reevaluate`.
  **Anker-selectie uit het vocabulaire + herstel-pas** (#188 increment 3,
  aangescherpt na adversariële review): productiedata (issue #199) toonde
  dat 117 van de 133 pending clarify-corrections faalden op *anchored* — de
  extractie koos een vrije-vorm-onderwerp ("battlefield control without
  units") buiten het bestaande anker-vocabulaire. De extractieprompt
  (`ClarificationMiner.GetSystemPrompt`) krijgt daarom voortaan het ECHTE
  vocabulaire letterlijk mee — de mechaniek-namen (seed + geaccepteerde
  keywords) en de primer-concept-keys/-titels — met de instructie er bij
  voorkeur een anker uit te KIEZEN; voor kaarten/§-codes (te talrijk om op
  te sommen) blijft de instructie "gebruik de exacte naam/code".
  `ClaimTopicMapper.Resolve` bepaalt nog altijd, ongewijzigd, of een gekozen
  anker ook echt bestaat — beter gereedschap voor de LLM, geen wijziging van
  de poort. Voor de bestaande achterstand draait de `clarify`-job er een
  **herstel-pas** achteraan (`CorrectionReevaluationService.
  RepairPendingAnchorsAsync`, gecapt op 40 per run + `CapHit` voor het
  #190-drain-pad): per pending item met reden "onderwerp … niet herkend" (en
  zonder `ReviewNote` — #184, beheerder-eigendom blijft onaangeraakt) doet
  één rb-ai-aanroep een anker-KEUZE uit hetzelfde vocabulaire
  (`ClarificationAnchorRepair`, Engelse prompt, mét citaat en het
  oorspronkelijke onderwerp als context; antwoord
  `{"topicType":…,"topicRef":…}` of `{"none":true}` — none is een eersteklas
  antwoord). **Autoriteitsmodel (review-uitkomst):** een resolvend anker
  bewijst alleen dat de term bestaat, niet dat hij bij déze tekst hoort —
  auto-verificatie vereist daarom ook **lexicale steun**
  (`ClarificationAnchorRepair.HasLexicalSupport`, deterministisch: de
  ankerterm — voor een concept minstens één significant token van key of
  titel — komt voor in verduidelijking + citaat + het oorspronkelijke
  onderwerp), en dan pas draait de volledige hybride poort (dezelfde logica
  als "opnieuw evalueren" hierboven, gedeeld i.p.v. gedupliceerd). Zonder
  lexicale steun wordt het item een **aanbeveling** (het #199-principe:
  machine sorteert voor, mens klikt): Scope/Ref verhuizen naar het
  resolvende anker zodat de reviewqueue het bij het juiste onderwerp toont,
  maar de status blijft pending ("anker hersteld via LLM-suggestie … wacht
  op review") en de beheerder one-click-verifieert via het bestaande
  /verify-pad. **Terminaliteit:** een definitieve uitkomst ("none" of een
  niet-resolvende keuze) markeert het item terminaal ("anker-herstel
  geprobeerd" in de StatusReason; het selectie-predicaat sluit die marker
  uit) zodat het niet elke run cap-ruimte verbrandt; AI-uitval en
  onbruikbare output zijn transiënt (volgende run opnieuw), en een latere
  her-mine die het item bijwerkt schrijft een verse reden zonder marker en
  her-opent de eligibility vanzelf (het herstel-na-nieuwe-informatie-pad).
  `CapHit` telt alleen echt-eligible items. **Duplicaat-bewaking (alléén het
  geautomatiseerde pad — een handmatige #184-anker-correctie mag altijd
  verplaatsen):** vóór elke verplaatsing checkt de pas CANONIEK
  (BrainRef-vergelijking via `ClaimTopicMapper.Resolve`, zodat aliassen —
  kaartvarianten, concept-key vs. -titel — niet langs elkaar heen matchen)
  of een ándere Correction van dezelfde bron het anker al bezet; zo ja, dan
  wordt het item een terminale **duplicaat-kandidaat** ("anker … is al bezet
  … mogelijk duplicaat, beoordeel handmatig") in plaats van een tweede
  ruling over hetzelfde onderwerp. Achtergrond: de pas zet bewust géén
  `ReviewNote` (dat zou een geautomatiseerde keuze onterecht als
  menselijk-beoordeeld labelen), dus de ReviewNote-gebaseerde
  cross-bucket-redding in `StoreAsync` (#184) ziet een door deze pas
  verplaatst item niet — deze canonieke check dekt dat gat.
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
- **Herkomst-adoptie & near-duplicaat-samenvoeging** (#175) — de overlap
  tussen handmatig toegevoegde bronnen en feed-afstammelingen wordt niet
  meer stil overgeslagen. Herontdekt een feed-crawl een artikel-URL die
  (genormaliseerd) al een bestaande `Source` is zonder `FeedId`, dan
  adopteert die bron de feed als herkomst — `Enabled`/`TrustTier`/`Rank`
  blijven exact zoals ze zijn (adoptie is geen nieuwe beoordeling, een
  uitgezette of laag-getrouwde bron blijft zo). Los daarvan voegt elke
  feed-crawl-run near-duplicaat-bronnen samen: rijen die vóór deze fix als
  aparte `Source` bestonden maar alleen in URL-*vorm* verschillen (trailing
  slash, http/https, www). Winnaar: de rij mét `FeedId` (herkomst al
  vastgesteld), anders de hoogste `Rank`, anders de laagste Id. Referenties
  hangen mee om (#144-patroon: Document/Change/RuleChunk/`ClaimSource` op
  `SourceId`, `Conflict` op zijn Source-velden, BanEntry/Erratum/Correction
  op de URL-vorm). Bronnen die bewust dezelfde URL delen (zoals de Rules
  Hub-PDF/HTML-drieling, elk met een eigen Parser) zijn geen near-duplicaat
  en blijven ongemoeid. Beide stappen zijn idempotent en stil — geen nieuwe
  UI, alleen een run_log-regel; de bestaande bron-herkomstweergave (bron-
  dossier, §4.1 hierboven) toont een geadopteerde bron meteen correct.
- **Bron-dossier** (#171, spiegelbeeld van #167) — per bron in één oogopslag
  wat die aan het systeem heeft toegevoegd en of dat compleet verwerkt is.
  Herkomst (bovenstroom): welke bron-feed het artikel ontdekte, of
  "handmatig". Opbrengst (benedenstroom), met aantal + korte lijst per soort:
  documenten en regelsecties (`SourceId`), changes/wijzigingen (`SourceId`),
  bans/errata/geverifieerde rulings (`SourceUrl`, genormaliseerd tegen
  `Source.Url`) en community-claims (via `ClaimSource`, directe FK). Ver-
  werkingsstatus uit het run_log-grootboek: de laatste scan (wanneer,
  uitkomst) plus vervolgstappen (classify op changes van deze bron, claims-
  mining op het document) met hun eigen status. Een compleetheidssignaal
  (`SourceDossierCompleteness`, pure Domain-functie) leidt daaruit af:
  *volledig* (scan ok + geen hangende/mislukte stap + opbrengst), *onvolledig*
  (scan of vervolgstap mislukt/hangt), *leeg* (scan ok, niets opgeleverd —
  kan legitiem zijn) of *nooit gescand*. Semantische volledigheid ("zijn
  écht alle rulings uit dit artikel gehaald?") is niet hard te garanderen —
  de UI is daar expliciet over en linkt door naar het ruwe document. Een
  re-triggerknop draait scan (en daarmee classify) opnieuw voor déze ene
  bron. Bronnen met een gefaald/onvolledig signaal staan ook als
  signaalregel in het kennis-gaten-rapport (§4.5), met doorklik naar het
  dossier. Uitklap in de bestaande bronnentabel, alles geprojecteerd op
  bestaande data (#127-patroon) — geen embeddings, geen LLM.
  *Route* `/admin` (uitklap per bron) · *endpoint*
  `GET /api/admin/sources/{id}/dossier`.
- **Bronnen negeren met reden** (#180) — de feed-crawl (AutoApprove op
  officiële domeinen) registreert ook merch-/toernooi-/preorder-artikelen als
  trust-1-bronnen, die niets aan de kennisbank toevoegen. `Source.IgnoredAt`
  + `IgnoreReason` (nullable) markeren dat bewust en blijvend — nadrukkelijk
  ANDERS dan `Enabled` (dat blijft "tijdelijk uit"; een genegeerde bron mag
  `Enabled` gewoon op true laten staan). Genegeerd ⇒ de scan-lus
  (`IngestService.ScanAsync`) én de verwerkende consumers (claims-/clarify-
  mining, ban-/errata-extractie, regelsectie-indexering, het kennis-gaten-
  rapport — #180-review, volledig bereik in ARCHITECTURE §6.2) slaan de bron
  over (geen HTTP-fetch, geen LLM-kosten, geen aandachtssignalen) en de bron
  verdwijnt uit de standaard bronnenlijst (het
  publieke `/api/sources` filtert 'm eruit); een "Genegeerd (N)"-chip in de
  admin-bronnentabel toont ze alsnog, met reden en een "Terugzetten"-knop.
  Negeren is GEEN delete: bestaande `Document`/`Change`-rijen blijven staan,
  en de bron-rij zelf blijft bestaan — dezelfde bescherming tegen
  stille heraanmaak als de #167/#175-tombstone voor een verwijderde
  feed-bron (`FeedCrawlService`'s known-URL-dedup ziet de rij gewoon nog
  staan, dus die adopteert of hercreëert 'm nooit; de near-duplicaat-
  samenvoeging slaat een groep met een genegeerde rij erin bovendien
  volledig over, met run_log-melding — een merge zou de negeer-beslissing
  stil ongedaan kunnen maken). **Negeer-kandidaten**:
  de admin-bronnenlijst-projectie (`SourceListService`) berekent per bron —
  goedkoop, vier gebatchte tellingen ongeacht het aantal bronnen, geen
  aparte tabel — of die na ≥2 voltooide scans nog steeds niets heeft
  opgeleverd (0 `Change`, 0 claims via `ClaimSource`, 0 clarify-mining-
  rulings via `Correction.Provenance`); zo'n bron krijgt een hint ("levert
  niets op — negeren?") in de lijst. De beheerder beslist altijd zelf — geen
  automatisch negeren. *Route* `/admin` (bronnentabel + genegeerd-chip) ·
  *endpoints* `GET /api/admin/sources`,
  `POST /api/admin/sources/{id}/ignore|unignore`.
- **Temporele precedentie** (#168) — naast gezag (`TrustTier`) telt nu ook
  wanneer iets gepubliceerd/bijgewerkt is. `Source.PublishedAt` (uit de
  bron-feed-artikeldatum) en `Source.UpdatedAt` (detectiemoment van een
  échte content-wijziging) zijn zichtbaar als "geldig sinds"/"laatst
  bijgewerkt" op de regelsectiepagina. Bij gelijk gezag beslist recency —
  zie §4.4 voor de errata- en `/ask`-toepassing.
  *Route* `/rules/[code]` · *endpoint* `/api/rules/section/{code}`.

### 4.2 Kaarten

- **Kaartbrowser** — alle kaarten met facetten (mechaniek, domein, tag, set) en
  semantisch zoeken op betekenis; facetten zijn overal klikbaar naar een
  gefilterde browser. *Route* `/cards` · *endpoints* `/api/cards`,
  `/api/cards/facets`, `/api/cards/search`.
- **Kaartdetail** — het kaart-dossier: stats en tekst (met icoontokens),
  gekoppelde regels/errata, ontdekte interacties, en "similar-why" (semantische
  uitleg waarom kaarten op elkaar lijken) met versies/varianten. De
  interactie-lijst leest sinds #258 **beide** interactielagen: primair de
  gereïficeerde laag (#226 — die de ROL agent/patient en de losse
  window/status/cost-condities meedraagt, en alleen toont wat de promotiepoort
  heeft goedgekeurd), aangevuld met de oude paar-lexicale tabel voor buren die
  de opvolger nog niet kent. Die aanvulling is een expliciet eindige
  **migratiebrug**: hard omschakelen zou vandaag het kaartdetail van 94 kaarten
  naar nul zichtbare interacties brengen, omdat de nieuwe mining pas 1,4% van de
  kaarten heeft bereikt (geblokkeerd door #281). De brug vervalt zodra die
  dekking op orde is — zie `InteractionService.NeighborsAsync` voor het
  criterium. Bij meerdere
  errata over dezelfde kaart (#168) wint de tekst met de hoogste
  temporele precedentie (TrustTier, dan nieuwste `EffectiveFrom`) — die staat
  bovenaan met "geldig sinds \<datum\>"; oudere versies blijven zichtbaar,
  gemarkeerd als eerdere versie.
  *Route* `/cards/[id]` · *endpoints* `/api/cards/{id}`, `/api/cards/{id}/rules`,
  `/api/cards/{id}/interactions`, `/api/cards/{id}/similar`,
  `/api/cards/{id}/similar/{otherId}/explain`.
- **Kaart-dossier (verdieping)** — op dezelfde pagina: geverifieerde rulings
  (kaart-scoped én naam-vermeldingen, met klikbare §-verwijzingen),
  geaccepteerde claims met trust-label en bron/citaat, relaties achter
  dezelfde reviewpoort als de graph-projectie, en de volledige ban-historie
  per variantgroep. *Endpoint* `/api/cards/{id}/dossier`.
- **"In decks"** *(#15 golf 1 spoor B)* — deck-gebruikssignaal op basis van de
  Piltover Archive-decks: het aandeel van de 500 meest recent bijgewerkte
  decks (PA's `updatedAt`, een vaste poolgrootte i.p.v. een kalendervenster —
  zodat de noemer stabiel blijft terwijl de deck-backfill nog loopt) dat de
  kaart speelt, gematcht op de canonieke kaart over champions/hoofddeck/
  runes/battlefields (sideboard, bench en de 1-op-1 legend tellen bewust
  niet mee — geen kernidentiteit van het ingeleverde deck). Altijd het
  percentage mét het absolute aantal en de noemer ("N van M recente decks"),
  plus gemiddeld aantal exemplaren wanneer gespeeld en de top vijf
  mede-gespeelde kaarten (co-occurrence). Onder de 20 recente decks
  (drempel) toont het blok alleen de absolute aantallen — geen misleidend
  percentage op een kleine noemer — met een "nog te weinig deckdata"-notitie;
  bij nul decks een expliciete lege staat. Link naar de deck-browser
  (spoor A, `/decks`). *Endpoint* `/api/cards/{id}/dossier`
  (`deckPopularity`-veld).
- **Variantgroepering op basisnaam** — "Naam (Alternate Art)" telt als dezelfde
  kaart; de naamloze printing is canoniek, ook toekomstvast bij herdrukken
  (canonical-flip). Alleen canonieke printings gaan de graph in.
- **Bronvolgorde kaart-sync** — de officiële Riot-gallery is leidend
  (onvoorwaardelijke upsert, ook voor namen en kaartvelden) en riftcodex
  vult daarna alleen aan — extra kaarten (JDG-promo's) en set-metadata;
  `CARD_SOURCE=riot|riftcodex` blijven expliciete overrides en het job-detail
  telt per bron (#150). Sinds #270 staat die voorrang expliciet in
  `CardMerge`: leidend schrijft onvoorwaardelijk (ook leeg — ontbreekt een
  veld in Riots payload, dan hééft de kaart het niet), aanvullend vult
  **alleen lege velden** en raakt gevulde nooit aan. Daardoor mag de
  aanvul-pass ook gaten dichten op kaarten die Riot al kent (Riot levert geen
  `supertype`) zonder Riot-data te kunnen beschadigen. Valt Riot uit, dan is
  riftcodex leidend — anders zou de kaartenset bevriezen zolang Riot plat ligt.
- **Bronvorm-normalisatie (riftcodex)** — de kaart-sync normaliseert
  riftcodex-vormen bij binnenkomst naar de Riot-gallery-vorm: ster-id's
  ("sfd-239\*-221" → "sfd-239-star-221") en streepjes-namen ("Soraka -
  Wanderer" → "Soraka, Wanderer" — alleen waar de komma-basisnaam al als
  kaart bekend is; bij conflict op dezelfde printing wint de bestaande
  Riot-naam). Een idempotente reparatiestap in de sync voegt eerder
  ontstane dubbelen samen op (set, collector-nummer, variant-suffix) en
  hangt alle verwijzingen (bans, errata, interacties, rulings, relaties,
  claims, variant-verwijzingen) mee om; run_log meldt hoeveel (#144).
- **Kaarttekst-icoontokens** — tokens als `:rb_energy_1:` renderen als **Riots
  eigen glyphs** (#257): de 22 officiële icoon-SVG's staan gevendord in
  `rb-web/static/glyphs/` (opgehaald met `scripts/fetch-glyphs.sh`) in plaats
  van zelfgetekende vormen. Injectie loopt via `$lib/rbtokens.ts`, veilig
  ge-escaped vóór injectie en achter een allowlist — een onbekend token blijft
  letterlijk staan in plaats van een gebroken afbeelding te worden, en als een
  glyph niet laadt verschijnt de tokentekst zelf. Ze schalen mee met hun
  tekstregel (ondergrens 14px, omdat het binnenwerk daaronder dichtslibt) en
  `app.css` corrigeert per thema dat Riot ze voor een donkere UI tekent.
- **Presentatievelden uit de bron** *(#269/#270)* — de kaart-sync bewaart nu
  ook wat Riot bij elke kaart meelevert: afmetingen, dominante kleuren,
  `accessibilityText`, illustrator, `mightBonus`, het losse `effect`-blok,
  markers (`New`) en de publieke kaartcode. Zichtbaar effect: **battlefields
  renderen liggend** in het deckgrid en de kaartlijst in plaats van
  bijgesneden te worden (66 van de 1178 kaarten zijn liggend, en er zitten er
  drie in elk deck), elke kaartafbeelding draagt een echte alt-tekst, de
  dominante kaartkleur dient als laadkleur, en de kaartpagina toont de
  illustrator als krediet, de might-bonus, het Effect-blok, de kaartcode en de
  "Nieuw"-markering. Voor kaarten die alléén via riftcodex binnenkomen leidt
  `CardPresentation` af wat af te leiden valt (de maat staat in de
  afbeeldings-URL; de alt-tekst stellen we samen uit naam, type en kaarttekst).
  Harde grens: zo'n samengestelde alt-tekst hoort uitsluitend in een `alt=` —
  hij wordt nooit als Riots officiële kaarttekst getoond en gaat nooit de
  kennisbank of een LLM-prompt in. Afgeleid is niet officieel.
- **Mechanieken per kaart — gelezen, niet geraden** *(#211)* — de mechaniek-
  facetten, de `HAS_MECHANIC`-edges in de graaf en het mechaniek-blok op de
  kaartpagina komen uit `Card.Mechanics`, en dat veld wordt sinds #211
  deterministisch uit de kaarttekst zélf afgeleid: Riot drukt élk keyword
  gebracket af (`[Equip]`, `[Assault 2]`). Meting over de 1429 kaartteksten met
  tekst: 31 verschillende keywords, állemaal in die vorm — slechts ~3% van de
  vermeldingen staat érgens zónder haken. Magnitudes blijven bij hun familie
  ("Assault 2" en "Assault 3" zijn beide `Assault`, nooit een eigen facet).
  Het LLM doet nog uitsluitend wat die vorm niet kan: per kaart beoordelen of
  een bekend keyword dat er *zonder* haken in staat daar als spelterm wordt
  gebruikt ("Equip :rb_rune_body:") of als gewoon Engels woord ("Repeat this
  gear's play effect"); dat oordeel wordt achteraf tegen dezelfde gesloten
  lijst nagerekend en kan alleen toevoegen. **Merkbaar gevolg:** valt rb-ai uit
  — een verwacht pad — dan hebben kaarten nog steeds hun mechanieken, dus
  blijven de facetten, de graaf en het kaart-dossier gevuld; alleen de
  afgeleide triggers/effects wachten op de volgende run.

### 4.3 De vraagbaak (`/ask`)

- **Vraag-router** — classificeert de vraag (Ruling / Definitie / Kaart /
  Legaliteit / Toernooi) en kiest per type de antwoordstructuur en de
  bronnen-bias.
- **Scheidsrechter-format** — Oordeel → Zekerheid → Uitleg → Regelbasis → Let
  op, met de essentie in één oogopslag.
- **Citaties & bewijs** — uitklap-citaties tonen de regel plus de ouderregel
  voor context (#39); betrokken kaarten als bewijs; widget-markers
  `[[rule:…]]` / `[[card:…]]` worden interactieve blokken. Elke citatie toont
  ook "geldig sinds"/"laatst bijgewerkt \<datum\>" van haar bron (#168); bij
  twee even gezaghebbende (gelijke TrustTier) fragmenten die in de fusie-
  rangorde naast elkaar staan, krijgt het recentste voorrang.
- **Board-state-foto's (vision)** — een foto meesturen; het antwoord redeneert
  over het herkende bord.
- **Streaming + voorlezen** — het antwoord komt woord voor woord binnen
  (NDJSON-proxy) met vangnet naar de niet-streamende route; een
  voorlees-knop leest het antwoord voor (speechSynthesis).
  *Routes* `/ask`, `/ask/stream` · *endpoints* `/api/ask`, `/api/ask/stream`.
- **Navigatiebestendige vraagsessie** (#248) — vraag, antwoord en de lopende
  stream leven in een sessie-store buiten de pagina-component
  (`$lib/askSession.svelte.ts`), niet in component-state. Navigeer je weg van
  `/ask` en kom je terug, dan staat het antwoord er nog; een lopende search
  loopt gewoon door en is bij terugkomst compleet of nog streamend. Het
  huidige antwoord + de vraag worden ook in `localStorage` bewaard (naast de
  bestaande vragenlijst, eigen sleutel, 12 uur houdbaar), zodat een page
  reload het terugbrengt. Een antwoord dat níet afgemaakt is — verbinding
  weg, zelf gestopt, of door de reload afgebroken — komt terug als
  **onderbroken**: het deelantwoord blijft staan met een expliciete melding,
  en feedback, "vastleggen als ruling" en doorvragen blijven daarbij weg (op
  een half antwoord bouw je niet verder). Omdat de vraag doorloopt zonder dat
  je erbij staat, kan hij expliciet **gestopt** worden (AbortController) en
  kan een blijvend antwoord **gewist** worden. Anoniem net zo goed als
  ingelogd; cross-device-sync via het account is bewust géén onderdeel
  hiervan. Zonder JavaScript blijft de gewone form action het antwoord
  server-side renderen.
- **Deck-meta als kennislaag 3** (#267) — bij kaart- en lijst-/meta-vragen
  waarin een kaartnaam herkend is, gaat het deck-gebruikssignaal van het
  kaartdossier (aandeel van de recente Piltover Archive-decks, gemiddeld
  aantal exemplaren, top-co-occurrence — dezelfde `DeckPopularityQuery` als
  het "In decks"-blok) mee in de prompt als expliciet gelabeld
  `DECK-META`-blok: kennislaag 3, de zwakste laag — community-metagegevens,
  geen officiële regel. De omgangsregels in het blok dwingen af dat meta een
  antwoord kleurt maar nooit een Oordeel draagt. Onder de decks-drempel
  (dunne bank) staan er absolute aantallen in plaats van een
  percentage-claim. **Hotpath bewaakt:** elke andere vraag — in het bijzonder
  een regelvraag zonder kaarten — doet géén enkele deck-query
  (`DeckMetaRetrieval.ShouldRetrieve`); vuurt het kanaal wel, dan draait het
  concurrent onder de query-rewrite en kost het geen extra wandkloktijd. De
  trace toont de meegegeven kaarten met een `deckmeta:`-prefix in het
  kennislagen-veld.
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
  kaartcontext, banlijst, claims, deck-meta, misvattingen) draaien concurrent op een
  eigen databasecontext per kanaal, en een kleine LRU-cache slaat de
  rewrite-call over bij een herhaalde/gelijksoortige vraag. Uitval van één
  kanaal blijft altijd gedegradeerd (leeg kanaal + trace-marker), nooit een
  500; de uitkomst (antwoord, citaties, prompt) blijft byte-voor-byte gelijk
  aan de oude seriële pipeline.
- **Brein-GraphRAG-retrieval (achter flag, default UIT)** (#228) — een
  optionele verrijking die de context aanvult met de kennisgraaf: de
  fase-4-`RetrievalOrchestrator` (entity-linking → β(q)-router →
  Local/Global/Path/Drift → gegate trust → context-bundeling) voegt één
  trust-gelabeld `BREIN-CONTEXT`-blok (subgraaf-fragmenten, pad-onderbouwing als
  citaties, gating-beslissing) aan de prompt toe en legt een `AnswerTrace`
  vast (verantwoording per antwoord, zichtbaar in de Brein-verkenner). Zit
  achter een feature-flag: **default UIT ⇒ `/ask` gedraagt zich exact zoals
  hierboven** (geen brein-call, geen extra latency). De vlag is sinds #254
  bedienbaar in **beheer → Brein → /ask-retrieval** (env
  `BREIN_RETRIEVAL_ENABLED` blijft de startwaarde) en werkt direct, zonder deploy.
  Staat de flag aan, dan is de verrijking best-effort met hard latency-budget en
  nette terugval op de bestaande retrieval bij elke fout — nooit een 500 voor de
  gebruiker.
- **Eigen ask-geschiedenis** — uitklap-paneel "Mijn eerdere vragen" op `/ask`
  (dicht standaard): de laatste 20 eigen vragen met tijdstip, vraagtype en het
  antwoord (gerenderd zoals het origineel, met dezelfde sanitize/widgets),
  plus of de vraag agentic beantwoord werd. Scope is altijd de vrager zelf —
  ingelogd op het account (`user_id`), anders op de gehashte IP-koppeling
  (`ip_hash`, zie §4.6/§5) van het huidige request; geen id-parameter, dus
  geen enumeratie van andermans historie mogelijk (#157). *Endpoint*
  `/api/ask/history`.
- **Ruling vastleggen vanuit het gesprek** (#166) — bij een antwoord een
  compacte actie "Vastleggen als ruling": de uitspraak (voorgevuld vanuit het
  antwoord, bewerkbaar), onderwerp/scope (kaart/§-sectie/algemeen) en een
  verplichte bronverwijzing ("waar besloten" — Discord-link, officiële post,
  toernooibeslissing, of een vrije citatie). Alleen zichtbaar voor ingelogd of
  beheerder; anoniem ziet de actie niet. Autoriteit bepaalt de route (§4.4):
  beheerder ⇒ direct geverifieerd, ingelogde gebruiker ⇒ voorstel in de
  reviewqueue. *Endpoint* `/api/ask/ruling`.

### 4.4 Kennisbank / het brein

- **Kennispiramide** — officieel > geverifieerde rulings > primer > community >
  meta, expliciet gelabeld in de prompt en het antwoordformat. Sinds #267 is
  ook laag 3 (meta) gevuld: het deck-gebruikssignaal gaat bij kaart-/
  lijstvragen als gelabeld `DECK-META`-blok mee (zie §4.3).
- **Temporele precedentie** (#168) — naast gezag (TrustTier) telt nu ook
  recency als tie-breaker: `Precedence.Compare` (pure Domain-functie) kiest
  bij gelijke tier de nieuwste datum, met een ontbrekende datum als oudste
  (nooit geraden). Toegepast op de errata-resolutie (welke tekst NU geldt
  voor een kaart, §4.2) en op de `/ask`-citatie-volgorde (§4.3). Waar een
  nieuwere officiële errata-bron een oudere over dezelfde kaart aantoonbaar
  overtreft, toont het errata-beheeroverzicht een kandidaat-supersede-signaal
  (puur berekend, geen automatische verwijdering) met verwijzing naar de
  opvolger.
- **Game-primer** — ~12 concept-docs (beurt, resources, combat, prioriteit,
  zones, scoren, keyword-gedrag) gedistilleerd uit de regels, met draft →
  approve in beheer; altijd als achtergrondblok mee in `/ask` en read-only op
  de site. *Route* `/primer` · *endpoint* `/api/knowledge`.
  **Nederlandse weergave (#266)** — de primer wordt canoniek in het Engels
  opgeslagen (#187, hierboven), maar `/primer` is een bezoekerspagina op een
  Nederlandse site en toont dus Nederlands, met de Engelse speltermen
  onvertaald (Runes, Battlefields, showdown, Might, Bonus Damage, …). De
  vertaling gebeurt **bij de generatie**, niet bij de weergave: ze zit in
  `knowledge_doc.body_nl` naast de canonieke Engelse `body`, en gaat daarmee
  door dezelfde draft/approve-poort — de beheerder keurt exact de tekst goed
  die de bezoeker leest, en ziet beide versies naast elkaar (bewerkbaar).
  Her-generatie vervangt beide teksten samen en zet de status terug op draft,
  zodat er geen tweede waarheid kan ontstaan. Een vertaling die een spelterm
  vernederlandst of een §-verwijzing laat vallen wordt door een
  glossarium-waarborg afgekeurd; dan (en bij AI-uitval) toont de pagina de
  canonieke Engelse tekst met een expliciete melding erbij — nooit een leeg
  vak. De conceptnamen zijn handgeschreven Nederlandse titels bij
  `PrimerTopics` (geen LLM, dus geen drift); een door de beheerder aangepaste
  titel wint. Retrieval, embedding en `/ask`-context blijven onveranderd op de
  Engelse tekst. Geen vertaallaag over officiële regel- of kaartteksten — daar
  zou vertalen interpretatierisico introduceren (#189).
- **Claims-pipeline** — community-interpretatie als geparafraseerde claims met
  bron-trust, corroboratie (aantal onafhankelijke bronnen) en een officiële
  toets; geaccepteerde claims doen mee als eigen "Community-consensus"-kanaal in
  `/ask`.
- **FAQ-/clarificatie-concept-extractie** (#177, zie ook §4.1) — anders dan de
  claims-pipeline is de bron hier per definitie officieel, maar niet elk
  geëxtraheerd concept wordt blind `verified`: een hybride poort eist grounding
  (citaat in de bron) én een resolvend onderwerp-anker
  (mechanic/rule_section/card/concept). Alleen dan direct `verified` met een
  gefocuste embedding zodat de losse verduidelijking — niet de hele FAQ-slab —
  semantisch vindbaar is; anders `unverified` met reden, de reviewqueue in.
- **Evolutie-raamwerk** — set-release-keten, groeiend mechaniek-vocabulaire
  (keyword-kandidaten → reviewqueue → re-mine) en een kennis-gaten-rapport dat
  meet waar de bank aantoonbaar niets weet. Het vocabulaire groeit uitsluitend
  langs die queue: een nieuwe set introduceert nieuwe keywords in gebrackete
  vorm, de mining oogst ze deterministisch als kandidaat mét kaart-telling en
  bewijssnippet, en pas een menselijke acceptatie maakt er vocabulaire van
  (#211: een LLM-oordeel kan nooit zelf een term toevoegen; een afwijzing geldt
  óók voor de deterministische route).
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
  Sinds #317 zijn de eindpunten begrensd op de vijf gemeten knoopsoorten
  (Card/Mechanic/Concept/RuleSection/Claim): een gereviewde relatie waarvan een
  ref naar een andere knoopsoort wijst, wordt bij de rebuild bewust niet als
  edge geprojecteerd (de relatie zelf blijft in Postgres staan). Sinds #321 is
  dat niet-projecteren nooit stil: de rebuild telt wat Neo4j wérkelijk schreef
  en meldt het verschil met het aanbod per oorzaak (eindpunt-soort buiten de
  projectie vs. ref zonder knoop) als warn-regel in run_log en in het
  job-detail; de agentic voorstellen-poort weigert een eindpunt buiten de vijf
  bovendien al aan de bron, mét reden in de terugkoppeling.
- **Rulings in de graph** (#191) — geverifieerde rulings/clarificaties krijgen
  een `Ruling`-knoop (alleen `status=verified`; een `kind`-property
  onderscheidt clarify-mining/chat-ruling/review-note) met dezelfde
  `ABOUT`-resolutie als Claim (naar Card/Mechanic/RuleSection/Concept) en een
  `SUPPORTED_BY`-edge naar de bron — de brein-API en de graph-verkenner tonen
  een ruling voortaan bij haar onderwerp, niet alleen via semantisch zoeken.
- **Afgeleide kennis in het Engels** (#187) — de mining-prompts (claims,
  primer, relatie-`explanation`), de relatie-kind-labels en de
  claim-toets-redenering (`OfficialCheck`/`ClaimJudge` → de weerleg-/
  misvattingstekst op `Claim.StatusReason` die `/ask` gebruikt, #125)
  extraheren/synthetiseren in het Engels, dicht bij de officiële bewoording —
  geen vertaalstap; UI en `/ask`-antwoorden blijven Nederlands. Een
  wipe-en-regenereer-job (`regenerateknowledge`, zie §4.5) gooit de bestaande
  Nederlandse afgeleide laag schoon weg i.p.v. in-place te vertalen.
  Uitzondering sinds #266: de primer krijgt bij de generatie een Nederlandse
  wéérgave naast de canonieke Engelse tekst, omdat `/primer` de enige plek is
  waar afgeleide kennis rechtstreeks als leespagina bij de bezoeker komt (zie
  Game-primer hierboven). De opslag blijft leidend Engels.
- **Graph-verkenner** — interactieve kaart↔mechaniek↔regel-visualisatie.
  *Route* `/graph` · *endpoint* `/api/graph/neighbors`.
  **Hover-preview en knoop-detail (#252)** — hoveren over een knoop toont een
  vaste hovercard (patroon uit #243/#244: `position: fixed` op paginaniveau
  met viewport-clamp, boven/onder-flip en `pointer-events: none`, bewust
  buiten de horizontaal scrollende graaf-container die hem zou clippen): bij
  een kaart naam + afbeelding + type/domeinen, bij mechaniek/regelsectie/
  claim/concept het label met een korte uitleg uit de brein-projectie
  (facetten tonen hun kaart-telling). **Klikken selecteert** in plaats van weg
  te navigeren: de verkende graaf-positie blijft staan, de gekozen knoop
  krijgt een accent-ring en de volledige info verschijnt in een detailpaneel
  ónder de graaf — voor kaarten de kaartweergave (afbeelding, tekst met
  rb-iconen, type/domeinen/stats, ban- en legaliteitsstatus, mechaniek- en
  tag-chips) plus een compact dossier-blok (rulings, community-claims,
  ban-historie, met "N van totaal"), voor overige soorten de compacte
  brein-projectie. Het paneel draagt de bestaande doorklik-links plus "Verken
  vanaf deze knoop" (dát navigeert wél en maakt de knoop het nieuwe centrum);
  ctrl/cmd-klik op een knoop opent hem nog gewoon in een nieuw tabblad. Het
  detail komt client-side via de proxy `GET /graph/node?ref=` (kaarten:
  `/api/cards/{id}` + `/api/cards/{id}/dossier`, overige: `/api/brain/node`).
  Knopen hebben een ruime onzichtbare trefzone zodat hover en klik ook met een
  vinger raak zijn.
- **Self-learning** — negatieve/positieve feedback → reviewqueue →
  geverifieerde correcties (embedded, gezaghebbend) die semantisch terugkomen
  in de prompt.
- **Autoriteitsroute voor de antwoord-beïnvloedende laag** (#166) — wie mag
  een Correction rechtstreeks `verified` maken (telt meteen mee in `/ask` en
  `/rulings`) is streng server-authoritatief: alleen de beheerder (X-Admin-Key)
  krijgt dat pad, direct embedded via hetzelfde verify-pad als de
  reviewqueue. Een ingelogde gebruiker legt altijd een `pending`-voorstel vast
  (nooit direct verified/geëmbed) — precies dezelfde poort als de
  review-notitie-promotie (#124); pas na beheerder-goedkeuring (het bestaande
  verify-pad) telt het mee. Anoniem wordt bij dit pad geweigerd (401) — geen
  invoer zonder identiteit. Dit is de anti-vergiftigingsgrens uit
  docs/KNOWLEDGE.md in code. Sinds #177 bestaat er een derde, niet-menselijke
  route: `ClarificationMiningService` schrijft ook rechtstreeks `verified`,
  maar uitsluitend voor concepten uit een TrustTier-1-bron **die bovendien de
  hybride poort halen** (grounding: citaat écht in de bron; anchoring:
  onderwerp resolvet) — een LLM-parafrase zonder bewijs of met een verzonnen
  anker haalt de poort niet en gaat `unverified` de reviewqueue in (met reden),
  precies zoals een gebruikersvoorstel. De trust-1-poort blijft dus een
  beheerdersbeslissing (wie een bron official maakt) én de grounding/anchor-
  poort houdt de LLM eerlijk; het is geen blanket-uitzondering op de
  anti-vergiftigingsgrens. Een vierde weg (#184): een beheerder-opmerking op
  een `unverified` clarify-item triggert een deterministische her-toets van
  dezelfde hybride poort voor dát ene item (`CorrectionReevaluationService`)
  — nog steeds machine-gecontroleerd (grounding/anchoring, evt. met een
  anker-correctie uit de opmerking), geen directe menselijke override van
  Status; alleen de beheerder (X-Admin-Key) kan de actie triggeren. Een
  al `verified` of `rejected` item degradeert/heropent nooit via dit pad.

### 4.5 Beheer (`/admin`)

- **Beheer-console** (#214) — `/admin` heeft een **eigen console-shell**, los
  van de publieke zijbalk: een beheer-zijbalk met "← naar de site", nav met
  tel-badges (open correcties → Reviewqueue, aantal bronnen → Bronnen),
  Gevarenzone in rood en een thema-schakelaar; mobiel een bovenbalk + drawer.
  De publieke chrome wordt binnen `/admin` onderdrukt en bij terugkeer naar de
  site meteen hersteld. Drie kernschermen zijn naar het "Domains"-design
  herbouwd — **Overzicht** (dashboard met "Nu bezig"-voortgang, pad-knoppen,
  telling-tegels, rapport-links, een graph-drift-tabel en recente runs),
  **Reviewqueue** (relatie-aanbevelingsstrip + gekleurde bulk-balken, zie
  "Reviewqueues") en **Bronnen** (bron-cards met trust-badge, cadans-chip,
  negeer-kandidaat-hint en negeren-met-reden) — met álle bestaande jobs,
  form-acties en data-bindings ongewijzigd. *Route* `/admin`
  (+ `/admin/overview/[kind]`).
- **Jobs met live voortgang** — de "Alles bijwerken"-keten en losse jobs
  (scan, feeds, cards, embed, mine, rules, rules-index, bans, graph, primer,
  interactions, scout, classify, consolidatechanges, claims, clarify, relations,
  relationtriage, setrelease, decks, benchmark, benchmarksweep,
  regenerateknowledge) draaien via
  `JobRunner` met live-voortgang en run_log. *Route* `/admin` · *endpoints*
  `/api/admin/jobs/{name}`, `/api/admin/status`, `/api/admin/logs`.
- **Embed-uitval zichtbaar in beheer** (#282) — valt Ollama om tijdens een
  embed-run (de kernel schoot `llama-server` af op zijn 2,5 GiB-cap), dan slaat
  de pijplijn de gefaalde batch over, loopt door met de rest, en meldt achteraf
  **per oorzaak** hoeveel kaarten/regelsecties bleven liggen — "5xx
  (model-runner omgevallen?)×3", "onbereikbaar×1", "4xx (model niet
  gepulld?)×2", net zoals #251 dat voor rb-ai-uitval doet. Die uitsplitsing
  landt als `run_log`-regel (kind `embed`, status `error`) ongeacht wie de
  pijplijn startte — beheer-knop, job óf scheduler-tick — en de cockpit toont
  hem als eigen paneel bovenaan. Voorheen was de degradatie stil: de run meldde
  het aantal *te-doen* kaarten als "geembed" en de scheduler logde hooguit
  "Ollama onbereikbaar?" naar de containerlog, waar niemand kijkt — kaarten
  liepen zonder embedding rond en semantisch zoeken verslechterde ongemerkt.
  **Het alarm dooft door herstel, niet door veroudering**: ook een geslaagde
  run schrijft zijn regel (geen enkel UI-pad deed dat, dus een oude foutregel
  bleef anders eeuwig de nieuwste embed-regel), en de cockpit leest een eigen
  `lastEmbed`-veld in plaats van de 15 nieuwste logrijen — daar wordt een
  nachtelijke embed-fout vóór de ochtend uit weggedrukt door de rijen van de
  latere stappen. Alle jobs melden de uitval ook in hun eigen ketendetail, in
  plaats van een omgevallen stap als geslaagd te tonen — de embed-job, beide
  regel-index-jobs (`rules` en de incrementele `rules-index` uit #258) en de
  set-release-keten.
  **Niet-geëmbedde items blijven staan** voor de volgende run (de pijplijn
  selecteert op ontbrekende embedding), en bij de regel-index wordt de hele
  bron overgeslagen in plaats van een complete index door een gatenkaas te
  vervangen. **Doorlopen, maar niet eindeloos**: na drie opeenvolgende gefaalde
  batches stopt de run en meldt dat, zodat een dode Ollama de beheer- en
  schedulerlus niet urenlang bezet houdt. Het gebruik is begrensd in plaats van
  het plafond verhoogd: `EMBED_BATCH_SIZE` (16 → 8) en een tekenbudget
  `EMBED_BATCH_CHARS` (6000), omdat de piek in het verzoek zit en
  regel-secties (streefgrens 2400 tekens) veel zwaarder zijn dan kaartteksten.
  Model en dimensie blijven ongewijzigd — een kleinere batch is geen ander
  model. **Dat tekenbudget stond tot #293 op 8000, en dat bleek gemeten precies
  de omvalwaarde** (7000 tekens → HTTP 200, 8000 → HTTP 400 in 3 van de 3
  pogingen, met een OOM-kill van `llama-server` per poging): de begrenzing stond
  op de klip in plaats van eronder, waardoor de card-errata-bron elke run
  oversloeg. Sindsdien 6000, met de meetwaarden en een regressietest in de code,
  een env-plafond op de gemeten veilige grens, en een **harde kap op de
  itemlengte** — een enkele chunk of kaarttekst boven het budget wordt op het
  budget gekapt in plaats van als solo-verzoek de OOM uit te lokken. Die kap
  geldt alleen voor de embed-invoer (de opgeslagen en getoonde tekst blijft
  volledig) en wordt altijd gemeld, nooit stil. De 4xx-melding noemt niet langer
  "model niet gepulld?" als eerste hypothese maar de OOM-hypothese, mét Ollama's
  ruwe foutbody erbij. *Route* `/admin` (paneel "Embeddings onvolledig" +
  logtabel) · *endpoint* `/api/admin/status` (`lastEmbed`).
- **De budgetgarantie geldt voor élke aanroeper, en een kap laat een spoor na**
  (#301/#299/#302/#303) — de kap uit #293 zat in de twee embed-pijplijnen, maar
  de tien andere aanroepplekken kenden het budget niet. De reële: een reviewer
  die een primer-draft bewerkt liet de geplakte tekst ongemeten embedden, en bij
  8000+ tekens is dat geen mislukte embed maar een OOM-kill van `llama-server` —
  een VM-breed geheugenincident dat als een hikje oogde. De begrenzing zit nu
  één laag lager, in de embed-service zelf: elke tekst wordt op het budget
  gekapt én elke aanroep in deelverzoeken geknipt, zodat geen enkele aanroeper
  er nog omheen kan. Een handmatige snee van 1500 tekens elders (de ad-hoc
  variant van hetzelfde probleem) is daarmee vervallen. **Een gekapte vector is
  voortaan herkenbaar op de rij** (`embedding_truncated_at` op kaarten,
  regel-chunks en primer-docs: null = volledige tekst, een getal = de kaplengte
  van dat moment), zodat "welke vectoren zijn partieel?" ook over een half jaar
  beantwoordbaar is en die rijen gericht her-embed kunnen worden zodra het
  budget omhoog kan — de markering verdwijnt vanzelf zodra de tekst weer past.
  Een run die kapte maar verder slaagde krijgt status `warn` in plaats van `ok`
  en verschijnt in beheer als eigen waarschuwing (kleur + tekst, geen fout); een
  echte uitval wint altijd. De melding noemt naast de kaplengte nu ook de
  **langste originele invoer** ("afgekapt op 6000 tekens (langste invoer
  20000)"), want alleen dát zegt of het budget knelt of ruim zit. En het
  env-plafond staat niet langer op 7000 — de waarde die de code zelf de klifrand
  noemt — maar op 6300, tien procent daaronder: bijstellen blijft mogelijk,
  jezelf op de rand zetten niet. *Route* `/admin` (paneel "Embeddings deels
  afgekapt") · *endpoint* `/api/admin/status` (`lastEmbed`).
- **Lopende job afbreken** (#253) — naast de "Nu bezig"-voortgangsbalk staat
  een **Afbreken**-knop (met bevestiging) die de lopende job of het lopende
  pad coöperatief stopt; binnen enkele seconden is `running` weer leeg en kan
  een volgende job starten. Al opgeslagen voortgang blijft behouden (de jobs
  committen per batch/watermark) — afbreken laat dus geen half feit achter.
  De afbreking landt als gewone afronding in `run_log` met status
  `cancelled` + de bereikte voortgang, en is als zodanig zichtbaar in "Recente
  runs" en bij de laatste-run-regel per job. Dat de afronding geschreven
  wordt is essentieel: de scheduler leest datzelfde grootboek, dus zonder die
  regel zou hij de afgebroken (nacht)run meteen opnieuw starten — precies wat
  er gebeurde toen `docker restart` de enige uitweg was. *Route* `/admin`
  (paneel "Nu bezig") · *endpoint* `POST /api/admin/jobs/cancel` (200 met
  `cancelled:false` als er niets draait) · *actie* `POST ?/cancelJob`.
- **Paden** (#190) — geordende ketens van bestaande jobs die vanzelf
  doorstromen (één klik = de hele keten), gedefinieerd in `JobPaths`
  (Infrastructure) en uitgevoerd door `PathRunner` als één gewone
  `JobRunner`-run onder de padnaam (dezelfde éénjob-gate, dezelfde
  live-Progress, dezelfde run_log-afronding als een losse job — de padnaam
  verschijnt zo vanzelf op `/api/admin/status`). Sinds #258 is het pad het
  ENIGE ketenmechanisme: "Alles bijwerken" en de nachtrun waren met de hand
  geschreven ketens met een eigen volgorde en zonder per-stap-historie, en zijn
  nu dunne aliassen op een pad. Vier los startbare paden, elk een fase van de
  keten: **Ingest-pad** (scan → rules-index → bans → classify →
  consolidatechanges — `consolidatechanges` #206: koppelt changes die hetzelfde
  event vanuit meerdere bronnen melden), **Kaart-pad** (cards → embed → mine →
  graph), **Kennis-pad** (claims → clarify → relations → relationtriage →
  primer → graph) en het nieuwe **Brein-pad** (breinentiteiten →
  breinmine-interacties → breinmine-predicaten → breinaudit-interacties #255 →
  breinprojectie → reason).
  #258 verschoof daarbij vier dingen: `rules`/`bans` ontbraken in het
  Ingest-pad (ze stonden alleen in "Alles bijwerken") en zijn toegevoegd als
  `rules-index` — de incrementele variant, want de bestaande `rules`-knop
  herbouwt bewust ALLES en hoort niet in een nachtelijke keten; `mine` verhuisde
  naar het Kaart-pad omdat mechaniek-mining kaart-afgeleid is; `primer`
  verhuisde naar het Kennis-pad, waardoor het aparte pad "Volledige regeneratie"
  (dat het Kennis-pad plus primer wás) is vervallen; en het Brein-pad is nieuw.
  De wipe zit nog steeds in geen enkel pad — die blijft de losse
  Gevarenzone-actie `regenerateknowledge` hieronder.
  **"Alles bijwerken" is daardoor wel duurder geworden**: er komen `classify` en
  `consolidatechanges` bij (beide LLM-werk) en `mine` draineert nu in plaats van
  één gecapte run te doen. In de normale situatie draineert die op run 1 — de
  extra kosten treden alleen op als er écht een achterstand ligt, wat van deze
  knop ook de bedoeling is. De keten synchroniseert bovendien eerst de kaarten en
  sluit als laatste af met de graph-projectie: `bans` resolvet naar kaart-id's
  (dus ná `cards`) en `graph` projecteert ook regelsecties (dus ná
  `rules-index`). Een per-run gecapte stap kan
  **Drain**-gedrag hebben:
  `PathRunner` herhaalt diezelfde job tot `JobOutcome.Drained` true is (geen
  per-run-cap meer geraakt) — de les uit een handmatige Fase 2-
  kennisregeneratie waarbij claims/clarify meerdere runs nodig hadden om hun
  cap niet meer te raken. Twee vangrails op die lus: de harde
  max-herhalingen-grens (standaard 10) én een no-progress-guard die stopt
  zodra twee opeenvolgende runs een identiek resultaat geven (zelfde detail
  én nog steeds niet gedraineerd — bv. een document waarvan al-bekende items
  het hele run-budget opeten); beide laten het pad gewoon doorlopen naar de
  volgende stap. Drained is machine-leesbaar mét vers-werk-semantiek
  (zojuist gefaalde items tellen NIET als resterend werk — een directe
  herhaling faalt vrijwel zeker opnieuw): claims/clarify/relations/relationtriage/decks
  via hun `CapHit`-veld (bij claims telt ook een hertoets-backlog groter dan het
  per-run-venster mee), mine via `Remaining − Failed`. Classify en
  consolidatechanges (#206) draaien bewust zónder Drain: beide zijn ongecapt
  (één run verwerkt de hele backlog/het hele kandidaten-venster), dus
  draineren zou alleen failures herkauwen. Geen string-matching op de
  detailtekst. Faalt een stap, dan stopt het pad netjes (status error); de
  al-gedraaide stappen blijven staan (de onderliggende jobs zijn zelf
  idempotent). De twee samengestelde ketens ("Alles bijwerken" en de nachtrun)
  zijn juist **best-effort** (#258): daar wordt de fout wél gelogd maar loopt de
  keten door — een nachtrun van uren mag niet stranden op één 5xx van rb-ai.
  Afbreken via beheer stopt ook zo'n keten: dat is een beslissing, geen storing. Elke (herhaalde) stap logt apart naar run_log (Kind =
  padnaam, Ref = stapnaam), via een eigen verse DbContext-scope per
  schrijfactie — nooit de context waarin een stap net crashte. Periodiek
  inplannen is mogelijk (zelfde `JobLedger`-vensterlogica als de losse jobs)
  maar bewust niet benut: Ingest/Kaart/Brein zitten al in de nachtrun-keten, de
  dure stappen van het Kennis-pad hebben hun eigen cadans als losse job (het pad
  erbij plannen zou ze twee keer per etmaal draaien), en `primer` hoort niet in
  een automaat omdat elke run drafts oplevert die een mens moet reviewen. *Route* `/admin` (sectie "Paden", boven de
  losse jobs) · *endpoints* `GET /api/admin/paths`,
  `POST /api/admin/paths/{name}` (meelift op `/api/admin/status`).
- **Relatie-triage** (#199 v1) — job `relationtriage`: per open
  relatievoorstel (Status "unreviewed", nog geen aanbeveling, niet
  gearchiveerd — een geparkeerd voorstel kost geen LLM-budget en krijgt
  geen aanbeveling) één retrieval-gegronde LLM-beoordeling —
  `accept`/`reject`/`unsure` +
  één-zin-motivering (Engels, met de geraadpleegde §/mechaniek/concept-refs
  erin gevouwen), opgeslagen als drie nullable velden op `Relation`
  (`Recommendation`, `RecommendationReason`, `RecommendedAt`). Bewust een
  AANBEVELINGS-machine, GEEN autoriteitspad (v1 laat de optionele
  auto-accept uit de issue expliciet ongebouwd): alleen een mens wijzigt
  Status, een al mens-beoordeeld voorstel wordt nooit her-getriaged. Cap
  ~40/run, gecapt met dezelfde vers-werk-semantiek als de andere
  triage-jobs. De reviewqueue (zie "Reviewqueues" hieronder) sorteert op
  aanbeveling (accept/reject eerst — de twee bulk-actionabele groepen) en
  toont motivering naast het bestaande bewijs; een **bulk-actie per
  aanbevelingsgroep** ("accepteer/verwerp alle N met aanbeveling X", met
  confirm()) loopt per item hetzelfde bestaande accept-/reject-pad na (geen
  nieuw autoriteitspad), alléén over unreviewed én niet-gearchiveerde
  voorstellen, en rendert alléén in de te-reviewen-weergave — telling,
  zichtbare items en actie-scope zijn zo hetzelfde universum. De bulk is
  TOCTOU-gefenced (adversariële review, finding 1): de UI stuurt de
  geladen groepstelling + de max `RecommendedAt` mee, en rb-api weigert
  met 409 ("groep is veranderd — ververs de pagina") zodra de herberekende
  groep afwijkt — een gelijktijdige triage-run (het kennis-pad) kan zo
  nooit items laten meebeslissen die de beheerder niet zag; dat zou de
  facto het auto-accept-pad zijn dat v1 níét heeft. *Endpoint* (bulk)
  `POST /api/admin/relations/bulk-decide` (400 bij ontbrekende/ongeldige
  velden, 409 bij een fence-schending, altijd alles-of-niets).
- **Kennis regenereren (Engels)** (#187) — eigen, zwaar gewaarschuwd
  admin-paneel (confirm-stap, geen kale "Start"-knop): job
  `regenerateknowledge` verwijdert de volledige LLM-afgeleide kennislaag
  (`claim` + embeddings/bewijs, `correction` — ALLE rijen, ook door mensen
  ingevoerde, `knowledge_doc` met kind="primer", `relation`) en reset de
  mining-markers (`Document.ClaimsMinedAt`/`ClarifiedAt`) zodat brondocumenten
  bij de volgende mine/clarify-run opnieuw worden opgepakt. Raakt NOOIT de
  bron-/mensenwerk-tabellen (`source`, `document`, `rule_chunk`, `card`,
  `errata`, `ban_entry`, `deck`/`deck_card`) — bewezen met een test die exact
  die tabellen seedt en na de wipe ongewijzigd telt
  (`KnowledgeRegenerationServiceTests`). Draait in één transactie, logt
  aantallen naar run_log, en genereert bewust NIETS automatisch opnieuw — de
  beheerder start daarna zelf primer → claims → clarify → relations. Bewust
  géén stap in "Alles bijwerken" en géén automatische trigger: uitsluitend een
  expliciete, eenmalige beheerdersactie na de deploy die de mining-prompts
  naar het Engels omzette. *Endpoint* `/api/admin/jobs/regenerateknowledge`.
- **Brein-mining resetten** (#263) — twee losse, gewaarschuwde
  Gevarenzone-acties (elk met confirm-stap) die ALLEEN de brein-mining-laag
  terugzetten, waar `regenerateknowledge` hierboven veel te grof voor is.
  Nodig omdat het mined-watermark van de interactie-mining "deze kaart leverde
  ooit een interactie-`Assertion` op" is: na de runs van 19–20 juli stonden
  ~800 kaarten afgevinkt met de extractie die #249 als ondeugdelijk
  vaststelde, waardoor de verbeterde extractie precies die kaarten zou
  overslaan en niet meetbaar zou zijn.
  Job `breinreset-interacties` verwijdert `interaction`,
  `interaction_condition`, `interaction_decision` en de `assertion`-rijen met
  `FactKind = interaction` (het watermark), en LICHT de grafstenen die de
  poort zelf schreef (`rejection_tombstone` met actor "gate" → `Lifted`, nooit
  hard-deleted — het audit-spoor blijft; grafstenen van de beheerder blijven
  gelden). Job `breinreset-volledig` doet hetzelfde plus `mechanic_predicate`,
  `canonical_entity`, `merge_candidate` en `merge_decision` — zodat ook de
  entiteit-/predicaat-extractie (#250) op een schone pool meetbaar is; de
  merge-historie moet daarbij mee (haar FK's staan op Restrict) en dat verlies
  staat expliciet in de bevestigingstekst, de telling en het run_log.
  De `mining_run`-historie blijft BEWUST staan: die PROV-O-activiteiten
  (model, prompt-versie, vocabulaire-snapshot, tellingen) zijn juist de
  baseline waartegen de #249-verbetering gemeten wordt. Raakt nooit claims,
  primer, correcties, relaties, kaarten, regels, bans of de OUDE lexicale
  `card_interaction`-laag — bewezen met regressietests
  (`BreinMiningResetServiceTests`). Draait in één transactie, telt terug wat
  er weg is en logt dat in `run_log` (kind `breinreset`, ref = de scope);
  bewust geen stap in "Alles bijwerken", geen pad en niet in de nachtrun, en
  chaint niets automatisch — de beheerder start daarna zelf
  `breinmine-interacties` (en bij de brede scope eerst `breinentiteiten`),
  gevolgd door `graph` + `breinprojectie`. De brein-cockpit (`/admin/brein`)
  wijst bij stap 1 naar deze acties. *Endpoints*
  `/api/admin/jobs/breinreset-interacties` en
  `/api/admin/jobs/breinreset-volledig`.
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
  bron-URL als attributie met deep-link terug. Draait ook elke 3 uur
  automatisch via de scheduler (#15 fase 3, spoor C) — de eenmalige backfill
  en de daaropvolgende verse/gewijzigde decks komen zo zonder handmatige
  trigger binnen. *Route* `/admin/overview/decks` · *endpoint*
  `/api/admin/overview/decks`.
- **Aanklikbare status-tegels** — elke teller opent een overzichtspagina.
  De wijzigingen-tegel telt roots-only (#206): hetzelfde aantal als de
  lijst waarheen hij linkt; het wijzigingen-overzicht toont bevestigingen
  genest onder hun primaire, met per bevestiging een "Ontkoppel"-actie
  (herstelpad voor een foute consolidatie — het paar wordt daarna nooit
  meer automatisch gemerged). *Route* `/admin/overview/[kind]` · *endpoints*
  `/api/admin/overview/{cards,
  rulechunks, bans, errata, interactions, changes, claims, proposals, relations,
  users, gaps, setcoverage, benchmark, feeds}`,
  `/api/admin/changes/{id}/unconsolidate`.
- **Set-dekking** — tegel + overzichtspagina: per set het basistotaal,
  aanwezige én exact ontbrekende kaartnummers (compacte reeksweergave,
  bv. "12, 45–47, 203"), dekking %, variantentelling, afwijkende
  bron-totalen en laatste sync — allemaal afgeleid uit de riftbound-id's
  zelf ("ogn-074-298" = nr. 74 van 298). Onvolledige sets verschijnen
  bovendien als signaalregel in het kennis-gaten-rapport ("set X mist N
  nummers", met doorklik). *Route* `/admin/overview/setdekking` ·
  *endpoint* `/api/admin/overview/setcoverage` (#145).
- **Reviewqueues** — claims, relaties (+ kandidaat-kinds), mechaniek-kandidaten
  en bronvoorstellen accepteren/verwerpen, mét het bewijs per keuze (#123):
  bron-naam + gesaniteerde (`UrlGuard`) klikbare link per claim-bewijs.
  *Endpoints* o.a. `/api/admin/claims/{id}/accept|reject`,
  `/api/admin/relations/{id}/accept|reject`,
  `/api/admin/relationkinds/{id}/accept|reject`,
  `/api/admin/mechanics/{id}/accept|reject`,
  `/api/admin/proposals/{id}/accept|reject`. De correcties-reviewqueue heeft
  daarnaast een eigen bron+opmerking→her-evaluatie-lus (#184, zie §4.1). De
  relatie-reviewqueue toont sinds #199 v1 (zie "Relatie-triage" hierboven) de
  LLM-aanbeveling (accept/reject/unsure + motivering) naast elk voorstel,
  gesorteerd met de bulk-actionabele groepen eerst, en — alléén in de
  te-reviewen-weergave — een bulk-knop per aanbevelingsgroep
  (`POST /api/admin/relations/bulk-decide`, TOCTOU-gefenced: 409 als de
  groep sinds het laden veranderde) — de aanbeveling is puur
  sorteer-/klik-hulp, geen autoriteit.
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
- **Periodieke zelfverrijking** — relatie-mining nachtelijk, de
  bronnen-scout wekelijks en de Piltover-decks-verversing elke 3 uur (#15
  fase 3, spoor C: de eenmalige ~10k-deck-backfill loopt zo in enkele
  dagen leeg, daarna houdt dezelfde cadans verse/gewijzigde decks
  bijgewerkt) in de scheduler-tick, met job-gate, run_log-vensters en
  degradatiepaden (#122).
- **Nachtelijke ongelimiteerde run** (#245) — binnen een klok-venster
  (default **00:00–11:00** lokaal, env-overschrijfbaar) draait de job
  `nachtrun` de volledige **ongecapte** kennis-keten in één keer: het Ingest-
  en Kaart-pad (met ongecapte mechaniek-mining) → het Brein-pad
  (canonieke entiteiten → brein-interacties → brein-predicaten → projectie →
  reason), met ongecapte miners. Sinds #258 is dat één pad
  (`JobPaths.Nightly`) in plaats van een met de hand geschreven keten; de job
  bepaalt alleen nog de deadline. Winst voor de beheerder: **een run_log-regel
  per stap**, zodat na een nacht zichtbaar is welke stap hoe lang draaide en
  waar het strandde — voorheen was er alleen één afrondingsregel. De dag-caps (mining op 40, e.d.)
  zijn 's nachts nutteloos; deze run leegt de backlog zo ver het venster
  reikt en stopt netjes op de deadline (de rest volgt de volgende nacht via
  het watermark). Overdag blijven de losse jobs gecapt. Ook handmatig te
  starten in **beheer → Brein → "Volledige nachtrun"** (die knop toont ook de
  laatste nachtrun-afronding); handmatig buiten het venster draait zonder
  deadline (volledige drain). **Noodrem + venster** (#249/#254): "Automatische
  nachtrun" (Pauzeren/Hervatten) en het venster (start-uur, eind-uur, tijdzone)
  staan als schakelaars in **beheer → Brein**, direct werkend zonder SSH of
  deploy — handmatig starten via de knop blijft altijd werken. De env-waarden
  (`NIGHTLY_ENABLED` e.d.) blijven de startwaarden. Default aan; alleen een
  expliciete uit-waarde schakelt uit, zodat een typfout de keten niet stil
  stillegt. Een venster dat niet binnen één kalenderdag valt (start ≥ eind) wordt
  geweigerd met uitleg in plaats van stil genegeerd.
- **Brein-extractie herijkt** (#249/#250/#251) — een meting op 383 live
  interacties liet zien dat 69% kaart↔**eigen** keyword was: kennis die al
  gratis en deterministisch bestaat (de graph projecteert `Card.Mechanics[]`
  al als kaart→mechanic-edges), terwijl mech↔mech — het eigenlijke doel — op
  1,3% bleef. Drie wijzigingen: dat paar wordt niet meer gemined (apart geteld,
  géén grafsteen), de aanbieding kijkt naar de hele gedeelde-mechaniek-buurt
  plus regelsecties als bewijstekst (niet als rol), en de poort eist een échte
  relatie in plaats van twee identiteits-ankers. Nieuw is de deterministische
  job **`breinentiteiten`**: het enige pad dat canonieke entiteiten aandraagt —
  zonder die stap bleef de laag leeg, vond predicaat-mining nul subjects en
  bleven mechanic-hovers zonder definitie. Definities komen uit de officiële
  regeltekst — alleen uit **trust-tier-1-bronnen** (een community-parafrase mag
  geen keyword-definitie worden) en alleen uit een sectie die de term als heel
  woord introduceert mét definitie-vorm ("Deflect: …", "Tank N — …", "Deflect is
  …"); een procedure-zin die toevallig met het keyword begint telt niet. Vindt de
  poort niets, dan blijft de definitie leeg — een verzonnen definitie is erger
  dan geen. **rb-ai-uitval wordt nu per oorzaak geteld**
  (rate-limit/overbelasting/timeout/serverfout/parsefout/leeg) en staat in het
  run-detail en de cockpit, met bounded retry bij rate-limits en overbelasting.
  Een HTTP 200 met een afgekapte of schema-vreemde body telt daarbij als
  **onleesbaar antwoord**, niet langer als "geldig maar leeg" — anders meldt een
  run waarin rb-ai bij elke kaart onzin teruggeeft 0% uitval. En 503 heet "503
  overbelast" in plaats van "429 rate-limit": het herstelgedrag is hetzelfde,
  maar de beheerder moet een herstartende sidecar kunnen onderscheiden van een
  throttelend abonnement.
- **Interactie-mining schuift gegarandeerd door de kaartenpool** (#249-review) —
  het voortgangs-watermark staat sinds deze ronde expliciet op de kaart en wordt
  gezet zodra de extractie **geslaagd** is, ook als er niets te promoveren viel.
  Daarvóór werd het afgeleid uit de vastgelegde feiten, en bleef een kaart die
  alleen al-bekende of verworpen paren opleverde eeuwig aan de kop van de
  wachtrij staan: de gecapte job herkauwde elke keer dezelfde 40 kaarten, de
  nachtrun betaalde elke nacht opnieuw, en beheer meldde permanent "nog werk
  over". rb-ai-uitval en een kapot antwoord zetten het watermark bewust niet —
  die kaart komt juist terug. Zichtbaar effect: het drain-signaal in beheer klopt
  weer, en de pool loopt daadwerkelijk leeg. *Na deploy geen actie nodig — de
  al verwerkte kaarten blijven herkend.*
- **Extractie-vraag herijkt: minder vocabulaire, meer vragen** (#286) — na #249
  klopte de vraag inhoudelijk, maar 45-55% van de kaarten viel uit, en dat
  percentage liep monotoon op. Een meting wees de oorzaak aan: zelfde kaarttekst,
  3 aangeboden refs → antwoord in 49 s; 39 refs → afgekapt op de 90 s-timeout.
  Het **aantal aangeboden termen** drijft de kosten, niet de kaarttekst — en dat
  aantal groeit met elke set, dus dit was een schaalklip, geen pech: hoe meer het
  brein leerde, hoe meer extracties omvielen (een gefaalde kaart komt terug, dus
  de wachtrij verzwaarde zichzelf). Drie wijzigingen. **(1)** Een kaart krijgt
  niet langer het hele keyword-vocabulaire van haar buurt te zien maar alleen wat
  relevant is: de keywords die letterlijk op de kaart gedrukt staan plus de
  buren die aantoonbaar samen met zo'n keyword in één bewijstekst voorkomen, met
  een harde bovengrens (12 in plaats van 39). **(2)** Er is een
  **mechanic-niveau-vraag** bijgekomen die vóór de kaartronde draait: 38
  mechanics tegenover 1311 kaarten, en "Equip modificeert Might" geldt voor élke
  kaart met Equip — dezelfde kennis voor een fractie van de aanroepen. Die ronde
  vervángt de kaartronde niet: kaart↔kaart en kaart↔andermans-keyword bestaan
  alleen op kaartniveau en blijven daar gewoon gevonden worden. **(3)** Omdat de
  vaste kosten per aanroep toch betaald zijn, wordt er meer in dezelfde vraag
  meegenomen: het model wijst nu ook de **regelsectie** aan die een interactie
  officieel verankert, waardoor kaartdossier en graaf-verkenner bij zo'n relatie
  naar de bron kunnen doorverwijzen in plaats van alleen "gevonden". Het
  run-detail toont voortaan per ronde het aantal aanroepen, de gemiddelde duur en
  het gemiddelde aantal aangeboden termen, zodat de winst zichtbaar is in plaats
  van beweerd. *Na deploy: draai eerst `breinreset-interacties` als je de oude en
  nieuwe extractie op dezelfde pool wilt vergelijken.*
- **De regelsectie-verankering werkt nu écht** (#315) — de #286-belofte hierboven
  (het model wijst de verankerende regelsectie aan) strandde in de praktijk in
  rb-ai: het meegestuurde `sections`-veld werd daar nooit gelezen en de tool-vorm
  kende geen `governed_by`, dus het model kon het anker per constructie niet
  noemen en de sectie-verwijzing bleef in productie altijd leeg. Sinds #315 leest
  rb-ai de aangeboden secties wél, biedt ze het model als gesloten lijstje aan en
  rekent het antwoord erop na (een verzonnen sectie wordt ge-nuld, precies zoals
  de bestaande poorten). Zichtbaar effect: gepromoveerde interacties kunnen nu
  daadwerkelijk naar hun officiële regelsectie doorverwijzen; het aandeel mét zo'n
  verwijzing was tot deze fix per constructie 0% en is het meetpunt van de issue.
- **Mechanic↔mechanic promoveert alleen nog op regel-/definitietekst** (#324) —
  de eerste steekproef-audit (opus, n=10, zie de audit-feature hieronder) keurde
  9 van 10 gepromoveerde interacties af, en één faalklasse bleek een ontwerpfout:
  de mechanic-vraag bood kaartteksten als bewijs aan, dus een kaart-specifiek
  effect ("deze kaart Ready't zichzelf wanneer ze Stunt") promoveerde tot een
  algemene waarheid over de mechanieken (mechanic:Stun GRANTS mechanic:Ready).
  De promotiepoort eist nu deterministisch dat het bewijs het NIVEAU van de
  claim draagt: een keyword↔keyword-relatie telt alleen met een bewijszin uit
  officiële regel- of definitietekst; een kaart↔X-relatie blijft promoveerbaar
  op de eigen kaarttekst — dát is daar het juiste bewijsniveau. Een
  mech↔mech-paar met alleen kaarttekst-bewijs verdwijnt niet stil maar wacht als
  kandidaat in de reviewqueue, en de extractie-prompt zegt het bewijsniveau nu
  ook expliciet, zodat het model geen kandidaten aandraagt die de poort toch
  weggooit. Bestaande gepromoveerde interacties degraderen niet automatisch —
  de volledige audit levert de lijst en de beheerder beslist per geval in de
  reviewqueue. Meetpunt: de audit-precisie van nieuwe mech↔mech-promoties onder
  de nieuwe promptversie, naast die van de oude.
- **Brein-mining draait parallel** (#279) — de extractie kostte ~40 s per kaart
  en liep één kaart tegelijk: 40 kaarten was een half uur, een ongecapte
  nachtrun (~900 kaarten) tien uur. De kaart- en subject-lus verwerken nu
  meerdere items tegelijk (`BREIN_MINING_CONCURRENCY`, default 3), wat de
  wandkloktijd van een mining-run ruwweg derdeert — bij gelijkblijvende
  uitkomst: elk item is een eigen unit-of-work en de promotie-poort blijft
  geserialiseerd, dus geen dubbele en geen verloren feiten. **`/ask` gaat vóór
  de mining**: het aantal workers is gelijk aan rb-ai's achtergrond-deelcap,
  zodat er altijd slots vrij blijven voor bezoekers, en in de wachtrij haalt een
  vraag lopend mining-werk in. Een bezoeker merkt een draaiende nachtrun dus
  niet. Nieuw in de uitval-telling: **"429 AI-slots vol"** staat apart van "429
  rate-limit" — dat onderscheid voorkomt dat we onze eigen cap aanzien voor een
  throttelend abonnement en zo de verkeerde knop verdraaien. *Na deploy: het
  geheugenplafond van de AI-container is meeverhoogd (1 GiB → 2500 MiB); beide
  waarden horen bij elkaar.*
- **rb-ai vertelt waaróm een aanroep mislukte** (#281) — de mining meldde
  `22 rb-ai-uitval (5xx×22)` op 40 kaarten terwijl `docker logs rb-v2-ai` sinds
  de start één regel bevatte: meer dan de helft van de kaarten haalde de LLM
  niet en niemand kon zien waarom. rb-ai schrijft nu **één regel per
  LLM-aanroep** (`evt=ai_call`: endpoint, duur, statuscode, uitkomst, oorzaak),
  en geeft die oorzaak óók terug in de foutbody, zodat ze in de per-oorzaak-
  telling van #251 belandt en in het **run-detail** staat in plaats van in de
  containerlog: `5xx×22 (api_error×14, no_tool_call×8)`. Onderscheiden worden
  onder meer SDK-fout, max beurten, API-fout, auth, subprocess-crash, timeout,
  afgebroken door de client en "geforceerde tool niet geroepen". **Secrets en
  prompt-inhoud blijven eruit** (werkafspraak 7): elke tekst gaat verplicht door
  een redactie-poort, met een test die vastlegt dat het token nooit in een
  logregel belandt.

  **Een afgebroken run is geen serverfout meer.** Drie totaal verschillende
  oorzaken vielen samen in één ononderscheidbare 500: het model rondde af zonder
  de gevraagde tool te roepen, de tijdslimiet sloeg toe, of er ging echt iets
  stuk. Een afgekapte extractie krijgt nu een eigen statuscode, waardoor het
  run-detail **"timeout×22"** meldt in plaats van "5xx×22" — een heel andere
  aanwijzing voor wie moet beslissen wat er mis is.

  Wat de meting daarmee blootlegde: de duur van een extractie schaalt mee met
  het **aantal begrippen dat we per kaart aanbieden** (gemeten op productie: 3
  begrippen → klaar in 49 s, 39 begrippen → afgekapt op 92 s). Dat aantal groeit
  met elke set die de kennisbank leert, dus dit is een schaalklip: hoe meer het
  brein weet, hoe meer extracties omvallen. De tijdslimiet ophogen verschuift die
  klip alleen — er staat wel een ops-noodrem op, maar de echte oplossing (minder
  begrippen per vraag, of de vraag op mechanic-niveau stellen) is **#288**.
  Tweede, nu zichtbare versterker: de Agent SDK probeert een mislukte API-call
  zelf tot tien keer opnieuw met oplopende wachttijden, en die pogingen passen
  samen niet in het tijdsbudget — zo'n timeout wordt voortaan aan de échte
  oorzaak toegeschreven. Meegenomen: de tijdslimiet begint pas als de aanroep
  daadwerkelijk een AI-slot heeft (de wachtrij at tot een derde van het budget
  op sinds de mining parallel draait). *Na deploy: draai een mining-job en lees
  het run-detail — dáár staat nu welke knop verdraaid moet worden.*

  **De vraag van de bezoeker staat niet meer in de containerlog** (#292). Twee
  oudere logregels liepen volledig om de redactie-poort heen. De ene logde de
  argumenten waarmee de agent het brein bevraagt — bij een zoekopdracht is dat
  in de praktijk de **vraagtekst van de gebruiker**, en die belandde onbewerkt
  in de containerlog, een kanaal dat veel losser wordt behandeld dan de
  vraag-trace in het beheer (waar de vraag bewust wél staat, achter de
  admin-poort). De andere logde een rauwe SDK-foutmelding, die een
  auth-fragment kán dragen. Beide lopen nu door dezelfde poort als al het
  andere; de brein-regel houdt alleen nog de **toolnaam en de omvang** van de
  argumenten over, zodat een beheerder nog steeds ziet dát en hoe vaak de agent
  het brein bevroeg — zonder één teken gebruikersinvoer. De volledige stappen
  blijven ongewijzigd zichtbaar in de vraag-trace. Geen gedragswijziging voor
  de bezoeker; wel minder dat er over hem wordt vastgelegd.
- **Kennis-gaten-rapport** — geclusterde onzekere/lege-retrieval-vragen sturen
  de volgende harvest; bronnen met een gefaalde/onvolledige verwerking staan
  er ook als signaalregel op (#171, `SourceDossierCompleteness`), met
  doorklik naar het bron-dossier. *Endpoint* `/api/admin/overview/gaps`.
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
- **Model-sweep** (#174, uitbreiding op de judge-benchmark) — eigen job
  "benchmarksweep" draait dezelfde vragenset door élk model uit een
  geconfigureerde lijst (env `AI_BENCHMARK_MODELS`, comma-gescheiden;
  standaard een spreiding over de modellen die rb-ai al inzet plus enkele
  nieuwere varianten), elk 2× — voor een eerlijke score/tijd-vergelijking en
  een consistentie-check (scoren de 2 runs gelijk, of was de eerste een
  toevalstreffer?). rb-ai's `/ask` accepteert daarvoor een optioneel expliciet
  `model`-veld dat de SDK-`query()`-modeloverride ingaat — puur additief,
  zonder override ongewijzigd cheap/hard/agentic-gedrag; onbekende modellen
  degraderen netjes (dezelfde AI-uitval-afhandeling als altijd, geen crash).
  `benchmark_run` draagt daarvoor `Model`, `RunIndex` (1/2) en `SweepId` (de
  groepeersleutel, tevens de starttijd van de sweep). Sequentieel uitgevoerd:
  elke ask-aanroep gaat toch al door rb-ai's globale gelijktijdigheids-cap
  (#155) heen, dus een sweep kan de VM niet omvertrekken zonder een eigen
  lokale semafoor nodig te hebben. De geschatte omvang (N modellen × 2 × de
  vragenset) staat vóór de eerste aanroep al in het job-log. Het
  sweep-overzicht toont per model beide runs naast elkaar (score, gemiddelde
  tijd per vraag, consistentie), sorteerbaar op score of snelheid, plus de
  sweep-historie (verloop van modelkwaliteit/-snelheid over tijd). *Route*
  `/admin/overview/benchmark?sweep=…` · *endpoints*
  `/api/admin/jobs/benchmarksweep`, `/api/admin/overview/benchmark`.
- **Primer- & correctie-beheer** — drafts goedkeuren/intrekken, met sinds #266
  de Nederlandse weergave én de canonieke Engelse tekst naast elkaar in de
  reviewrij en beide bewerkbaar in het overzicht (leeg opslaan wist de
  Nederlandse tekst, waarna `/primer` het Engels toont); correcties
  verifiëren/afwijzen, met bron-naam+link, een opmerkingsveld en een
  "opnieuw evalueren"-actie die de opmerking bewaart en de hybride poort
  her-toetst (#184). *Endpoints* `/api/admin/knowledge/*`,
  `/api/admin/corrections/*` (incl. `/reevaluate`).
- **Bronnenbeheer** — bronnen met trust/rank toevoegen/verwijderen/negeren
  (zie "Bronnen negeren met reden" in §4.1). *Endpoints*
  `GET/POST /api/admin/sources`, `PATCH/DELETE /api/admin/sources/{id}`,
  `POST /api/admin/sources/{id}/ignore|unignore`.
- **Brein-verkenner & inspectie** (#236) — een read-only subsectie **Brein**
  in de beheer-console die het Poracle-brein doorzoekbaar en inspecteerbaar
  maakt: een **overzicht** met tegels per brein-tabel (assertions, canonieke
  entiteiten, interacties, conflicts, mining-runs, eval-baselines,
  answertraces) en observability-rollups (mining-precisie, canonieke drift &
  duplicatie-schuld, interactie-tiers, conflict-kanalen — en sinds #255 de
  **gemeten audit-precisie** náást de poort-accept-ratio: per (model ×
  promptversie) n, bevestigd, onjuist en niet-gedragen uit de
  steekproef-audit, expliciet gelabeld "steekproef door {model}, n={aantal}"
  en met de kanttekening dat de mining-"precisie" de accept-ratio van onze
  eigen poort is); een **entiteiten**-
  verkenner (canoniek label + alias-lexicon + merge-status, filterbaar op
  kind/status); een **interacties**-verkenner (gereïficeerde interacties met
  condities, tier-badge en de doorklikbare provenance-keten
  WAS_GENERATED_BY/DERIVED_FROM/VERIFIED_BY per feit). De entiteiten aan beide
  kanten van een interactie zijn sinds #243 **klikbaar met hover-detail**: een
  kaart toont bij hover naam + afbeelding en linkt door naar de kaartpagina
  (`/cards/{id}`), een mechanic toont bij hover haar canonieke label + definitie
  (geen detailpagina); ook de `verankerd:`-ref is opgelost. Een **conflicts**-
  verkenner (reasoning-tegenspraken met hun routering, incl. het
  misvattingen-kanaal); en een **AnswerTrace-viewer** (herspeelbaar antwoord:
  de dragende subgraaf/paden met trust-waarde-toen en de epoch-stempels).
  De verkenner-tabs zijn read-only en additief: niets is bewerkbaar. De
  **Brein**-nav toont een tel-badge; lege staten zijn netjes ("nog geen
  brein-data — draai de brein-jobs via de pipeline"). *Route* `/admin/brein`
  (+ `entities`/`interactions`/`conflicts`/`answertrace`) · *endpoints*
  `GET /api/admin/brein/{overzicht,entities,interactions,assertions/{ref},conflicts,answertraces,answertrace/{id},observability}`.
- **Brein-cockpit & jobs** (brein-jobs-ui) — bovenaan het Brein-overzicht een
  operationele **pipeline-cockpit** die per stap toont wát er live staat en de
  jobs triggerbaar maakt (voorheen waren de vier brein-jobs API-only): **stap 1
  — Extractie** (`breinmine-interacties` + `breinmine-predicaten`: X interacties
  / Y mechanic-predicaten gemined, per job een trigger-knop; sinds #255 ook de
  **steekproef-audit** `breinaudit-interacties` — Z audit-oordelen, met eronder
  de beheerde steekproefdichtheid "1 op N" (`brein.audit.sample_n`, direct
  effect zonder herstart) — een negatief oordeel degradeert nooit zelf maar
  landt als open conflict in de reviewqueue), **stap 2 —
  Projectie** (`breinprojectie` → Neo4j: canonieke-entiteiten-teller + status,
  trigger-knop), **stap 3 — Reasoner** (`reason` → afgeleide edges + conflicts: X
  conflicts / Y open, trigger-knop), en de **/ask-retrieval**-flag (AAN/UIT, met
  sinds #254 een échte aan/uit-knop in plaats van de oude hint "zet
  `BREIN_RETRIEVAL_ENABLED=true` op de VM").
  Elke stap heeft een één-regel-uitleg en de volgorde-hint 1→2→3; status = kleur
  + tekst (geen emoji), met per stap de laatste-run (uit het run_log-grootboek,
  overleeft herstart). De knoppen respecteren de JobRunner-serialisatie (één job
  tegelijk; 409 → nette melding). *Endpoint*
  `GET /api/admin/brein/cockpit` (per-stap-tellingen + laatste-run per job +
  flag-status) · *actie* `POST ?/job` (start via `POST /api/admin/jobs/{name}`).
- **Beheerde feature-vlaggen** (#254) — de vlaggen die alleen via de VM-`.env` +
  een herstart te zetten waren, staan nu als schakelaars in **beheer → Brein**:
  `/ask`-retrieval aan/uit, de audit-steekproefdichtheid (#255,
  `brein.audit.sample_n`: 1 op de N, 1–100, default 10), en onder de
  nachtrun-kaart de noodrem
  (Pauzeren/Hervatten) plus het nachtvenster (start-uur, eind-uur, tijdzone).
  Een wijziging werkt **direct** — rb-api leest de waarde op het gebruiksmoment,
  dus geen SSH, geen redeploy, geen herstart. De omgeving blijft de startwaarde:
  zolang je niets omzet verandert er niets aan het bestaande gedrag. Bij elke
  schakelaar staat de herkomst ("beheerd 3u geleden door beheer · standaard …"),
  en elke wijziging landt als auditregel in het run-grootboek — geen onzichtbare
  state. Onmogelijke waarden (een venster dat niet binnen één kalenderdag valt,
  een onbekende tijdzone) worden geweigerd met uitleg. *Endpoints*
  `GET /api/admin/settings` (effectieve waarde + standaard + wanneer/door wie) ·
  `POST /api/admin/settings` (één of meer sleutels tegelijk, alles-of-niets) ·
  *actie* `POST ?/setting`.

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
- **Foutpagina met poro (#219)** — een globale `+error.svelte` binnen de shell.
  Bij een **404** een vriendelijke, "zoekende" poro (`animate="wink"`), de kop
  "404 — deze pagina bestaat niet (meer)", een korte regel en terug-links naar
  `/` (Overzicht) en `/ask` (vraag het de poro). Bij elke **andere** status een
  nette generieke variant (kop = `status + boodschap`), zelfde opzet. Gecentreerd,
  licht+dark, 0 horizontale overflow op 390/768/1280, toegankelijke links met
  `:focus-visible`. De status → tekst-afbeelding leeft als pure, geteste functie
  (`$lib/errorCopy`).

### 4.7 Decks (Piltover Archive)

- **Deck-browser** — read-only projectie boven op de door `DeckIngestService`
  binnengehaalde Piltover Archive-decks (#15 fase 3, spoor A): lijst met
  facet op domein en sortering op recentheid (`PaUpdatedAt`), views of likes.
  Per deck: naam, domeinen, kaartaantal, views/likes, laatste PA-wijziging en
  een legaliteitsbadge (kleur + tekst, geen emoji). Nadrukkelijk géén editor
  en géén deck-mutatie — puur bladeren in wat de ingest al heeft opgeslagen.
  Filtert desgevraagd op één kaart (`?card=<riftboundId>`, gresolvet naar de
  canonieke groep): de "Bekijk in de deck-browser"-link van het "In decks"-
  blok op de kaartpagina (spoor B) landt hier op een op die kaart gefilterde
  lijst, met een wisbare filterkop. *Route* `/decks` · *endpoints*
  `/api/decks` (`domain`/`sort`/`page`/`format`/`card`/`legality`/`q`),
  `/api/decks/facets`.
- **Filteren op legaliteit en zoeken** *(#265)* — de deckbank is vol (ruim
  10.000 decks) en een groot deel daarvan is niet legaal, wat de lijst als
  inspiratiebron uitholde. `legality=legal|illegal|incomplete` filtert daar
  nu op; op de pagina staat het als één klik ("Alleen legale decks") én als
  keuze in de filter-rail, allebei als wisbare chip. **Zoeken** (`q`) gaat
  over de deckname én de namen van de legend en champions — dat zijn de twee
  dingen waaraan een deck herkenbaar is, dus "Yasuo" vindt Yasuo-decks ook als
  de bouwer zijn deck anders noemde. Bewust géén zoeken over álle kaartregels:
  die vraag beantwoordt het kaart-filter al, en het zou elk deck met een
  populaire kaart erin opleveren. Beide filters zijn onderdeel van de query
  (dus vóór de paginering), zodat het getoonde totaal klopt; een onbekende
  filterwaarde levert de ongefilterde lijst op, geen fout.
- **Deck-code plakken (import)** *(#264)* — een plakveld op `/decks` leest een
  gedeelde deck-code uit: welke kaarten zitten erin en is het deck legaal.
  Gekoppelde kaarten zijn klikbaar naar `/cards/[id]`, niet-gekoppelde regels
  tonen de rauwe kaartcode met "niet in onze databank" (onbekend is data, geen
  fout) en tellen mee als onvolledig. Er wordt niets opgeslagen — puur
  uitlezen. De secties zijn die van het codeformaat zelf (hoofddeck,
  sideboard, chosen champion) mét de notitie dat de PA-indeling in legend,
  champions, battlefields en runes er niet in zit. Een ongeldige of afgekapte
  code geeft de uitleg van de decoder terug ("Ongeldig teken 'x' in de
  deck-code.") in plaats van een fout; de bestaande lijst blijft daarbij
  staan. *Endpoint* `POST /api/decks/decode`.
  **Export (deck-code genereren) bewust niet gebouwd**: het codeformaat kent
  alleen main deck / sideboard / chosen champion, onze PA-decks hebben zeven
  secties, en de PA-payload bevat géén deck-code — een gegenereerde code is
  dus tegen niets round-trip te toetsen. Een kopieerknop die stilletjes het
  verkeerde deck oplevert is schadelijker dan geen knop. Import heeft dat
  probleem niet: `Decode` geeft kaartcodes die we al op `variantNumber`
  matchen.
- **Legaliteitscheck** (`DeckLegality`, pure Domain-logica) — een deck is
  legaal als al zijn gekoppelde kaarten (via `CanonicalRiftboundId`) in een
  legale set zitten (`SetLegality.StatusFor` op de set-releasedatum) én geen
  enkele kaart op de banlijst staat voor het format (default `constructed`).
  Drie uitkomsten: legaal, illegaal-met-reden (per kaart: "nog niet legale
  set" of "geband"), of onvolledig — niet-gekoppelde kaarten en sets zonder
  bekende releasedatum maken een deck nooit hard "illegaal" (dat zou een
  claim zijn die de data niet onderbouwt), ze tellen mee als onvolledig.
- **Deckdetail** — de volledige decklijst per sectie (legend/champions/
  battlefields/runes/maindeck/sideboard/bench, lege secties verborgen), elke
  gekoppelde kaart klikbaar naar `/cards/[id]`; niet-gekoppelde regels tonen
  de rauwe PA-kaartcode zonder link. De legaliteitsuitleg staat bovenaan
  (welke kaart(en) en waarom), met een prominente "Bekijk op Piltover
  Archive"-deeplink en attributietekst — wij spiegelen hun werk, geen eigen
  deckbuilder. *Route* `/decks/[id]` · *endpoint* `/api/decks/{id}`.
  **Visueel deckoverzicht** (#256): per sectie een grid van kaarttegels met
  de kaartafbeelding en het aantal als badge (`×8`), elk in de verhouding van
  díé kaart — sinds #269 uit de gesynchroniseerde afmetingen, zodat de
  liggende battlefields (drie per deck) niet meer als portret bijgesneden
  worden — het grid wrapt vanzelf (~3 tegels op 390px, ~7 op desktop) en
  alle afbeeldingen zijn `loading="lazy"`, zodat een deck van 64 kaarten de
  pagina niet vertraagt. Een niet-gekoppelde kaart krijgt een
  placeholder-tegel met de PA-kaartcode en de notitie "niet in onze
  databank", in dezelfde afmeting en zonder link — onbekend is data, geen
  gat en geen fout. De sectiekop toont het totale aantal kaarten (de som van
  de aantallen, niet het aantal regels); bewust géén "40/40"-noemer, want
  deckbouw-limieten staan nergens in onze data en zouden dus een verzonnen
  regel zijn. Een **lijst/grid-schakelaar** houdt de compacte tekstlijst
  beschikbaar om snel te scannen; het grid is de standaard en de keuze wordt
  in `localStorage` bewaard (`rb-deck-view`, `$lib/deckView.ts`). De server
  rendert altijd het grid — de bewaarde keuze komt pas in de browser binnen,
  wat een hydration-mismatch voorkomt.

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
  volledig functioneel. **Best-effort betekent zichtbaar, niet stil** (#282):
  een overgeslagen stap meldt per oorzaak wat er bleef liggen en laat dat werk
  staan voor de volgende run — een resultaat dat "klaar" zegt terwijl de helft
  ontbreekt, is een bug.
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
  default 5 sinds #279, agentic weegt 2) voorkomt dat een piek aan gelijktijdige
  vragen de VM leegtrekt — boven de cap wacht een vraag kort (max 30s) en
  degradeert daarna netjes (bestaand "AI weg"-pad), nooit een crash of een
  stille wurging van de VM. Sinds #279 kent die cap **prioriteit**: de
  brein-mining is batch-werk en mag maar een deel van de slots bezetten
  (`AI_INTERACTIVE_RESERVE` blijft gereserveerd voor vragen), en in de wachtrij
  gaat een bezoeker altijd vóór de mining — een nachtrun mag `/ask` nooit
  uithongeren.
- **CI is de poort.** `dotnet test` + `svelte-check` + `tsc` groen vóór images
  publiceren; elke productie-bug krijgt eerst een regressietest.

---

## 6. Roadmap

Uit de openstaande GitHub-issues, gegroepeerd op thema. **In-flight** =
openstaande PR.

**Ops & platform**
- **#258** Jobs & paden opschonen — *in-flight*, zie §4.5. Eén ketenmechanisme:
  "Alles bijwerken" en de nachtrun zijn dunne aliassen op een `PathDefinition`
  geworden, de padenstructuur volgt de vier fases (ingest/kaart/kennis/brein),
  `owlaudit` is een CI-assert, en de legacy interactie-miner is uit alle ketens.
  Het uitfaseren van die miner zelf blijft geagendeerd achter #281: het
  kaartdetail leunt nog op zijn data zolang de gereïficeerde mining pas 1,4% van
  de kaarten heeft bereikt.
- **#45** Ops-hardening voor de 8GB-VM: memory-limits, healthchecks +
  deploy-verificatie, één updatemechanisme (Watchtower vs push-to-deploy),
  log-rotatie, migratie-retry bij opstart, secrets-hygiëne, CSP/security-headers.
- **#282** Ollama OOM-gekilled op zijn 2,5 GiB-cap, embeddings vielen stil —
  *in-flight*, zie §4.5. Uitval per oorzaak zichtbaar in het run-detail, werk
  blijft staan voor de volgende run, en het gebruik is begrensd
  (`EMBED_BATCH_SIZE`/`EMBED_BATCH_CHARS`) in plaats van het plafond verhoogd —
  daar is op de 8 GB-VM na #279 geen ruimte meer voor.
- **#293** Het tekenbudget uit #282 stond op 8000 — gemeten exact de waarde
  waarop `llama-server` omvalt — *in-flight*, zie §4.5. Default naar 6000 met
  marge onder de gemeten klip, env-plafond op de meetwaarde, misleidende
  "model niet gepulld?"-hint vervangen door de OOM-hypothese + de ruwe foutbody,
  en een harde (gemelde) kap op de lengte van één embed-item.
- **#301 / #299 / #302 / #303** Vier bevindingen uit de adversariële review op
  #293, samen opgelost omdat ze dezelfde embed-laag raken — *in-flight*, zie
  §4.5. De budgetkap verhuist naar de embed-service zelf, zodat alle ~12
  aanroepplekken hem krijgen in plaats van alleen de twee pijplijnen (#301, met
  als reële geval de primer-draft die een reviewer bewerkt); een gekapte vector
  krijgt provenance op de rij (`embedding_truncated_at`) en de run een
  `warn`-status die het beheer-paneel toont (#299); de run-melding noemt de
  langste originele invoer in plaats van alleen de kaplengte (#302); en het
  env-plafond zakt van 7000 — de klifrand zelf — naar 6300 (#303).
- **#292** Twee logregels in rb-ai omzeilden de redactie-poort — *in-flight*,
  zie §4.5. De agentic tool-log schreef de vraagtekst van de bezoeker naar de
  containerlog en het warmpool-faalpad een rauwe SDK-fout. Beide lopen nu door
  dezelfde poort; de brein-regel houdt alleen toolnaam + argument-omvang over.
  Geen regressie van #281/#285, maar #285 nodigt beheerders uit die log te
  gaan lezen.
- **#254** Feature-vlaggen beheerbaar in de beheerpagina i.p.v. de VM-`.env` —
  *in-flight*, zie §4.5. Een `setting`-tabel met lezen-op-gebruiksmoment: de
  `/ask`-retrieval-vlag en de nachtrun-noodrem + het venster zijn vanuit beheer
  te schakelen, met de omgeving als startwaarde en een auditregel per wijziging.

**Documentatie & proces**
- **#134** Levende documentatie: arc42-architectuurdocument + dit PRD, verankerd
  in elke PR (dit document is deel van #134).
- **#55** Autonome-werkdag-draaiboek en **#60** actuele handoff — proces-issues
  die de stand en volgorde bijhouden (geen productfeature).

**Decks (Piltover-first)**
- **#15** *(herscoped 2026-07-13; fase 1+2 live, fase 3 golf 1 in uitvoering)*
  Géén eigen deckbuilder: we spiegelen Piltover Archive met attributie en
  deep-links terug. Fase 1 — deck-codes (PR #146: C#-port van
  RiftboundDeckCodes, Apache 2.0, Domain-laag). Fase 2 — deck-model +
  robots-compliant PA-ingest (PR #148); backfill van ~10k decks droppelt
  binnen, automatisch ververst via de scheduler (spoor C, PR #179, elke 3
  uur). Fase 3, golf 1 (parallelle sporen): (A) deck-browser +
  legaliteitscheck — *in-flight* (PR #181), zie §4.7, (B) "In decks"-
  dossierblok op de kaartpagina — gemerged (PR #182), (C) periodieke
  decks-verversing in de scheduler-tick — gemerged (PR #179). Golf 2 (ná
  golf 1): (D) co-occurrence/staple-signalen als kennispiramide-laag 3
  in /ask — gedaan via #267 (archetype-detectie blijft expliciet buiten
  scope).
  Onderzoek in `docs/ENGINE.md` §5.
- **#265** Deckbrowser: filter op legaliteit + zoeken op deck-/legend-/
  championnaam — *in-flight*, zie §4.7. Afgesplitst van #15 na scoping: de
  browser filterde alleen op domein terwijl het merendeel van de lijst niet
  legaal is.
- **#264** Deck-codes aansluiten: import via een plakveld op `/decks` —
  *in-flight*, zie §4.7. Sluit de al bestaande `DeckCode`-port (fase 1) aan op
  het product. Export blijft dicht tot er hard bewijs is voor de
  sectie-mapping; de referentie-implementatie kent alleen main deck,
  sideboard en chosen champion.

**Brein & kennisbank**
- **#279** Brein-mining parallelliseren — *in-flight*, zie §4.5. De extractie is
  vrijwel volledig wachttijd op rb-ai (~40 s/kaart), dus een ongecapte nachtrun
  duurde tien uur terwijl de sidecar meerdere sessies aankan. Opgelost in twee
  bewegingen die bij elkaar horen: de lus draait op meerdere workers (elk met
  een eigen `DbContext`), en de sidecar-cap + het geheugenplafond van de
  AI-container gaan mee omhoog. Randvoorwaarde die de hele vorm bepaalt: een
  mining-run mag `/ask` niet uithongeren, dus batch-werk krijgt een deelcap en
  wijkt in de wachtrij voor interactief verkeer.
- **#263** Gerichte brein-mining-reset — *in-flight*, zie §4.5. Zonder deze
  reset blijven ~800 kaarten afgevinkt met de in #249 als ondeugdelijk
  vastgestelde extractie, waardoor de verbeterde extractie precies die kaarten
  overslaat en de verbetering niet meetbaar is. Twee expliciete scopes
  (alleen interacties, of ook entiteiten/predicaten voor #250).
- **#255** Steekproef-audit door een sterker model — *in-flight*, zie
  §4.5. De getoonde "precisie ≈ 0,91" is de accept-ratio van onze eigen
  promotie-poort (zelfreferentieel); de nieuwe job `breinaudit-interacties`
  laat 1 op de N gepromoveerde interacties door rb-ai's task "hard" beoordelen
  (gesloten oordeel: correct + gedragen door het bewijs) en toont de gemeten
  precisie in de observability náást de poort-ratio. Het oordeel draagt eigen
  provenance en verandert nooit zelf een tier — een negatief oordeel landt in
  de reviewqueue. De bredere modelkeuze-per-mining-taak uit het issue blijft
  open; de audit gebruikt bewust de bestaande cheap/hard-taak-typering.
- **#266** `/primer` in het Nederlands weergeven, met de Engelse opslag
  canoniek — *in-flight*, zie §4.4. Live regressie sinds #187/#197: de
  vertaling gebeurt bij de generatie en gaat door de bestaande
  draft/approve-poort. Afgesplitst van **#189** (de bredere taalafweging),
  waar de conclusie blijft: geen vertaallaag over officiële regel- of
  kaartteksten.

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
- **Model-sweep** (`/api/admin/overview/benchmark?sweep=…`, #174): dezelfde
  regressiemeter, maar dan als kwaliteits-/kostenmetriek tussen modellen
  onderling — score én tijd naast elkaar per model (elk 2× voor een
  consistentie-check), gerangschikt op score en op snelheid, met een
  sweep-historie zodat modelkeuzes voor rb-ai (cheap/hard/agentic) op cijfers
  te onderbouwen zijn in plaats van op aanname.
- **Gemeten mining-precisie** (`/api/admin/brein/observability`, #255): het
  aandeel gepromoveerde interacties dat een onafhankelijke steekproef door een
  sterker model als "correct én gedragen door het bewijs" beoordeelt —
  expliciet naast (en los van) de accept-ratio van de eigen promotie-poort,
  die alleen meet hoe vaak de poort zichzelf gelijk geeft.

**Zinnige volgende metrieken**
- **Dekking**: aandeel `/ask`-antwoorden met een "Zeker/Redelijk zeker"-label
  vs. "Onzeker", en het aandeel met minstens één officiële §-regelbasis.
- **Kosten per vraag**: (geschatte) kosten in euro's bovenop de tokentotalen
  van #121, als budgetbewaking naast de puur informatieve tellingen.
- **Kennis-versheid**: doorlooptijd van set-release/erratum tot bijgewerkte
  primer/claims (invalidatie → hertoetsing → re-review). De temporele
  precedentie-datums (#168: `Source.PublishedAt`/`UpdatedAt`,
  `Erratum.EffectiveFrom`) leveren de ruwe tijdstempels die deze metriek
  straks berekenbaar maken — nu nog puur zichtbaar, niet geaggregeerd.
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
  meertalige site is geen doel. De Nederlandse primer-weergave naast de
  canonieke Engelse opslag (#266) is géén taalwissel-functie: er is één
  bezoekerstaal, en de Engelse tekst blijft alleen zichtbaar als eerlijke
  terugval wanneer de vertaling ontbreekt.
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
