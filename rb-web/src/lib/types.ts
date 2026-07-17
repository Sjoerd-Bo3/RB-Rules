// Gedeelde API-response-types (docs/CONVENTIONS.md: "zodra twee routes
// hetzelfde type nodig hebben verhuist het naar $lib/types.ts").

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
}
