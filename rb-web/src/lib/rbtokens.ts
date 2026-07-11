// Riot's kaartteksten bevatten icon-tokens (:rb_energy_1:, :rb_might:,
// :rb_exhaust:, :rb_rune_fury: …). Deze module zet ge-escapete tekst om naar
// HTML met echte inline-iconen (styling in app.css). Zelfde renderer voor
// kaartpagina's én markdown-antwoorden.

const escapeHtml = (s: string) =>
	s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const RUNES = new Set(['fury', 'calm', 'mind', 'body', 'order', 'chaos', 'rainbow']);

const MIGHT_SVG =
	'<svg class="tok tok-svg tok-might" viewBox="0 0 16 16" role="img" aria-label="might">' +
	'<path d="M8 1l2.2 7.4L8 12l-2.2-3.6z"/><path d="M4.6 11.2h6.8v1.5H9.1V15H6.9v-2.3H4.6z"/></svg>';

const EXHAUST_SVG =
	'<svg class="tok tok-svg tok-exhaust" viewBox="0 0 16 16" role="img" aria-label="exhaust">' +
	'<path d="M8 2.6a5.4 5.4 0 1 1-5.1 3.7l1.5.5A3.8 3.8 0 1 0 8 4.2z"/>' +
	'<path d="M2.2 1.6l4 1.4-2.7 3.2z"/></svg>';

/** Vervangt tokens in reeds ge-escapete HTML door icoon-markup. */
export function iconifyTokens(escapedHtml: string): string {
	return escapedHtml.replace(/:rb_([a-z0-9_]+):/g, (whole, tok: string) => {
		if (tok.startsWith('energy_')) {
			const n = tok.slice('energy_'.length);
			return `<span class="tok tok-energy" role="img" aria-label="${n} energy">${n}</span>`;
		}
		if (tok === 'might') return MIGHT_SVG;
		if (tok === 'exhaust') return EXHAUST_SVG;
		if (tok.startsWith('rune_')) {
			const rune = tok.slice('rune_'.length);
			if (RUNES.has(rune)) {
				const label = rune === 'rainbow' ? 'willekeurige rune' : `${rune} rune`;
				return `<span class="tok tok-rune tok-rune-${rune}" role="img" aria-label="${label}" title="${label}"></span>`;
			}
		}
		return whole; // onbekend token: laten staan
	});
}

/** Kaarttekst (plain, onvertrouwd) → veilige HTML met iconen. */
export function renderCardText(raw: string): string {
	return iconifyTokens(escapeHtml(raw));
}
