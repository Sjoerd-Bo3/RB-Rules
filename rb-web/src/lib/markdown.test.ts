import { describe, it, expect } from 'vitest';
import { renderMarkdown, renderInlineMarkdown } from './markdown';

describe('renderMarkdown — keywords en tokens in antwoorden (#359)', () => {
	it('rendert een gebracket keyword midden in een antwoordzin als badge', () => {
		const html = renderMarkdown('Alleen kaarten met het [Action]- of [Reaction]-keyword.');
		expect(html).toContain('<span class="kw kw-teal">Action</span>');
		expect(html).toContain('<span class="kw kw-teal">Reaction</span>');
	});

	it('laat citatienummers ([5]) ongemoeid', () => {
		expect(renderMarkdown('Dit volgt uit de regels [5].')).not.toContain('class="kw"');
	});
});

describe('renderMarkdown — linkclassificatie (review #363)', () => {
	it('behandelt protocol-relatieve URL\'s (//host) als extern: nieuw tabblad + noopener', () => {
		// '//evil.com' begint met '/' maar is een externe, LLM-stuurbare link.
		const html = renderMarkdown('[klik](//evil.com)');
		expect(html).toContain('target="_blank"');
		expect(html).toContain('rel="noopener noreferrer"');
	});

	it('houdt echte interne paden intern (geen nieuw tabblad)', () => {
		const html = renderMarkdown('[§ 348](/rules/348)');
		expect(html).toContain('href="/rules/348"');
		expect(html).not.toContain('target="_blank"');
	});
});

describe('renderInlineMarkdown — oordeel-banner (#359)', () => {
	it('rendert vet als <strong> in plaats van letterlijke sterretjes', () => {
		const html = renderInlineMarkdown('het **Action**- of **Reaction**-keyword');
		expect(html).toContain('<strong>Action</strong>');
		expect(html).not.toContain('**');
	});

	it('produceert geen blok-elementen (inline-variant)', () => {
		expect(renderInlineMarkdown('gewoon een zin')).not.toContain('<p>');
	});

	it('escapet HTML uit het antwoord (zelfde verdediging als renderMarkdown)', () => {
		const html = renderInlineMarkdown('<script>alert(1)</script> met [Action]');
		expect(html).not.toContain('<script');
		expect(html).toContain('<span class="kw kw-teal">Action</span>');
	});

	it('weert javascript:-links, net als het blok-pad', () => {
		const html = renderInlineMarkdown('[klik](javascript:alert(1))');
		expect(html).not.toContain('javascript:');
		expect(html).toContain('klik');
	});

	it('rendert glyph-tokens ook in de banner', () => {
		expect(renderInlineMarkdown('kost :rb_energy_2:')).toContain('src="/glyphs/energy_2.svg"');
	});
});
