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

// Keyword-badges (#359): Riftbound drukt keywords gebracket in de kaarttekst
// ([Action], [Assault 2]) — wij stylen ze als badge in de gedrukte kaartstijl
// (schuin parallellogram, cursieve caps), overal waar speltekst rendert
// (kaarten, antwoorden, regelcitaten). Bewust een VORM-patroon en geen
// allowlist: de keyword-set groeit per set mee (docs/KNOWLEDGE.md) — een
// onbekend keyword krijgt de neutrale badge. Wél tekstverlies: de haken
// verdwijnen uit de weergave (de badge ís de bracket); oppervlakken waar
// tekstgetrouwheid het doel is (admin-review) zetten keywords uit. Eisen:
// begint met hoofdletter, hooguit drie woorden, optionele magnitude —
// cijfers ([5]-citaties) en §-verwijzingen ([→§308.1.a]) vallen er per
// constructie buiten. Geen apostrof in het patroon: marked escapet ' naar
// &#39;, waardoor zo'n tak in de antwoordpaden toch nooit zou matchen
// (review #359) — en gebrackete apostrof-termen zijn kaartnamen, geen
// keywords.
const KEYWORD_RE = /\[([A-Z][A-Za-z]*(?:[ -][A-Z][A-Za-z]*){0,2}(?: \d{1,2})?)\]/g;

/** Kleurfamilie per keyword, GEMETEN op de gedrukte kaarten (workflow
 *  2026-08-10: per keyword een echte kaartafbeelding afgelezen; hexwaarden
 *  per pixel-regio bepaald). Riot kent vier badge-kleuren; magnitudes horen
 *  bij hun familie ("Assault 2" → assault). Onbekend keyword → neutraal
 *  grijs, zodat een nieuwe set niet stuk oogt maar gewoon nog geen kleur
 *  heeft. Recycle/Stun/XP worden op de kaarten NIET als badge gedrukt en
 *  staan hier bewust niet in — die matcht de regex alleen als een bron ze
 *  tóch bracket, en dan is neutraal grijs prima. */
const KEYWORD_KIND: Record<string, string> = {
	// teal #24705f — timing/permissie
	accelerate: 'kw-teal', action: 'kw-teal', ambush: 'kw-teal', flow: 'kw-teal',
	hidden: 'kw-teal', legion: 'kw-teal', 'quick-draw': 'kw-teal',
	reaction: 'kw-teal', repeat: 'kw-teal',
	// magenta #cc356e — combat/stat
	assault: 'kw-magenta', backline: 'kw-magenta', shield: 'kw-magenta', tank: 'kw-magenta',
	// lime #94b22d (zwarte letters) — progressie/beweging
	deathknell: 'kw-lime', deflect: 'kw-lime', ganking: 'kw-lime',
	hunt: 'kw-lime', level: 'kw-lime', temporary: 'kw-lime'
	// grijs #717171 is de default (o.a. Add, Buff, Burn, Empower, Equip,
	// Mighty, Predict, Unique, Vision, Weaponmaster) — niet opgesomd, zodat
	// een ongemeten nieuw keyword er automatisch bij hoort.
};

/** Gesloten keyword-vocabulaire (basisvormen, distinct mechanics uit de
 *  prod-DB, 2026-08-10) — voor de vet→gebracket-normalisatie in antwoorden
 *  (#363): alleen woorden uit deze lijst worden omgeschreven, zodat gewone
 *  vette nadruk ("**belangrijk**") met rust gelaten wordt. Het BADGE-matchen
 *  blijft bewust een vorm-patroon (zie KEYWORD_RE) zodat nieuwe sets
 *  meeliften; dit vocabulaire is alleen de omschrijf-poort. */
export const KNOWN_KEYWORDS = new Set([
	'accelerate', 'action', 'add', 'ambush', 'assault', 'backline', 'buff', 'burn',
	'deathknell', 'deflect', 'empower', 'equip', 'flow', 'ganking', 'hidden', 'hunt',
	'legion', 'level', 'mighty', 'predict', 'quick-draw', 'reaction', 'recycle',
	'repeat', 'shield', 'stun', 'tank', 'temporary', 'unique', 'vision', 'weaponmaster'
]);

/** Is dit (met optionele magnitude, bv. "Assault 2") een bekend keyword? */
export function isKnownKeyword(term: string): boolean {
	return KNOWN_KEYWORDS.has(term.replace(/ \d+$/, '').toLowerCase());
}

function keywordHtml(kw: string): string {
	const base = kw.replace(/ \d+$/, '').toLowerCase();
	const kind = KEYWORD_KIND[base] ?? '';
	return `<span class="kw${kind ? ` ${kind}` : ''}">${kw}</span>`;
}

export interface IconifyOptions {
	/** false = alleen glyphs, geen keyword-badges (admin-review toont de
	 *  brontekst letterlijk, inclusief haken — review #359). */
	keywords?: boolean;
}

function replaceInText(text: string, opts?: IconifyOptions): string {
	const out = text.replace(/:rb_([a-z0-9_]+):/g, (whole, tok: string) => {
		const info = tokenInfo(tok);
		return info ? glyphHtml(tok, info) : whole; // onbekend token: laten staan
	});
	if (opts?.keywords === false) return out;
	return out.replace(KEYWORD_RE, (_, kw: string) => keywordHtml(kw));
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
export function iconifyTokens(escapedHtml: string, opts?: IconifyOptions): string {
	return escapedHtml.replace(/<[^>]*>|[^<]+/g, (part) =>
		part.startsWith('<') ? part : replaceInText(part, opts)
	);
}

/** Kaarttekst (plain, onvertrouwd) → veilige HTML met iconen. */
export function renderCardText(raw: string, opts?: IconifyOptions): string {
	return iconifyTokens(escapeHtml(raw), opts);
}
