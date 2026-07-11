import { describe, expect, it } from 'vitest';
import {
	certaintyLevel,
	citationEssence,
	splitSettled,
	stripDuplicateRuleRefs,
	type CitationRef
} from './answerFormat';

describe('splitSettled', () => {
	it('zonder newline is alles staart', () => {
		expect(splitSettled('**Oordeel:** Ja')).toEqual({ settled: '', tail: '**Oordeel:** Ja' });
	});

	it('splitst op de laatste newline', () => {
		expect(splitSettled('**Oordeel:** Ja.\n\nDe unit blijft')).toEqual({
			settled: '**Oordeel:** Ja.\n\n',
			tail: 'De unit blijft'
		});
	});

	it('verbergt een half binnengekomen widget-marker in de staart', () => {
		expect(splitSettled('Stap 1 [1].\n[[rule:466.2')).toEqual({
			settled: 'Stap 1 [1].\n',
			tail: ''
		});
	});

	it('verbergt ook een complete marker die nog op zijn newline wacht', () => {
		// Zonder dit toont de staart letterlijk "[[rule:466.2.c]]" tot het
		// volgende frame de newline brengt (review-fix).
		expect(splitSettled('Stap 1 [1].\n[[rule:466.2.c]]')).toEqual({
			settled: 'Stap 1 [1].\n',
			tail: ''
		});
		expect(splitSettled('Stap 1 [1].\n[[rule:466.2.c]')).toEqual({
			settled: 'Stap 1 [1].\n',
			tail: ''
		});
	});

	it('laat een afgeronde marker in settled ongemoeid', () => {
		expect(splitSettled('[[rule:466.2.c]]\nvervolg')).toEqual({
			settled: '[[rule:466.2.c]]\n',
			tail: 'vervolg'
		});
	});

	it('lege input blijft leeg', () => {
		expect(splitSettled('')).toEqual({ settled: '', tail: '' });
	});
});

describe('certaintyLevel', () => {
	it('herkent de bestaande labels, met en zonder toevoeging', () => {
		expect(certaintyLevel('Bevestigd')).toBe('ok');
		expect(certaintyLevel('Bevestigd (officieel)')).toBe('ok');
		expect(certaintyLevel('Afgeleid')).toBe('warn');
		expect(certaintyLevel('Onzeker')).toBe('unsure');
	});

	it('geeft community-consensus (#51) een eigen niveau', () => {
		expect(certaintyLevel('Community-consensus (3 bronnen)')).toBe('community');
	});

	it('valt zonder of met onbekend label terug op unsure', () => {
		expect(certaintyLevel(null)).toBe('unsure');
		expect(certaintyLevel(undefined)).toBe('unsure');
		expect(certaintyLevel('Waarschijnlijk wel')).toBe('unsure');
	});
});

describe('citationEssence', () => {
	it('geeft null zonder tekst', () => {
		expect(citationEssence(null)).toBeNull();
		expect(citationEssence(undefined)).toBeNull();
		expect(citationEssence('   ')).toBeNull();
	});

	it('pakt de eerste zin', () => {
		expect(
			citationEssence('A unit with Deflect cannot be chosen. Later sentences add detail.')
		).toBe('A unit with Deflect cannot be chosen.');
	});

	it('breekt niet af op punten in §-codes of afkortingen', () => {
		expect(
			citationEssence('As described in 466.2.c, e.g. a Hidden unit stays hidden. Next.')
		).toBe('As described in 466.2.c, e.g. a Hidden unit stays hidden.');
	});

	it('stript markdown en klapt witruimte samen', () => {
		expect(citationEssence('**Deflect** werkt   als\n[schild](https://x.y) tegen targeting.')).toBe(
			'Deflect werkt als schild tegen targeting.'
		);
	});

	it('kapt lange zinnen af op ~110 tekens met ellipsis', () => {
		const long = 'x'.repeat(200) + '.';
		const out = citationEssence(long)!;
		expect(out.length).toBeLessThanOrEqual(110);
		expect(out.endsWith('…')).toBe(true);
	});
});

