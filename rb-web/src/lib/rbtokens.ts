// Riot's kaartteksten bevatten icon-tokens (:rb_energy_1:, :rb_might:,
// :rb_exhaust:, :rb_rune_fury: …). Deze module zet ge-escapete tekst om naar
// HTML met echte inline-iconen (styling in app.css). Zelfde renderer voor
// kaartpagina's én markdown-antwoorden.

const escapeHtml = (s: string) =>
	s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const MIGHT_SVG =
	'<svg class="tok tok-svg tok-might" viewBox="0 0 16 16" role="img" aria-label="might">' +
	'<path d="M8 1l2.2 7.4L8 12l-2.2-3.6z"/><path d="M4.6 11.2h6.8v1.5H9.1V15H6.9v-2.3H4.6z"/></svg>';

const EXHAUST_SVG =
	'<svg class="tok tok-svg tok-exhaust" viewBox="0 0 16 16" role="img" aria-label="exhaust">' +
	'<path d="M8 2.6a5.4 5.4 0 1 1-5.1 3.7l1.5.5A3.8 3.8 0 1 0 8 4.2z"/>' +
	'<path d="M2.2 1.6l4 1.4-2.7 3.2z"/></svg>';

// Runen (#257): elk domein krijgt een eigen VORM, niet alleen een eigen kleur.
// Kleur alleen viel op deze maat niet te onderscheiden (order vs body), en een
// vorm werkt ook zonder kleurwaarneming. Zelfgetekend — geen Riot-materiaal,
// geen extra netwerk-asset, scherp op elke maat. Vulling komt uit de
// domein-tokens in app.css.
//
//   fury    vlam (punt omhoog)        chaos   bliksemschicht
//   calm    wassende maan             order   ruit
//   mind    ring                      body    blokje
//   rainbow zeshoek in zes domeinkleuren ("willekeurige rune")
const RAINBOW_HEX = 'M8 1.4L13.7 4.7L13.7 11.3L8 14.6L2.3 11.3L2.3 4.7z';
const RAINBOW_WEDGES: [string, string][] = [
	['w-fury', 'M8 8L8 1.4L13.7 4.7z'],
	['w-body', 'M8 8L13.7 4.7L13.7 11.3z'],
	['w-order', 'M8 8L13.7 11.3L8 14.6z'],
	['w-calm', 'M8 8L8 14.6L2.3 11.3z'],
	['w-mind', 'M8 8L2.3 11.3L2.3 4.7z'],
	['w-chaos', 'M8 8L2.3 4.7L8 1.4z']
];

const RUNE_SHAPES: Record<string, string> = {
	fury: '<path d="M8 1.5c3 3 4.5 5 4.5 7.2a4.5 4.5 0 1 1-9 0C3.5 6.5 5 4.5 8 1.5z"/>',
	calm: '<path d="M9.4 2.2A6 6 0 1 0 9.4 13.8A6.4 6.4 0 0 1 9.4 2.2z"/>',
	mind:
		'<path d="M2 8a6 6 0 1 0 12 0 6 6 0 1 0-12 0z' +
		'M5.4 8a2.6 2.6 0 1 1 5.2 0 2.6 2.6 0 1 1-5.2 0z"/>',
	body: '<rect x="2.9" y="2.9" width="10.2" height="10.2" rx="2.4"/>',
	order: '<path d="M8 1.4L14.6 8L8 14.6L1.4 8z"/>',
	chaos: '<path d="M9.8 1.3L3.2 9.2h3.9l-.9 5.5 6.6-8.1H8.9z"/>',
	rainbow:
		`<path class="hex" d="${RAINBOW_HEX}"/>` +
		RAINBOW_WEDGES.map(([cls, d]) => `<path class="${cls}" d="${d}"/>`).join('')
};

const RUNES = new Set(Object.keys(RUNE_SHAPES));

/** Nederlandse naam van de rune, voor aria-label én tooltip. */
function runeLabel(rune: string): string {
	return rune === 'rainbow' ? 'willekeurige rune' : `${rune} rune`;
}

// Vooraf opgebouwd: de markup is constant per rune.
const RUNE_SVG: Record<string, string> = Object.fromEntries(
	Object.entries(RUNE_SHAPES).map(([rune, shape]) => {
		const label = runeLabel(rune);
		return [
			rune,
			`<svg class="tok tok-svg tok-rune tok-rune-${rune}" viewBox="0 0 16 16" role="img"` +
				` aria-label="${label}"><title>${label}</title>${shape}</svg>`
		];
	})
);

const replaceInText = (text: string) =>
	text.replace(/:rb_([a-z0-9_]+):/g, (whole, tok: string) => {
		if (tok.startsWith('energy_')) {
			const n = tok.slice('energy_'.length);
			return `<span class="tok tok-energy" role="img" aria-label="${n} energy">${n}</span>`;
		}
		if (tok === 'might') return MIGHT_SVG;
		if (tok === 'exhaust') return EXHAUST_SVG;
		if (tok.startsWith('rune_')) {
			const rune = tok.slice('rune_'.length);
			if (RUNES.has(rune)) return RUNE_SVG[rune];
		}
		return whole; // onbekend token: laten staan
	});

/**
 * Vervangt tokens in reeds ge-escapete HTML door icoon-markup.
 *
 * Alleen in tekstposities, nooit binnen een tag: `renderMarkdown` draait dit
 * over marked-uitvoer, en een token in een link-URL (`[x](/a/:rb_might:)`) zou
 * anders icoon-markup — inclusief aanhalingstekens — ín het href-attribuut
 * plakken en daaruit ontsnappen. De invoer is ge-escaped, dus elke echte `<`
 * hoort bij een tag die wij zelf hebben gemaakt.
 */
export function iconifyTokens(escapedHtml: string): string {
	return escapedHtml.replace(/<[^>]*>|[^<]+/g, (part) =>
		part.startsWith('<') ? part : replaceInText(part)
	);
}

/** Kaarttekst (plain, onvertrouwd) → veilige HTML met iconen. */
export function renderCardText(raw: string): string {
	return iconifyTokens(escapeHtml(raw));
}
