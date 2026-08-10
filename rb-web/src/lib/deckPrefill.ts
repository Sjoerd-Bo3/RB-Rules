// Deck-prefill voor het score pad (#344): één genormaliseerde decklijst-vorm
// die de deck- en registration-vellen eten, ongeacht of de lijst uit rb-api
// komt (DeckDetail/DecodedDeck via de /scorepad-proxy's) of uit geplakte
// tekst. Zuiver en DOM-loos zodat alles testbaar is. Nooit gooien: rommelige
// invoer levert een zo-goed-mogelijke lijst op, nooit een kapotte pagina.

export interface DeckPrefillRow {
	qty: number;
	name: string;
}

export interface DeckPrefillSection {
	/** Canonieke sectiesleutel, lowercase (legend/champions/…); onbekende
	 *  secties houden hun eigen (genormaliseerde) naam. */
	section: string;
	rows: DeckPrefillRow[];
}

export interface DeckPrefill {
	name: string | null;
	sections: DeckPrefillSection[];
}

/** Vaste weergavevolgorde — spiegelt SECTION_LABEL op /decks/[id]. Onbekende
 *  secties komen achteraan, in hun oorspronkelijke volgorde. */
export const SECTION_ORDER: readonly string[] = [
	'legend',
	'champions',
	'battlefields',
	'runes',
	'maindeck',
	'sideboard',
	'bench'
];

/** Minimale structurele vorm van DeckDetail- én DecodedDeck-secties (#15/#264)
 *  — beide leveren precies deze velden, de rest doet er hier niet toe. */
export interface DeckSectionInput {
	section: string;
	cards: { cardCode: string; quantity: number; cardName?: string | null }[];
}

function sectionRank(section: string): number {
	const i = SECTION_ORDER.indexOf(section);
	return i === -1 ? SECTION_ORDER.length : i;
}

/** Bekende secties in de vaste volgorde, onbekende achteraan; de sort is
 *  stabiel, dus onbekende secties behouden hun onderlinge volgorde. */
function sortSections(sections: DeckPrefillSection[]): DeckPrefillSection[] {
	return [...sections].sort((a, b) => sectionRank(a.section) - sectionRank(b.section));
}

/** DeckDetail-/DecodedDeck-secties → DeckPrefill. Rij-naam = cardName met de
 *  rauwe cardCode als vangnet (niet-gekoppelde regels dragen alleen de code);
 *  lege secties vallen weg. */
export function fromDeckSections(name: string | null, sections: DeckSectionInput[]): DeckPrefill {
	const out: DeckPrefillSection[] = [];
	for (const sec of sections) {
		const rows = sec.cards.map((c) => ({
			qty: c.quantity,
			name: (c.cardName ?? '').trim() || c.cardCode
		}));
		if (rows.length === 0) continue;
		out.push({ section: sec.section.trim().toLowerCase(), rows });
	}
	const trimmed = name?.trim();
	return { name: trimmed ? trimmed : null, sections: sortSections(out) };
}

/** Sectiekop-aliassen (case-insensitief, met of zonder ':'), gemapt op de
 *  canonieke sleutels van SECTION_ORDER. */
const HEADER_ALIAS: Record<string, string> = {
	legend: 'legend',
	legends: 'legend',
	champion: 'champions',
	champions: 'champions',
	battlefield: 'battlefields',
	battlefields: 'battlefields',
	rune: 'runes',
	runes: 'runes',
	'main deck': 'maindeck',
	maindeck: 'maindeck',
	deck: 'maindeck',
	sideboard: 'sideboard',
	bench: 'bench'
};

/** Regels mét expliciet aantal: '3x Naam', '3 x Naam', '3 Naam', 'Naam x3'.
 *  Geen match = geen aantal (kandidaat-sectiekop of kale naam). */
function parseQtyRow(line: string): DeckPrefillRow | null {
	let m = /^(\d{1,3})\s*[x×]\s+(.+)$/i.exec(line);
	if (m) return { qty: Number(m[1]), name: m[2].trim() };
	m = /^(\d{1,3})\s+(.+)$/.exec(line);
	if (m) return { qty: Number(m[1]), name: m[2].trim() };
	m = /^(.+?)\s+[x×]\s*(\d{1,3})$/i.exec(line);
	if (m) return { qty: Number(m[2]), name: m[1].trim() };
	return null;
}

/** Geplakte decklijst-tekst → DeckPrefill. Sectiekop = regel zónder aantal
 *  die op ':' eindigt of exact een bekende sectienaam is; alles vóór de
 *  eerste kop valt onder 'maindeck'. Lege regels en '//'-commentaar worden
 *  overgeslagen. Secties met dezelfde kop worden samengevoegd. */
export function parseDeckText(text: string): DeckPrefill {
	// Map bewaart de invoegvolgorde — dat is de volgorde van onbekende secties.
	const byKey = new Map<string, DeckPrefillRow[]>();
	let current = 'maindeck';

	const pushRow = (row: DeckPrefillRow) => {
		// qty 0 is per constructie rommel ('0x …') — stil overslaan.
		if (row.qty < 1 || !row.name) return;
		const rows = byKey.get(current);
		if (rows) rows.push(row);
		else byKey.set(current, [row]);
	};

	for (const raw of text.split(/\r?\n/)) {
		// '//'-commentaar mag ook achter een regel staan; kaartnamen bevatten
		// geen '//', dus alles erachter is notitie.
		const line = raw.split('//')[0].trim();
		if (!line) continue;

		const qtyRow = parseQtyRow(line);
		if (qtyRow !== null) {
			pushRow(qtyRow);
			continue;
		}

		// Regel zonder aantal: sectiekop of kale naam (qty 1).
		const endsColon = line.endsWith(':');
		const head = (endsColon ? line.slice(0, -1) : line).trim().toLowerCase();
		const alias = HEADER_ALIAS[head];
		if (alias !== undefined) {
			current = alias;
			continue;
		}
		if (endsColon) {
			// Onbekende kop ('Removal:'): eigen sectie, komt achteraan te staan.
			if (head) current = head;
			continue;
		}
		pushRow({ qty: 1, name: line });
	}

	const sections = [...byKey.entries()].map(([section, rows]) => ({ section, rows }));
	return { name: null, sections: sortSections(sections) };
}

/** Deck-id uit een geplakte Piltover-link (…/decks/view/{uuid}, met of zonder
 *  query/fragment/trailing slash) of een kale uuid/id; anders null. */
export function piltoverDeckIdFromInput(s: string): string | null {
	const input = s.trim();
	if (!input) return null;
	// De lookahead eist dat direct na de id een /, ? of # volgt (of het einde)
	// — een te lange id wordt zo afgewezen in plaats van stil afgekapt.
	const link = /\/decks\/view\/([A-Za-z0-9-]{8,64})(?=[/?#]|$)/i.exec(input);
	if (link) return link[1];
	if (/^[A-Za-z0-9-]{8,64}$/.test(input)) return input;
	return null;
}