const CITES: CitationRef[] = [
	{ n: 1, section: '466.2.c' },
	{ n: 2, section: '150' },
	{ n: 3, section: null }
];

describe('stripDuplicateRuleRefs', () => {
	it('laat alles staan zonder citaties', () => {
		const answer = '### Regelbasis\n- [1] § 466.2.c';
		expect(stripDuplicateRuleRefs(answer, [])).toBe(answer);
	});

	it('verwijdert een Regelbasis-blok dat alleen bekende verwijzingen dubbelt', () => {
		const answer = [
			'### Uitleg',
			'1. Deflect voorkomt targeting [1].',
			'### Regelbasis',
			'- [1] § 466.2.c: Deflect en targeting',
			'- [2] § 150: showdown-volgorde',
			'### Let op',
			'Alleen enemy spells worden geweerd.'
		].join('\n');
		const out = stripDuplicateRuleRefs(answer, CITES);
		expect(out).not.toContain('Regelbasis');
		expect(out).not.toContain('§ 150');
		expect(out).toContain('Deflect voorkomt targeting [1].');
		expect(out).toContain('### Let op');
		expect(out).toContain('Alleen enemy spells worden geweerd.');
	});

	it('verwijdert een Regelbasis-tabel inclusief kopregel, ook als **label**', () => {
		const answer = [
			'**Oordeel:** Nee.',
			'**Regelbasis:**',
			'| § | Bron |',
			'| --- | --- |',
			'| § 466.2.c [1] | Core Rules |',
			'| 150 [2] | Core Rules |'
		].join('\n');
		const out = stripDuplicateRuleRefs(answer, CITES);
		expect(out).toBe('**Oordeel:** Nee.');
	});

	it('verwijdert een blok met alleen [[rule:…]]-markers die de lijst dubbelen', () => {
		const answer = '### Regelbasis\n[[rule:466.2.c]]\n[[rule:150]]';
		expect(stripDuplicateRuleRefs(answer, CITES).trim()).toBe('');
	});

	it('behoudt een Regelbasis-blok met echte lopende tekst', () => {
		const answer = [
			'### Regelbasis',
			'- [1] § 466.2.c: Deflect en targeting',
			'De banlijst kent hierop een uitzondering voor showdowns.'
		].join('\n');
		expect(stripDuplicateRuleRefs(answer, CITES)).toBe(answer);
	});

	it('behoudt een blok dat naar een onbekende sectie verwijst', () => {
		const answer = '### Regelbasis\n- § 999.9: niet in de citatielijst';
		expect(stripDuplicateRuleRefs(answer, CITES)).toBe(answer);
	});

	it('verwijdert een losse §-tabel zonder Regelbasis-kop', () => {
		const answer = ['Deflect weert de spell [1].', '', '| Regel | Bron |', '| --- | --- |', '| 466.2.c | Core Rules [1] |'].join(
			'\n'
		);
		const out = stripDuplicateRuleRefs(answer, CITES);
		expect(out).toBe('Deflect weert de spell [1].');
	});

	it('behoudt tabellen zonder regelverwijzingen', () => {
		const answer = ['| Speler | Punten |', '| --- | --- |', '| A | 12 |'].join('\n');
		expect(stripDuplicateRuleRefs(answer, CITES)).toBe(answer);
	});

	it('behoudt inline [n]-verwijzingen en losse getallen in prose', () => {
		const answer = 'Je verliest 150 punten; zie § 466.2.c [1] voor de details.';
		expect(stripDuplicateRuleRefs(answer, CITES)).toBe(answer);
	});

	it('matcht ook als de citatie-sectie een §-prefix draagt', () => {
		const cites: CitationRef[] = [
			{ n: 1, section: '§ 466.2.c' },
			{ n: 2, section: '§150' }
		];
		const answer = [
			'Oordeel [1].',
			'### Regelbasis',
			'| § | Inhoud |',
			'| --- | --- |',
			'| §466.2.c | Hidden-regel |',
			'| §150 | Gear-regel |'
		].join('\n');
		expect(stripDuplicateRuleRefs(answer, cites)).toBe('Oordeel [1].');
	});
});
