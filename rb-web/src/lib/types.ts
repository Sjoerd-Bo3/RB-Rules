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
