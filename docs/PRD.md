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
  deck-browser (`/decks`) toont Piltover Archive-decks met facet op domein en
  een legaliteitsbadge; de detailpagina laat precies zien welke kaart een
  probleem geeft (nog niet legale set of geband) en linkt terug naar de bron.

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
  FTS → RRF, degradatie naar alleen-FTS eerlijk gemeld). Elke ruling toont ook
  zijn "waar besloten"-bronverwijzing (URL of vrije citatie, #166), ook in het
  kaart-dossier en de reviewqueue.
  *Route* `/rulings` · *endpoint* `/api/rulings`.
- **FAQ-/clarificatie-concept-extractie** (#177) — een FAQ-/clarificatie-
  artikel (bv. de Unleashed Rules FAQ) wordt door de gewone scan-pipeline
  geknipt en geëmbed als vaste-lengte-slabs die meerdere losse
  verduidelijkingen mengen; één embedding over zo'n slab slaat de betekenis
  plat, dus een gerichte vraag ("Legion = finalize an item on the chain")
  haalt het chunk niet boven. Herkenning via een naam-/URL-heuristiek
  (`ClarificationSources.IsMatch`, geen migratie nodig — Source draagt Url/
  Name al) op officiële (TrustTier 1) bronnen; job "clarify" destilleert er
  via rb-ai discrete concepten uit (onderwerp + gefocuste verduidelijking +
  evt. §-verwijzing + citaat) en slaat elk op als ruling met een eigen,
  gefocuste embedding (alleen de verduidelijking, niet de hele slab) — zo komt
  het item wél boven bij een gerichte vraag, in `/ask`, `/rulings` en (bij een
  kaart-onderwerp) het kaartdossier. **Hybride autoriteitspoort** (autoriteits-
  review): auto-verified voor LLM-geparafraseerde tekst is te los, dus een
  concept wordt alleen direct `verified` als het én *grounded* is (het citaat
  komt écht in de brontekst voor — vangt een gehallucineerd citaat) én
  *anchored* (het onderwerp resolvet naar een bestaande knoop: kaartnaam,
  mechaniek-vocabulaire, §-code of primer-concept — vangt een verzonnen/fout
  anker dat anders stil aan een kaartpagina zou koppelen). Anders gaat het als
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
  De eerste scan van zo'n bron krijgt ook meteen een sjabloon-`Change` (type
  "clarification") zodat de aankomst zelf al in de wijzigingen-feed
  verschijnt (er is dan nog geen vorige versie om te diffen). *Job* `clarify`
  (handmatig of nachtelijk via `ScanScheduler`) · *endpoints*
  `/api/admin/jobs/clarify`, `/api/admin/corrections/{id}/reject`.
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
  uitleg waarom kaarten op elkaar lijken) met versies/varianten. Bij meerdere
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
  meta, expliciet gelabeld in de prompt en het antwoordformat.
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
- **Graph-verkenner** — interactieve kaart↔mechaniek↔regel-visualisatie.
  *Route* `/graph` · *endpoint* `/api/graph/neighbors`.
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
  anti-vergiftigingsgrens.

### 4.5 Beheer (`/admin`)

- **Jobs met live voortgang** — de "Alles bijwerken"-keten en losse jobs
  (scan, feeds, cards, embed, mine, rules, bans, graph, primer, interactions,
  scout, classify, claims, clarify, relations, setrelease, decks, benchmark,
  benchmarksweep, regenerateknowledge) draaien via `JobRunner` met
  live-voortgang en run_log. *Route* `/admin` · *endpoints*
  `/api/admin/jobs/{name}`, `/api/admin/status`, `/api/admin/logs`.
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
- **Periodieke zelfverrijking** — relatie-mining nachtelijk, de
  bronnen-scout wekelijks en de Piltover-decks-verversing elke 3 uur (#15
  fase 3, spoor C: de eenmalige ~10k-deck-backfill loopt zo in enkele
  dagen leeg, daarna houdt dezelfde cadans verse/gewijzigde decks
  bijgewerkt) in de scheduler-tick, met job-gate, run_log-vensters en
  degradatiepaden (#122).
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
  `/api/decks` (`domain`/`sort`/`page`/`format`/`card`), `/api/decks/facets`.
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
  golf 1): (D) co-occurrence/archetype-signalen als kennispiramide-laag 3
  in /ask.
  Onderzoek in `docs/ENGINE.md` §5.

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
