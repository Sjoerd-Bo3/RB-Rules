// Gedeelde API-response-types (docs/CONVENTIONS.md: "zodra twee routes
// hetzelfde type nodig hebben verhuist het naar $lib/types.ts").

/** Kaartdetail zoals `/api/cards/{id}` het levert. Gedeeld door `/cards/[id]`
 *  en de graph-node-proxy `/graph/node` (#252 — de detailweergave onder de
 *  brein-graaf toont dezelfde kaartfeiten zonder wegnavigeren). */
export interface CardDetail {
	riftboundId: string;
	name: string;
	type: string | null;
	supertype: string | null;
	rarity: string | null;
	domains: string[];
	energy: number | null;
	might: number | null;
	power: number | null;
	setId: string | null;
	setLabel: string | null;
	collectorNumber: number | null;
	textPlain: string | null;
	imageUrl: string | null;
	tags: string[];
	mechanics: string[] | null;
	triggers: string[] | null;
	effects: string[] | null;
	banned: boolean;
	errataText: string | null;
	variantOf: string | null;
	/** Set-legaliteit (#22): releasedatum van de set (yyyy-mm-dd) of null. */
	legalFrom: string | null;
	legality: 'legal' | 'upcoming' | 'announced';
	versions: {
		riftboundId: string;
		setId: string | null;
		setLabel: string | null;
		rarity: string | null;
		collectorNumber: number | null;
		imageUrl: string | null;
	}[];
}

/** Bevestiging (#206): een secundaire change (andere bron, zelfde
 *  gebeurtenis) genest onder de primaire — dezelfde DTO-vorm
 *  (rb-api ChangeFeedConfirmation) op zowel de publieke feed als het
 *  admin-overzicht "wijzigingen". SourceUrl is Source.Url, een geregistreerde
 *  bron-kolom (geen aparte UrlGuard-sanitize nodig, zie #206-commentaar). */
export interface ChangeConfirmation {
	id: number;
	sourceId: string;
	sourceName: string;
	sourceUrl: string;
	trustTier: number;
	summary: string | null;
	meaning: string | null;
	diff: string | null;
	detectedAt: string;
}

/** Herbruikbare vorm voor ChangeCard.svelte (#210). De meeste velden zijn
 *  optioneel omdat niet elke context evenveel data draagt:
 *  - compacte contexten (sectie-/bron-dossier) kennen alleen id/changeType/
 *    severity/summary/detectedAt;
 *  - het admin-overzicht "wijzigingen" mist sourceUrl/trustTier/diff op het
 *    primaire item — rb-api's ChangeOverviewItem draagt die (nog) niet (zie
 *    AdminOverviewService.ChangesAsync); de kaart degradeert dan gewoon
 *    (geen bronlink/trust-label/voor-na-uitklap voor dát item, de
 *    bevestigingen dragen die velden wél).
 *  Alles hieronder mag dus stil ontbreken zonder dat de kaart breekt. */
export interface ChangeCardData {
	id: number;
	changeType: string;
	severity: string;
	summary: string | null;
	detectedAt: string;
	meaning?: string | null;
	sourceName?: string;
	sourceUrl?: string | null;
	trustTier?: number | null;
	diff?: string | null;
	confirmedBy?: ChangeConfirmation[];
	/** Domein-proef (design-proof-branch, NIET nog uit rb-api afgeleid): kleurt
	 *  de linker-randstreep van de kaart. Canoniek: Fury/Body/Mind/Calm/Chaos/
	 *  Order/Colorless (zie app.css --dom-*). Onbekend/ontbrekend valt terug op
	 *  Colorless-neutraal — dit veld komt voorlopig alleen uit stub-data; het
	 *  echt afleiden uit de geraakte kaart/structured-ban-laag is een
	 *  rb-api-follow-up ná goedkeuring van de richting. */
	domain?: string | null;
}

// ── /ask-antwoord (#31, gedeeld sinds #248) ──────────────────────────────
// De vorm die de niet-streamende form action én het streamingpad opleveren.
// Sinds #248 leest ook de sessie-store ($lib/askSession.svelte.ts) deze
// types, dus wonen ze hier in plaats van bij de load van /ask.

export interface AskCitation {
	n: number;
	sourceName: string;
	url: string;
	section: string | null;
	trust: number;
	text: string | null;
	pdfUrl: string | null;
	page: number | null;
	parents: { code: string; text: string }[] | null;
	/** Temporele precedentie (#168): "laatst bijgewerkt" (een echte
	 *  content-wijziging) of anders "geldig sinds" (publicatiedatum) —
	 *  beide null als de bron geen van beide draagt. */
	publishedAt: string | null;
	updatedAt: string | null;
}

export interface AskCard {
	riftboundId: string;
	name: string;
	type: string | null;
	supertype: string | null;
	domains: string[];
	energy: number | null;
	might: number | null;
	textPlain: string | null;
	mechanics: string[] | null;
	imageUrl: string | null;
	banned: boolean;
	/** Set-legaliteit (#68): label voor kaarten uit een nog niet verschenen set. */
	setName: string | null;
	legalFrom: string | null;
	legality: 'legal' | 'upcoming' | 'announced';
}

/** Community-consensus (#51): geaccepteerde claims die als interpretatielaag
 *  meegingen — apart blok onder het antwoord, met trust-label en bronnen. */
export interface AskClaimSource {
	sourceName: string;
	url: string;
}
export interface AskClaim {
	topicType: string;
	topicRef: string;
	statement: string;
	corroboration: number;
	trustScore: number;
	officialStatus: string;
	sources: AskClaimSource[];
}

/** Misvattingen-kanaal (#125): verworpen community-claims mét officiële
 *  weerlegging — het misvatting-blok toont beide bewijzen (community-citaat
 *  met bron-link én de weerlegging, met §-link waar herleidbaar). */
export interface AskMisconceptionSource {
	sourceName: string;
	url: string;
	quote: string | null;
}
export interface AskMisconception {
	topicType: string;
	topicRef: string;
	statement: string;
	rebuttal: string;
	rebuttalSection: string | null;
	sources: AskMisconceptionSource[];
}

export interface AskResult {
	answer: string;
	citations: AskCitation[];
	cards: AskCard[];
	questionType: string;
	claims: AskClaim[] | null;
	misconceptions: AskMisconception[] | null;
	/** Aanpak-terugmelding (#153): welke aanpak het werd en waarom die
	 *  eventueel afwijkt van de keuze (machine-sleutel, zie $lib/approach). */
	approach: string | null;
	approachReason: string | null;
}

/** Doorvragen (#41): één eerdere ronde in het gesprek. */
export interface AskTurn {
	question: string;
	answer: string;
}
