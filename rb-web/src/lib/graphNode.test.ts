import { describe, expect, it } from 'vitest';
import {
	cardMeta,
	nodeLabel,
	nodeLinks,
	nodeSummary,
	refKey,
	refKind,
	truncate
} from './graphNode';

describe('refKind / refKey', () => {
	it('splitst op de eerste dubbele punt', () => {
		expect(refKind('card:OGN-001')).toBe('card');
		expect(refKey('card:OGN-001')).toBe('OGN-001');
	});

	it('houdt slashes en dubbele punten in de key intact (section-refs)', () => {
		const ref = 'section:core-rules-pdf/101.2';
		expect(refKind(ref)).toBe('section');
		expect(refKey(ref)).toBe('core-rules-pdf/101.2');
	});

	it('geeft een lege soort bij een ref zonder scheidingsteken', () => {
		expect(refKind('OGN-001')).toBe('');
		expect(refKey('OGN-001')).toBe('OGN-001');
	});
});

describe('nodeLabel', () => {
	it('toont een sectie als §code', () => {
		expect(nodeLabel('section', 'section:core/101.2', { code: '101.2', text: 'x' })).toBe('§101.2');
	});

	it('pakt het eerste gevulde labelveld', () => {
		expect(nodeLabel('card', 'card:OGN-001', { name: 'Yasuo' })).toBe('Yasuo');
		expect(nodeLabel('claim', 'claim:7', { statement: 'Deflect stopt damage' })).toBe(
			'Deflect stopt damage'
		);
		expect(nodeLabel('concept', 'concept:combat', { topic: 'combat' })).toBe('combat');
	});

	it('valt terug op de ref als er geen bruikbaar veld is', () => {
		expect(nodeLabel('source', 'source:rules-hub', { url: 'https://x' })).toBe('source:rules-hub');
		expect(nodeLabel('card', 'card:OGN-001', null)).toBe('card:OGN-001');
		expect(nodeLabel('card', 'card:OGN-001', { name: '' })).toBe('card:OGN-001');
	});
});

describe('nodeSummary', () => {
	it('vertaalt een facet-telling naar een korte uitleg per soort', () => {
		expect(nodeSummary('mechanic', { name: 'Deflect', cardCount: 12 })).toBe(
			'Mechaniek op 12 kaarten'
		);
		expect(nodeSummary('domain', { name: 'Fury', cardCount: 1 })).toBe('Domein van 1 kaart');
		expect(nodeSummary('tag', { name: 'Champion', cardCount: 4 })).toBe('Tag op 4 kaarten');
		expect(nodeSummary('set', { name: 'Origins', cardCount: 300 })).toBe('Set met 300 kaarten');
	});

	it('kiest de meest verklarende tekst (meaning vóór summary vóór text)', () => {
		expect(nodeSummary('change', { meaning: 'Deflect timing', summary: 'diff', text: 'raw' })).toBe(
			'Deflect timing'
		);
		expect(nodeSummary('change', { summary: 'diff', text: 'raw' })).toBe('diff');
		expect(nodeSummary('section', { text: 'raw' })).toBe('raw');
	});

	it('normaliseert witruimte en kapt af op een woordgrens', () => {
		const long = 'Deflect prevents the next instance of damage that would be dealt this turn';
		expect(nodeSummary('section', { text: `  Deflect\n\nprevents ` })).toBe('Deflect prevents');
		const cut = nodeSummary('section', { text: long }, 30);
		expect(cut?.endsWith('…')).toBe(true);
		expect(cut?.length).toBeLessThanOrEqual(31);
		expect(cut).not.toMatch(/\s…$/);
	});

	it('geeft null als de knoop niets te vertellen heeft', () => {
		expect(nodeSummary('source', { url: 'https://x', enabled: true })).toBeNull();
		expect(nodeSummary('claim', { statement: '   ' })).toBeNull();
		expect(nodeSummary('card', null)).toBeNull();
	});
});

describe('truncate', () => {
	it('laat korte tekst ongemoeid', () => {
		expect(truncate('kort', 20)).toBe('kort');
	});

	it('kapt hard af als er geen spatie in de tweede helft zit', () => {
		expect(truncate('aaaaaaaaaaaaaaaaaaaa', 8)).toBe('aaaaaaaa…');
	});
});

describe('cardMeta', () => {
	it('zet type, domeinen en stats op één regel', () => {
		expect(
			cardMeta({
				supertype: 'Champion',
				type: 'Unit',
				domains: ['Fury', 'Body'],
				energy: 3,
				might: 4
			})
		).toBe('Champion Unit · Fury/Body · Energy 3 · Might 4');
	});

	it('slaat ontbrekende velden over', () => {
		expect(cardMeta({ supertype: null, type: 'Spell', domains: [], energy: null, might: null })).toBe(
			'Spell'
		);
		expect(
			cardMeta({ supertype: null, type: null, domains: [], energy: null, might: null })
		).toBe('');
	});

	it('toont een energiekost van 0 (geen falsy-val)', () => {
		expect(
			cardMeta({ supertype: null, type: 'Spell', domains: ['Calm'], energy: 0, might: null })
		).toBe('Spell · Calm · Energy 0');
	});
});

describe('nodeLinks', () => {
	it('geeft kaartknopen de weg terug naar kaartpagina en kaart-verkenning', () => {
		expect(nodeLinks('card:OGN-001', 'card')).toEqual([
			{ href: '/cards/OGN-001', label: 'Naar kaartpagina' },
			{
				href: '/graph?card=OGN-001',
				label: 'Kaart-verkenning (mechanieken en interacties)'
			}
		]);
	});

	it('pelt de sourceId van een sectie-key af voor de regels-browser', () => {
		expect(nodeLinks('section:core-rules-pdf/101.2', 'section')).toEqual([
			{ href: '/rules/101.2', label: 'Lees in de regels-browser' }
		]);
	});

	it('encodeert mechanieknamen met spaties', () => {
		expect(nodeLinks('mechanic:Hidden Path', 'mechanic')[0].href).toBe(
			'/cards?mechanic=Hidden%20Path'
		);
	});

	it('geeft geen links voor soorten zonder eigen scherm', () => {
		expect(nodeLinks('claim:7', 'claim')).toEqual([]);
	});
});
