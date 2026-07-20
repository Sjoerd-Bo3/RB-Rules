import { describe, it, expect } from 'vitest';
import { iconifyTokens, renderCardText } from './rbtokens';

const RUNES = ['fury', 'calm', 'mind', 'body', 'order', 'chaos', 'rainbow'] as const;

describe('iconifyTokens — tokentypes', () => {
	it('zet :rb_energy_N: om naar een bolletje met het cijfer', () => {
		const html = iconifyTokens('Kost :rb_energy_3:.');
		expect(html).toBe(
			'Kost <span class="tok tok-energy" role="img" aria-label="3 energy">3</span>.'
		);
	});

	it('houdt meercijferige energiekosten heel', () => {
		expect(iconifyTokens(':rb_energy_12:')).toContain('aria-label="12 energy"');
		expect(iconifyTokens(':rb_energy_12:')).toContain('>12</span>');
	});

	it('zet :rb_might: en :rb_exhaust: om naar hun inline-SVG', () => {
		expect(iconifyTokens(':rb_might:')).toContain('class="tok tok-svg tok-might"');
		expect(iconifyTokens(':rb_exhaust:')).toContain('class="tok tok-svg tok-exhaust"');
	});

	it.each(RUNES)('zet :rb_rune_%s: om naar een SVG met eigen vorm-klasse', (rune) => {
		const html = iconifyTokens(`:rb_rune_${rune}:`);
		expect(html).toContain(`class="tok tok-svg tok-rune tok-rune-${rune}"`);
		expect(html).toMatch(/^<svg /);
		expect(html).toMatch(/<\/svg>$/);
	});

	it('geeft elke rune een eigen tekening (vorm, niet alleen kleur)', () => {
		// De vorm zit in de path-data; twee runes mogen die nooit delen, anders
		// zijn ze op 14px alleen aan kleur te onderscheiden (#257).
		const shapes = RUNES.map((r) => {
			const html = iconifyTokens(`:rb_rune_${r}:`);
			return html.slice(html.indexOf('</title>'));
		});
		expect(new Set(shapes).size).toBe(RUNES.length);
	});

	it('houdt aria-label en title (tooltip) op elke rune', () => {
		expect(iconifyTokens(':rb_rune_mind:')).toContain('aria-label="mind rune"');
		expect(iconifyTokens(':rb_rune_mind:')).toContain('<title>mind rune</title>');
		expect(iconifyTokens(':rb_rune_rainbow:')).toContain('aria-label="willekeurige rune"');
		expect(iconifyTokens(':rb_rune_rainbow:')).toContain('<title>willekeurige rune</title>');
	});

	it('rendert de rainbow-rune met alle zes domeinsegmenten', () => {
		const html = iconifyTokens(':rb_rune_rainbow:');
		for (const w of ['w-fury', 'w-body', 'w-order', 'w-calm', 'w-mind', 'w-chaos']) {
			expect(html).toContain(`class="${w}"`);
		}
	});

	it('vervangt meerdere tokens in één tekst', () => {
		const html = iconifyTokens('Betaal :rb_energy_1::rb_rune_fury: en :rb_exhaust: dit.');
		expect(html).toContain('tok-energy');
		expect(html).toContain('tok-rune-fury');
		expect(html).toContain('tok-exhaust');
	});
});

describe('iconifyTokens — onbekende tokens', () => {
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

describe('renderCardText — escaping blijft intact', () => {
	it('escapet HTML-tekens in kaarttekst', () => {
		expect(renderCardText('a < b & c > d')).toBe('a &lt; b &amp; c &gt; d');
	});

	it('voert geen scripttag uit die in de kaarttekst staat', () => {
		const html = renderCardText('<script>alert(1)</script>');
		expect(html).toBe('&lt;script&gt;alert(1)&lt;/script&gt;');
		expect(html).not.toContain('<script');
	});

	it('laat geen enkele echte tag over uit onvertrouwde kaarttekst', () => {
		const html = renderCardText('<img src=x onerror="alert(1)"> :rb_rune_chaos:');
		expect(html).not.toContain('<img');
		// Alleen onze eigen icoon-markup mag een echte tag opleveren.
		expect(html.match(/<[a-z]/g)).toEqual(['<s', '<t', '<p']); // svg, title, path
		expect(html).toContain('tok-rune-chaos');
	});

	it('vervangt geen token binnen een tag (attribuut-injectie)', () => {
		// renderMarkdown draait iconifyTokens over marked-uitvoer; een token in
		// een href zou de icoon-markup ín het attribuut plakken en eruit breken.
		const html = iconifyTokens('<a href="/kaart/:rb_might:">tekst :rb_might:</a>');
		expect(html).toContain('href="/kaart/:rb_might:"');
		expect(html).toContain('tekst <svg class="tok tok-svg tok-might"');
	});

	it('laat een losse < ongemoeid staan in plaats van hem als tag te lezen', () => {
		expect(iconifyTokens('a &lt; b :rb_rune_calm: c')).toContain('tok-rune-calm');
		expect(iconifyTokens('kracht < 3')).toBe('kracht < 3');
	});

	it('escapet vóór het iconificeren, dus tokens in ge-escapete tekst werken', () => {
		// De volgorde is veiligheidskritisch: eerst escapen, dan icoon-markup
		// injecteren — omgekeerd zou de eigen markup ge-escaped worden.
		const html = renderCardText('<b>:rb_rune_order:</b>');
		expect(html).toBe(
			'&lt;b&gt;' + iconifyTokens(':rb_rune_order:') + '&lt;/b&gt;'
		);
	});

	it('laat een lege tekst leeg', () => {
		expect(renderCardText('')).toBe('');
	});
});
