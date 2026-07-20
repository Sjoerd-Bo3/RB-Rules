/** Weergavekeuze voor het deckoverzicht (#256). Het grid met kaartafbeeldingen
 * is de standaard — dat is waar de issue om vraagt; de compacte tekstlijst
 * blijft bestaan om een decklijst snel te kunnen scannen of overtypen. */
export type DeckView = 'grid' | 'list';

/** localStorage-sleutel; hetzelfde `rb-`-voorvoegsel als de thema- en
 * ask-historie-sleutels. */
export const DECK_VIEW_KEY = 'rb-deck-view';

/** Leest een bewaarde keuze streng: alleen de letterlijke waarde 'list' zet de
 * lijstweergave aan. Alles anders (geen keuze, een oude waarde, met de hand
 * geknoeide rommel) valt terug op het grid, zodat een kapotte localStorage
 * nooit een lege of onverwachte pagina oplevert. */
export function parseDeckView(raw: string | null): DeckView {
	return raw === 'list' ? 'list' : 'grid';
}

/** Totaal aantal fysieke kaarten in een sectie: de som van de aantallen, niet
 * het aantal regels — 4× dezelfde kaart is één regel maar vier kaarten. */
export function sectionTotal(cards: readonly { quantity: number }[]): number {
	return cards.reduce((total, card) => total + card.quantity, 0);
}
