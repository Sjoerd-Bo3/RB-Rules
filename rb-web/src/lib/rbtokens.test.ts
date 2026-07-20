import { describe, it, expect } from 'vitest';
import { existsSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { iconifyTokens, renderCardText } from './rbtokens';

const RUNES = ['fury', 'calm', 'mind', 'body', 'order', 'chaos', 'rainbow'] as const;
const KNOWN_TOKENS = [
	'might',
	'exhaust',
	...RUNES.map((r) => `rune_${r}`),
	...Array.from({ length: 13 }, (_, n) => `energy_${n}`) // energy_0 t/m energy_12
];

const GLYPHS_DIR = fileURLToPath(new URL('../../static/glyphs', import.meta.url));

describe('iconifyTokens — bekende tokens renderen als glyph', () => {
	it('wijst elk bekend token naar zijn eigen /glyphs/{token}.svg-pad', () => {
		for (const tok of KNOWN_TOKENS) {
			const html = iconifyTokens(`:rb_${tok}:`);
			expect(html).toContain(`src="/glyphs/${tok}.svg"`);
		}
	});

	it('zet :rb_energy_N: om naar een glyph met het cijferlabel', () => {
		const html = iconifyTokens('Kost :rb_energy_3:.');
		expect(html).toContain('src="/glyphs/energy_3.svg"');
		expect(html).toContain('aria-label="3 energy"');
		expect(html).toContain('title="3 energy"');
	});

	it('houdt meercijferige energiekosten heel', () => {
		const html = iconifyTokens(':rb_energy_12:');
		expect(html).toContain('src="/glyphs/energy_12.svg"');
		expect(html).toContain('aria-label="12 energy"');
	});

	it('geeft might en exhaust zowel een aria-label als een title', () => {
		const might = iconifyTokens(':rb_might:');
		expect(might).toContain('aria-label="might"');
		expect(might).toContain('title="might"');
		const exhaust = iconifyTokens(':rb_exhaust:');
		expect(exhaust).toContain('aria-label="exhaust"');
		expect(exhaust).toContain('title="exhaust"');
	});

	it.each(RUNES)('geeft :rb_rune_%s: het juiste label', (rune) => {
		const html = iconifyTokens(`:rb_rune_${rune}:`);
		const label = rune === 'rainbow' ? 'willekeurige rune' : `${rune} rune`;
		expect(html).toContain(`aria-label="${label}"`);
		expect(html).toContain(`title="${label}"`);
		expect(html).toContain(`src="/glyphs/rune_${rune}.svg"`);
	});

	it('heeft loading="lazy" op elk glyph', () => {
		const html = iconifyTokens(':rb_might::rb_energy_5::rb_rune_calm:');
		expect(html.match(/loading="lazy"/g)?.length).toBe(3);
	});

	it('bevat een zichtbare terugval-tekst per glyph (bv. het cijfer of het label)', () => {
		expect(iconifyTokens(':rb_energy_7:')).toContain('<span class="tok-fallback">7</span>');
		expect(iconifyTokens(':rb_might:')).toContain('<span class="tok-fallback">might</span>');
		expect(iconifyTokens(':rb_rune_rainbow:')).toContain(
			'<span class="tok-fallback">willekeurige rune</span>'
		);
	});

	it('vervangt meerdere tokens in één tekst', () => {
		const html = iconifyTokens('Betaal :rb_energy_1::rb_rune_fury: en :rb_exhaust: dit.');
		expect(html).toContain('src="/glyphs/energy_1.svg"');
		expect(html).toContain('src="/glyphs/rune_fury.svg"');
		expect(html).toContain('src="/glyphs/exhaust.svg"');
	});
});

describe('iconifyTokens — onbekende tokens (allowlist)', () => {
	it('laat een niet-gevendorde energiewaarde letterlijk staan (energy_13 bestaat niet)', () => {
		expect(iconifyTokens(':rb_energy_13:')).toBe(':rb_energy_13:');
	});

	it('laat een onbekende rune letterlijk staan', () => {
		expect(iconifyTokens(':rb_rune_paars:')).toBe(':rb_rune_paars:');
	});

	it('laat een onbekend token letterlijk staan', () => {
		expect(iconifyTokens('zie :rb_wat_dan_ook: hier')).toBe('zie :rb_wat_dan_ook: hier');
	});

	it('raakt tekst zonder tokens niet aan', () => {
		expect(iconifyTokens('Gewone regeltekst met 3 : dubbele punten :.')).toBe(
			'Gewone regeltekst met 3 : dubbele punten :.'
		);
	});

	it('negeert tokens met hoofdletters of spaties (geen halve match)', () => {
		expect(iconifyTokens(':rb_RUNE_fury:')).toBe(':rb_RUNE_fury:');
		expect(iconifyTokens(':rb_rune fury:')).toBe(':rb_rune fury:');
	});
});

describe('elk gerenderd glyph-pad bestaat ook echt op schijf', () => {
	const files = new Set(existsSync(GLYPHS_DIR) ? readdirSync(GLYPHS_DIR) : []);

	it('vindt de glyphs-map (scripts/fetch-glyphs.sh moet gedraaid zijn)', () => {
		expect(existsSync(GLYPHS_DIR)).toBe(true);
	});

	it.each(KNOWN_TOKENS)('rb-web/static/glyphs/%s.svg bestaat', (tok) => {
		expect(files.has(`${tok}.svg`)).toBe(true);
	});
});

describe('renderCardText — escaping blijft intact', () => {
	it('escapet HTML-tekens in kaarttekst', () => {
		expect(renderCardText('a < b & c > d')).toBe('a &lt; b &amp; c &gt; d');
	});

	it('voert geen scripttag uit die in de kaarttekst staat', () => {
		const html = renderCardText('<script>alert(1)</script>');
		expect(html).toBe('&lt;script&gt;alert(1)&lt;/script&gt;');
		expect(html).not.toContain('<script');
	});

	it('laat geen kwaadaardige tag over uit onvertrouwde kaarttekst', () => {
		const html = renderCardText('<img src=x onerror="alert(1)"> :rb_rune_chaos:');
		// De onvertrouwde <img> is volledig ge-escaped (blijft zichtbare tekst,
		// geen echte tag) — alleen ónze eigen glyph-markup mag een echte <img>
		// opleveren.
		expect(html).not.toContain('<img src=x');
		expect(html).toContain('&lt;img src=x onerror="alert(1)"&gt;');
		expect(html).toContain('src="/glyphs/rune_chaos.svg"');
	});

	it('vervangt geen token binnen een tag (attribuut-injectie)', () => {
		// renderMarkdown draait iconifyTokens over marked-uitvoer; een token in
		// een href zou glyph-markup ín het attribuut plakken en eruit breken.
		const html = iconifyTokens('<a href="/kaart/:rb_might:">tekst :rb_might:</a>');
		expect(html).toContain('href="/kaart/:rb_might:"');
		expect(html).toContain('tekst <span class="tok" role="img" aria-label="might"');
	});

	it('laat een losse < ongemoeid staan in plaats van hem als tag te lezen', () => {
		expect(iconifyTokens('a &lt; b :rb_rune_calm: c')).toContain('src="/glyphs/rune_calm.svg"');
		expect(iconifyTokens('kracht < 3')).toBe('kracht < 3');
	});

	it('escapet vóór het iconificeren, dus tokens in ge-escapete tekst werken', () => {
		// De volgorde is veiligheidskritisch: eerst escapen, dan glyph-markup
		// injecteren — omgekeerd zou de eigen markup ge-escaped worden.
		const html = renderCardText('<b>:rb_rune_order:</b>');
		expect(html).toBe('&lt;b&gt;' + iconifyTokens(':rb_rune_order:') + '&lt;/b&gt;');
	});

	it('laat een lege tekst leeg', () => {
		expect(renderCardText('')).toBe('');
	});
});
