// Riot's kaartteksten bevatten icon-tokens (:rb_energy_1:, :rb_might:,
// :rb_exhaust:, :rb_rune_fury: …). Deze module zet ge-escapete tekst om naar
// HTML met Riots officiële glyphs (rb-web/static/glyphs/, opgehaald via
// scripts/fetch-glyphs.sh — styling in app.css). Zelfde renderer voor
// kaartpagina's én markdown-antwoorden.
//
// Vendoren i.p.v. hotlinken naar Riots CDN (#257): same-origin, dus geen
// extra derde-partij-host in het renderpad van élke pagina; werkt in de PWA
// offline; en het `/latest/`-segment in Riots CDN-pad kan stil roteren. Kost
// eenmalig ~25 KB.

const escapeHtml = (s: string) =>
	s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const RUNES = new Set(['fury', 'calm', 'mind', 'body', 'order', 'chaos', 'rainbow']);
const MAX_ENERGY = 12;

type TokenInfo = { label: string; fallback: string; cssClass: string };

/**
 * Allowlist: alleen deze 22 tokens hebben een glyph op schijf (scripts/
 * fetch-glyphs.sh). Onbekende tokens (verkeerd getypt, of een toekomstig
 * token dat nog niet gevendord is) geven `null`, zodat de tekst letterlijk
 * blijft staan i.p.v. een gebroken afbeelding te renderen.
 */
function tokenInfo(tok: string): TokenInfo | null {
	const energyMatch = /^energy_(0|[1-9][0-9]?)$/.exec(tok);
	if (energyMatch) {
		const n = Number(energyMatch[1]);
		if (n > MAX_ENERGY) return null;
		return { label: `${n} energy`, fallback: String(n), cssClass: 'tok-glyph-energy' };
	}
	if (tok === 'might') return { label: 'might', fallback: 'might', cssClass: 'tok-glyph-might' };
	if (tok === 'exhaust') {
		return { label: 'exhaust', fallback: 'exhaust', cssClass: 'tok-glyph-exhaust' };
	}
	if (tok.startsWith('rune_')) {
		const rune = tok.slice('rune_'.length);
		if (!RUNES.has(rune)) return null;
		const label = rune === 'rainbow' ? 'willekeurige rune' : `${rune} rune`;
		return { label, fallback: label, cssClass: 'tok-glyph-rune' };
	}
	return null;
}

/**
 * Glyph-markup voor één token. `role`/`aria-label`/`title` zitten op de
 * wrapper — die levert de toegankelijke naam, dus de `<img>` zelf krijgt een
 * lege `alt` (anders leest een screenreader het label dubbel). Als
 * `/glyphs/{tok}.svg` niet laadt, zet `onerror` een klasse die de `<img>`
 * verbergt en de tekst-terugval (`.tok-fallback`, CSS-verborgen zolang het
 * glyph er wél is) zichtbaar maakt — zo blijft er altijd iets betekenisvols
 * staan i.p.v. een leeg gat of een gebroken-afbeelding-icoon.
 */
function glyphHtml(tok: string, info: TokenInfo): string {
	return (
		`<span class="tok" role="img" aria-label="${info.label}" title="${info.label}">` +
		`<img class="tok-glyph ${info.cssClass}" src="/glyphs/${tok}.svg" alt="" aria-hidden="true" ` +
		`loading="lazy" width="16" height="16" onerror="this.classList.add('tok-glyph-error')">` +
		`<span class="tok-fallback">${info.fallback}</span>` +
		`</span>`
	);
}

function replaceInText(text: string): string {
	return text.replace(/:rb_([a-z0-9_]+):/g, (whole, tok: string) => {
		const info = tokenInfo(tok);
		return info ? glyphHtml(tok, info) : whole; // onbekend token: laten staan
	});
}

/**
 * Vervangt tokens in reeds ge-escapete HTML door glyph-markup.
 *
 * Alleen in tekstposities, nooit binnen een tag: `renderMarkdown` draait dit
 * over marked-uitvoer, en een token in een link-URL (`[x](/a/:rb_might:)`)
 * zou anders glyph-markup — inclusief aanhalingstekens — ín het href-
 * attribuut plakken en daaruit ontsnappen. De invoer is ge-escaped, dus elke
 * echte `<` hoort bij een tag die wij zelf hebben gemaakt.
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
