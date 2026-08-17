// Presentatie van een kaartafbeelding (#269/#270). Riot levert per kaart de
// afmetingen, een alt-tekst en de dominante kleuren; deze helpers maken daar
// de drie dingen van die élke kaarttegel nodig heeft: de juiste verhouding,
// een bruikbare alt-tekst en een laadkleur. Eén plek, want dit staat in het
// deckgrid, de kaartlijst, de kaartpagina en de kaartwidget.

/** De velden die de API bij elke kaart meestuurt; alles optioneel zodat
 *  oudere payloads (en kaarten zonder gekoppelde bron) blijven werken. */
export interface CardImageFields {
	name?: string | null;
	imageUrl?: string | null;
	imageWidth?: number | null;
	imageHeight?: number | null;
	imageAltText?: string | null;
	imageColorPrimary?: string | null;
	flags?: string[] | null;
}

/** Staande verhouding van een gewone Riftbound-kaart. Battlefields (66 van de
 *  1178) zijn liggend — vandaar dat dit nooit meer hardgecodeerd mag zijn. */
const PORTRAIT = '744 / 1039';

/**
 * CSS-waarde voor `aspect-ratio`. Zonder bekende maat: staand, want dat is de
 * verhouding van de overgrote meerderheid — en een tegel zonder verhouding
 * laat de hele lijst verspringen zodra de afbeelding binnenkomt.
 */
export function cardAspect(card: CardImageFields | null | undefined): string {
	const w = card?.imageWidth;
	const h = card?.imageHeight;
	return w && h && w > 0 && h > 0 ? `${w} / ${h}` : PORTRAIT;
}

/** Battlefields liggen; al het andere staat. */
export function isLandscape(card: CardImageFields | null | undefined): boolean {
	const w = card?.imageWidth;
	const h = card?.imageHeight;
	return !!w && !!h && w > h;
}

/**
 * Alt-tekst voor de afbeelding: Riots eigen accessibilityText waar die er is,
 * anders de door de API samengestelde variant, anders de kaartnaam.
 *
 * Dit hoort UITSLUITEND in een `alt=`. De tekst kan lokaal afgeleid zijn en
 * is dus geen officiële kaarttekst — nooit als zichtbare kaarttekst tonen.
 */
export function cardAlt(
	card: CardImageFields | null | undefined,
	fallbackName?: string | null
): string {
	const alt = card?.imageAltText?.trim();
	if (alt) return alt;
	return (fallbackName ?? card?.name ?? '').trim();
}

// Alleen een echte hexkleur mag in een style-waarde belanden; de API
// normaliseert al, maar de UI vertrouwt geen enkele waarde blind.
const HEX = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i;

/**
 * Laadkleur onder de afbeelding: Riots dominante kaartkleur, zodat een tegel
 * niet als wit gat begint. Zonder (geldige) kleur: het neutrale ontwerptoken.
 */
export function cardTint(card: CardImageFields | null | undefined): string {
	const hex = card?.imageColorPrimary?.trim();
	return hex && HEX.test(hex) ? hex : 'var(--surface-deep)';
}

/** Riots "New"-markering op een kaart in de gallery. */
export function isNewCard(card: CardImageFields | null | undefined): boolean {
	return !!card?.flags?.some((f) => f.toLowerCase() === 'new');
}
