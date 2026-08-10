import { describe, expect, it } from 'vitest';
import {
	certaintyLevel,
	certaintyParts,
	citationEssence,
	normalizeAnswerRefs,
	normalizeRuleCode,
	prepareAnswerMarkers,
	splitSettled,
	stripDuplicateRuleRefs,
	type CitationRef
} from './answerFormat';

describe('normalizeAnswerRefs (#363)', () => {
	const cits: CitationRef[] = [
		{ n: 6, section: '308.1' },
		{ n: 3, section: null },
		{ n: 1, section: '312.2.b' }
	];

	it('zet [n] om naar een §-link via de citatielijst', () => {
		expect(normalizeAnswerRefs('geldt de Showdown State [6].', cits)).toBe(
			'geldt de Showdown State [§ 308.1](/rules/308.1).'
		);
	});

	it('laat [n] zonder sectie of zonder citatie staan (nooit informatie weggooien)', () => {
		expect(normalizeAnswerRefs('zie [3] en [9]', cits)).toBe('zie [3] en [9]');
	});

	it('zet pijl-vormen om op de expliciet genoemde code', () => {
		expect(normalizeAnswerRefs('resolvet [6→§308.1.a]', cits)).toBe(
			'resolvet [§ 308.1.a](/rules/308.1.a)'
		);
		expect(normalizeAnswerRefs('door [→§347.2.b]', cits)).toBe(
			'door [§ 347.2.b](/rules/347.2.b)'
		);
	});

	it('linkt kale §-codes ook buiten de citatielijst (#363-aanvulling)', () => {
		expect(normalizeAnswerRefs('volgens § 308.1 geldt', cits)).toBe(
			'volgens [§ 308.1](/rules/308.1) geldt'
		);
		// §811 staat niet in de citaties maar wordt tóch klikbaar: een echte
		// sectie onklikbaar laten ("Stap 1 — De stapelregel (§809.2)" uit de
		// screenshot) is erger dan een dode link op een model-tikfout —
		// /rules/{code} vangt onbekende codes zelf netjes af.
		expect(normalizeAnswerRefs('de tekst van §811 ontbreekt', cits)).toBe(
			'de tekst van [§ 811](/rules/811) ontbreekt'
		);
		expect(normalizeAnswerRefs('Stap 1 — De stapelregel (§809.2)', cits)).toBe(
			'Stap 1 — De stapelregel ([§ 809.2](/rules/809.2))'
		);
	});

	it('herschrijft zijn eigen linktekst niet (één pass, geen geneste links)', () => {
		const out = normalizeAnswerRefs('zie [6] en § 308.1 en [1]', cits);
		expect(out.match(/\]\(\/rules\//g)?.length).toBe(3);
		expect(out).not.toContain('[§ [§');
	});

	it('zet vette keywords om naar de gebrackete vorm, gewone nadruk niet', () => {
		expect(normalizeAnswerRefs('het **Action**- of **Reaction**-keyword', [])).toBe(
			'het [Action]- of [Reaction]-keyword'
		);
		expect(normalizeAnswerRefs('**Assault 2** en **belangrijk** en **Focus**', [])).toBe(
			'[Assault 2] en **belangrijk** en **Focus**'
		);
	});

	it('bracket de kale magnitude-vorm (vocabulairewoord + cijfer)', () => {
		expect(normalizeAnswerRefs('dezelfde unit met Deflect 1: klaar', [])).toBe(
			'dezelfde unit met [Deflect 1]: klaar'
		);
		// Al gebracket of vet: niet dubbel inpakken.
		expect(normalizeAnswerRefs('[Deflect 1] blijft en **Shield 3** wordt', [])).toBe(
			'[Deflect 1] blijft en [Shield 3] wordt'
		);
	});

	it('bracket kale vocabulairewoorden, met naam-waarborg en samenstellingen', () => {
		expect(normalizeAnswerRefs('met Repeat (1 extra) en Deflect (1e keuze)', [])).toBe(
			'met [Repeat] (1 extra) en [Deflect] (1e keuze)'
		);
		// Samenstelling: alleen de kop badgen, zoals "[Action]-keyword".
		expect(normalizeAnswerRefs('de Repeat-extra en een Reaction-mogelijkheid', [])).toBe(
			'de [Repeat]-extra en een [Reaction]-mogelijkheid'
		);
		// Naam-waarborg: kaartnamen met een keyword-woord erin blijven heel.
		expect(normalizeAnswerRefs('Hidden Blade en Legion Rearguard zijn kaarten', [])).toBe(
			'Hidden Blade en Legion Rearguard zijn kaarten'
		);
		// Niet-vocabulairewoorden blijven staan.
		expect(normalizeAnswerRefs('de Showdown State geeft Focus en Priority', [])).toBe(
			'de Showdown State geeft Focus en Priority'
		);
	});

	it('zet uitgeschreven Energy-kosten om naar het glyph-token', () => {
		expect(normalizeAnswerRefs('Blood Rush (1 Energy) met Repeat (1 extra)', [])).toBe(
			'Blood Rush (:rb_energy_1:) met [Repeat] (1 extra)'
		);
		expect(normalizeAnswerRefs('kost 13 Energy', [])).toBe('kost 13 Energy');
	});
});

describe('normalizeAnswerRefs — opaque segmenten (review #363)', () => {
	const cits: CitationRef[] = [
		{ n: 1, section: '466.2.c' },
		{ n: 2, section: '150' },
		{ n: 4, section: '348' },
		{ n: 6, section: '308.1' }
	];

	it('herschrijft niet binnen widget-markers (§-prefix in een marker is model-gedrag)', () => {
		// Zonder bescherming werd dit [[rule:[§ 348](/rules/348)]] — widget weg,
		// machine-syntax zichtbaar (HIGH-bevinding).
		expect(normalizeAnswerRefs('[[rule:§348]]', cits)).toBe('[[rule:§348]]');
		expect(normalizeAnswerRefs('Zie de regel.\n\n[[rule:§348]]\n\nEinde.', cits)).toBe(
			'Zie de regel.\n\n[[rule:§348]]\n\nEinde.'
		);
	});

	it('herschrijft niet binnen bestaande markdown-linkteksten (geen geneste links)', () => {
		expect(normalizeAnswerRefs('[Core Rules § 308.1](https://example.com/rules)', cits)).toBe(
			'[Core Rules § 308.1](https://example.com/rules)'
		);
		// Ook een [n]-linklabel blijft heel — voorheen werd het label vervangen
		// en bleef de URL als losse tekst achter.
		expect(normalizeAnswerRefs('zie [1](https://x)', cits)).toBe('zie [1](https://x)');
	});

	it('laat verbatim geciteerde tekst tussen aanhalingstekens ongemoeid', () => {
		// Kost-brackets in geciteerde kaarttekst zijn geen citatienummers.
		expect(normalizeAnswerRefs('De regel luidt: "[Reaction] — Add [1]." [2]', cits)).toBe(
			'De regel luidt: "[Reaction] — Add [1]." [§ 150](/rules/150)'
		);
		// Typografische aanhalingstekens tellen ook als quote-span.
		expect(normalizeAnswerRefs('De kaart zegt “Add [1].” [2]', cits)).toBe(
			'De kaart zegt “Add [1].” [§ 150](/rules/150)'
		);
	});

	it('keyword-, magnitude- en Energy-passes blijven ook uit quotes', () => {
		expect(normalizeAnswerRefs('de kaart zegt "Deflect 1 until end of turn (2 Energy)"', [])).toBe(
			'de kaart zegt "Deflect 1 until end of turn (2 Energy)"'
		);
	});

	it('herschrijft niet binnen inline code-spans', () => {
		expect(normalizeAnswerRefs('gebruik `**Action**` en `regel [1]` letterlijk', cits)).toBe(
			'gebruik `**Action**` en `regel [1]` letterlijk'
		);
	});

	it('een ongepaard aanhalingsteken beschermt de rest van de tekst niet', () => {
		expect(normalizeAnswerRefs('een "quote zonder eind, en § 308.1 telt gewoon', cits)).toBe(
			'een "quote zonder eind, en [§ 308.1](/rules/308.1) telt gewoon'
		);
	});

	it('quote-spans stoppen bij een regeleinde en bij de maxlengte', () => {
		// Over een newline heen is het geen quote-span: normalisatie loopt door.
		expect(normalizeAnswerRefs('een "regel\nmet § 308.1 erin"', cits)).toBe(
			'een "regel\nmet [§ 308.1](/rules/308.1) erin"'
		);
		// Een absurd lange "quote" (ongepaard + later toevallig nog een teken)
		// eist niet de halve tekst op.
		const long = 'een "' + 'x'.repeat(400) + ' en § 308.1 daarna nog een " teken';
		expect(normalizeAnswerRefs(long, cits)).toContain('[§ 308.1](/rules/308.1)');
	});
});

describe('prepareAnswerMarkers (#363, zinnen-gat)', () => {
	it('knipt een mid-zin-marker uit de zin: naam inline, widget op eigen regel erna', () => {
		expect(prepareAnswerMarkers('De tekst van [[card:Noxus Saboteur]] luidt: "X".')).toBe(
			'De tekst van **Noxus Saboteur** luidt: "X".\n\n[[card:Noxus Saboteur]]'
		);
	});

	it('vervangt een dubbele marker inline in plaats van een gat achter te laten', () => {
		const out = prepareAnswerMarkers(
			'[[card:Noxus Saboteur]]\nDe tekst van [[card:Noxus Saboteur]] luidt: "X".'
		);
		expect(out).toBe('[[card:Noxus Saboteur]]\nDe tekst van **Noxus Saboteur** luidt: "X".');
	});

	it('rule-markers mid-zin worden een §-link; varianten dedupen op de genormaliseerde code', () => {
		const out = prepareAnswerMarkers('[[rule:348]]\nsluit de showdown [[rule:348.]] af.');
		expect(out).toBe('[[rule:348]]\nsluit de showdown [§ 348](/rules/348) af.');
	});

	it('laat een unieke marker op zijn eigen regel ongemoeid en dropt losse dubbele regels', () => {
		expect(prepareAnswerMarkers('[[rule:308.1]]\ntekst\n[[rule:308.1]]')).toBe(
			'[[rule:308.1]]\ntekst'
		);
	});
});

describe('normalizeRuleCode', () => {
	it('stript §-prefix en trailing punt naar de opslagvorm', () => {
		expect(normalizeRuleCode('348.')).toBe('348');
		expect(normalizeRuleCode('§348')).toBe('348');
		expect(normalizeRuleCode('§ 308.1.a')).toBe('308.1.a');
	});

	it('laat een al-schone code ongemoeid (binnenste punten blijven)', () => {
		expect(normalizeRuleCode('308.1.a')).toBe('308.1.a');
		expect(normalizeRuleCode('466.2.c')).toBe('466.2.c');
	});
});

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

	it('herkent het geleefde vocabulaire uit echte antwoorden (#366)', () => {
		// Letterlijke labels uit de screenshots: het model houdt zich niet aan
		// het prompt-vocabulaire, dus de mapping moet het geleefde ook kennen.
		expect(certaintyLevel('Zeker')).toBe('ok');
		expect(certaintyLevel('Zeker (voor onderstaande gevallen)')).toBe('ok');
		expect(certaintyLevel('Hoog — §308.1.a is expliciet')).toBe('ok');
		expect(certaintyLevel('Bevestigd door §102.11b en §534.5a')).toBe('ok');
		// #366 was destijds aangepast: 'Waarschijnlijk wel' viel eerst bewust op
		// unsure terug; met het geleefde vocabulaire hoort 'waarschijnlijk' bij warn.
		expect(certaintyLevel('Waarschijnlijk wel')).toBe('warn');
		expect(certaintyLevel('Gedeeltelijk afgeleid')).toBe('warn');
		expect(certaintyLevel('Laag')).toBe('warn');
		expect(certaintyLevel('Twijfel over de timing')).toBe('warn');
		expect(certaintyLevel('Onbekend')).toBe('unsure');
	});

	it("matcht op het eerste woord: 'Onzeker…' is nooit 'ok'", () => {
		// Mutatie-check: een naïeve includes('zeker')-implementatie geeft hier
		// 'ok' — deze test moet dat vangen (bewezen in de review-run, #366).
		expect(certaintyLevel('Onzeker')).toBe('unsure');
		expect(certaintyLevel('Onzeker — geen expliciete regel gevonden')).toBe('unsure');
		// Ankering: 'zeker' als níet-eerste woord telt evenmin.
		expect(certaintyLevel('Redelijk zeker')).toBe('unsure');
	});

	it('geeft community-consensus (#51) een eigen niveau', () => {
		expect(certaintyLevel('Community-consensus (3 bronnen)')).toBe('community');
	});

	it('valt zonder of met onbekend label terug op unsure', () => {
		expect(certaintyLevel(null)).toBe('unsure');
		expect(certaintyLevel(undefined)).toBe('unsure');
		expect(certaintyLevel('Vermoedelijk')).toBe('unsure');
	});
});

describe('certaintyParts (#366, zekerheid-chip)', () => {
	it('zonder scheider is het hele label de chip-kop', () => {
		expect(certaintyParts('Zeker')).toEqual({ head: 'Zeker', rest: '' });
	});

	it('splitst op het eerste " — " en laat de scheider zelf vallen', () => {
		expect(certaintyParts('Hoog — §308.1.a is expliciet')).toEqual({
			head: 'Hoog',
			rest: '§308.1.a is expliciet'
		});
	});

	it('splitst op de eerste "(" en houdt het haakje in de rest', () => {
		expect(certaintyParts('Zeker (voor onderstaande gevallen)')).toEqual({
			head: 'Zeker',
			rest: '(voor onderstaande gevallen)'
		});
	});

	it('kiest de vroegste van beide scheiders', () => {
		expect(certaintyParts('Community-consensus (3 bronnen) — zie forum')).toEqual({
			head: 'Community-consensus',
			rest: '(3 bronnen) — zie forum'
		});
	});

	it('valt terug op het hele label als de kop leeg zou zijn', () => {
		expect(certaintyParts('(alleen haakjes)')).toEqual({ head: '(alleen haakjes)', rest: '' });
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
