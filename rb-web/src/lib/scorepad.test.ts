import { describe, expect, it } from 'vitest';
import {
	MAX_SHEETS,
	SHEET_INFO,
	SHEET_ORDER,
	deckIdAt,
	defaultOptions,
	expandPages,
	pagePlan,
	parseOptions,
	serializeOptions,
	sheetTotal,
	type SheetEntry,
	type SheetKind
} from './scorepad';

/** Verkorte entry-bouwer — houdt de verwachtingen hieronder leesbaar. */
const e = (kind: SheetKind, deckRef: number | null = null): SheetEntry => ({ kind, deckRef });

describe('scorepad-opties', () => {
	it('serialiseert de standaardsituatie naar een lege query-string', () => {
		expect(serializeOptions(defaultOptions())).toBe('');
	});

	it('bewaart de volgorde en overleeft een parse/serialize-rondgang', () => {
		const qs =
			'sheets=match%3A2%2Creflection%2Cmatch&paper=a4&dup=0&ink=bw&notes=lines&bind=side&c1=aabb01&c2=112233';
		const parsed = parseOptions(new URLSearchParams(qs));
		expect(parsed.list).toEqual([e('match'), e('match'), e('reflection'), e('match')]);
		expect(parsed.paper).toBe('a4');
		expect(parsed.duplicate).toBe(false);
		expect(parsed.ink).toBe('bw');
		expect(parsed.notesStyle).toBe('lines');
		expect(parsed.binding).toBe('side');
		expect(parsed.c1).toBe('aabb01');
		expect(parsed.c2).toBe('112233');
		expect(serializeOptions(parsed)).toBe(qs);
	});

	it('slaat spelerkleuren lowercase op en laat de standaard buiten de URL', () => {
		const parsed = parseOptions(new URLSearchParams('c1=AABB01'));
		expect(parsed.c1).toBe('aabb01');
		expect(parsed.c2).toBeNull();
		expect(serializeOptions(defaultOptions())).toBe('');
	});

	it('negeert ongeldige spelerkleuren', () => {
		expect(parseOptions(new URLSearchParams('c1=rood')).c1).toBeNull();
		expect(parseOptions(new URLSearchParams('c1=fff')).c1).toBeNull();
		expect(parseOptions(new URLSearchParams('c1=aabbccdd')).c1).toBeNull();
	});

	it('matchalt doet mee in een volgorde-rondgang', () => {
		const qs = 'sheets=matchalt%2Cmatch';
		const parsed = parseOptions(new URLSearchParams(qs));
		expect(parsed.list).toEqual([e('matchalt'), e('match')]);
		expect(serializeOptions(parsed)).toBe(qs);
	});

	it('negeert een onbekende bind-waarde', () => {
		expect(parseOptions(new URLSearchParams('bind=diagonaal')).binding).toBe('none');
	});

	it('een expliciete sheets-parameter vervangt de standaardlijst volledig', () => {
		const parsed = parseOptions(new URLSearchParams('sheets=notes:2'));
		expect(parsed.list).toEqual([e('notes'), e('notes')]);
	});

	it('klemt rommelige aantallen en negeert onbekende veltypen', () => {
		const parsed = parseOptions(
			new URLSearchParams('sheets=solo:-3,ffa:abc,onzin:2,duo,match:9999')
		);
		// solo:-3 en ffa:NaN → 0 exemplaren; onbekend type valt weg; kaal type = 1.
		expect(parsed.list[0]).toEqual(e('duo'));
		// match:9999 vult tot het totaalplafond en niet verder.
		expect(parsed.list.length).toBe(MAX_SHEETS);
		expect(parsed.list.filter((x) => x.kind === 'match').length).toBe(MAX_SHEETS - 1);
	});

	it('hanteert het totaalplafond over runs heen', () => {
		const parsed = parseOptions(
			new URLSearchParams('sheets=match:20,notes:20,solo:20')
		);
		expect(parsed.list.length).toBe(MAX_SHEETS);
	});

	it('deck-veltypen staan achteraan en maken de rondgang mee, mét decks-param', () => {
		const qs = 'sheets=deck%401%2Cregistration%401&decks=0b154c76-14c9-4d7a-9d3f-2a06fe63b1af';
		const parsed = parseOptions(new URLSearchParams(qs));
		expect(parsed.list).toEqual([e('deck', 1), e('registration', 1)]);
		expect(parsed.decks).toEqual(['0b154c76-14c9-4d7a-9d3f-2a06fe63b1af']);
		expect(serializeOptions(parsed)).toBe(qs);
		expect(SHEET_ORDER.slice(-2)).toEqual(['deck', 'registration']);
		expect(SHEET_INFO.deck.group).toBe('deck');
		expect(SHEET_INFO.registration.group).toBe('deck');
	});

	it('meerdere decks: elk vel zijn eigen bron, rondgang behouden', () => {
		const qs =
			'sheets=deck%401%3A2%2Cregistration%401%2Cmatch%2Cdeck%402&decks=id-een%2Cid-twee';
		const parsed = parseOptions(new URLSearchParams(qs));
		expect(parsed.list).toEqual([
			e('deck', 1),
			e('deck', 1),
			e('registration', 1),
			e('match'),
			e('deck', 2)
		]);
		expect(parsed.decks).toEqual(['id-een', 'id-twee']);
		expect(serializeOptions(parsed)).toBe(qs);
		expect(deckIdAt(parsed, 1)).toBe('id-een');
		expect(deckIdAt(parsed, 2)).toBe('id-twee');
		expect(deckIdAt(parsed, 3)).toBeNull();
		expect(deckIdAt(parsed, null)).toBeNull();
	});

	it("het oude 'deck='-param blijft werken als alias: één deck, ref 1 op alle deck-vellen", () => {
		const parsed = parseOptions(
			new URLSearchParams('sheets=deck%2Cregistration%2Cmatch&deck=0b154c76-14c9-4d7a-9d3f-2a06fe63b1af')
		);
		expect(parsed.list).toEqual([e('deck', 1), e('registration', 1), e('match')]);
		expect(parsed.decks).toEqual(['0b154c76-14c9-4d7a-9d3f-2a06fe63b1af']);
		// Serialiseert naar de nieuwe vorm; die vorm parset naar hetzelfde
		// resultaat — de alias normaliseert dus zonder informatieverlies.
		const qs = serializeOptions(parsed);
		expect(qs).toBe(
			'sheets=deck%401%2Cregistration%401%2Cmatch&decks=0b154c76-14c9-4d7a-9d3f-2a06fe63b1af'
		);
		expect(parseOptions(new URLSearchParams(qs))).toEqual(parsed);
	});

	it('een deck-param zonder deck-vel blijft bewaard — de keuze en de prefill staan los', () => {
		const parsed = parseOptions(new URLSearchParams('deck=abcd1234'));
		expect(parsed.decks).toEqual(['abcd1234']);
		expect(serializeOptions(parsed)).toBe('decks=abcd1234');
	});

	it('een @-ref zonder bijbehorende decks-entry valt terug op null', () => {
		expect(parseOptions(new URLSearchParams('sheets=deck%401')).list).toEqual([e('deck', null)]);
		expect(parseOptions(new URLSearchParams('sheets=deck%403&decks=alleen-een')).list).toEqual([
			e('deck', null)
		]);
		expect(parseOptions(new URLSearchParams('sheets=deck%400&decks=x1234567')).list).toEqual([
			e('deck', null)
		]);
		expect(parseOptions(new URLSearchParams('sheets=deck%40abc&decks=x1234567')).list).toEqual([
			e('deck', null)
		]);
		// '@' op een niet-deck-vel is betekenisloos en wordt genegeerd.
		expect(parseOptions(new URLSearchParams('sheets=match%401&decks=x1234567')).list).toEqual([
			e('match')
		]);
	});

	it('een ongeldige id in decks valt weg, maar refs blijven bij hun eigen id horen', () => {
		const parsed = parseOptions(
			new URLSearchParams('sheets=deck%401%2Cdeck%402&decks=niet%20geldig%2Cwel-geldig')
		);
		expect(parsed.decks).toEqual(['wel-geldig']);
		// '@1' wees naar de weggevallen id → null; '@2' schuift mee naar de
		// nieuwe positie van zíjn id, niet naar de verkeerde.
		expect(parsed.list).toEqual([e('deck', null), e('deck', 1)]);
	});

	it('plafonneert decks op hetzelfde plafond als de vellen — uitgeschreven literal', () => {
		// Review #346: onbegrensd betekende honderden parallelle rb-api-fetches
		// per gecrafte URL. Literal 40 (niet MAX_SHEETS) zodat een meebewegende
		// constante deze test niet stil groen houdt (#293-les).
		const ids = Array.from({ length: 45 }, (_, i) => `id-${i}`).join(',');
		const parsed = parseOptions(new URLSearchParams(`decks=${ids}`));
		expect(parsed.decks.length).toBe(40);
	});

	it('dedupliceert dubbele deck-ids met behoud van refs naar het eerste exemplaar', () => {
		const parsed = parseOptions(
			new URLSearchParams('sheets=deck%401%2Cdeck%402&decks=zelfde-id%2Czelfde-id')
		);
		expect(parsed.decks).toEqual(['zelfde-id']);
		expect(parsed.list).toEqual([e('deck', 1), e('deck', 1)]);
	});

	it('run-length groepeert alleen over gelijke (kind, deckRef)-paren', () => {
		const o = defaultOptions();
		o.decks = ['id-een', 'id-twee'];
		o.list = [e('deck', 1), e('deck', 1), e('deck', 2), e('deck', null)];
		expect(serializeOptions(o)).toBe(
			'sheets=deck%401%3A2%2Cdeck%402%2Cdeck&decks=id-een%2Cid-twee'
		);
	});

	it('wijst rommel in de deck(s)-params af en serialiseert leeg niet', () => {
		expect(defaultOptions().decks).toEqual([]);
		expect(parseOptions(new URLSearchParams('deck=')).decks).toEqual([]);
		expect(parseOptions(new URLSearchParams('deck=a b')).decks).toEqual([]);
		expect(parseOptions(new URLSearchParams('decks=')).decks).toEqual([]);
		expect(parseOptions(new URLSearchParams('decks=a%2Fb')).decks).toEqual([]);
		expect(parseOptions(new URLSearchParams('decks=%3Cscript%3E')).decks).toEqual([]);
		// Grens: 64 tekens mag nog, 65 niet — uitgeschreven zodat een
		// meebewegende constante dit niet stil groen houdt (#293-les).
		expect(parseOptions(new URLSearchParams(`decks=${'a'.repeat(64)}`)).decks).toEqual([
			'a'.repeat(64)
		]);
		expect(parseOptions(new URLSearchParams(`decks=${'a'.repeat(65)}`)).decks).toEqual([]);
		expect(serializeOptions(defaultOptions())).toBe('');
	});

	it('shortLabels zijn onderling uniek — het volgorde-paneel moet vooraan al onderscheiden', () => {
		// Het volgorde-paneel kapt lange labels af; als twee veltypen met
		// dezelfde tekst beginnen, ogen ze daar identiek. shortLabel bestaat
		// precies dáárvoor, dus uniekheid is het contract.
		const shorts = SHEET_ORDER.map((k) => SHEET_INFO[k].shortLabel);
		expect(new Set(shorts).size).toBe(shorts.length);
	});

	it('een lange run overleeft de rondgang — de UI-grens en de parser-grens zijn dezelfde', () => {
		// Review #343: 25 gelijke vellen (UI stond dat toe) verloren er 5 bij
		// het herladen doordat de parser per run op 20 klemde. Nu is het
		// totaalplafond de enige grens, met uitgeschreven literals zodat een
		// meebewegende constante deze test niet stil groen houdt (#293-les).
		const parsed = parseOptions(new URLSearchParams('sheets=match:25'));
		expect(parsed.list.length).toBe(25);
		expect(serializeOptions(parsed)).toBe('sheets=match%3A25');
	});
});

describe('scorepad-paginaplan', () => {
	it('milestone review beslaat twee fysieke pagina’s per exemplaar', () => {
		const o = defaultOptions();
		o.list = [e('milestone'), e('milestone')];
		expect(expandPages(o)).toEqual([
			{ page: 'milestone', deckRef: null },
			{ page: 'milestone2', deckRef: null },
			{ page: 'milestone', deckRef: null },
			{ page: 'milestone2', deckRef: null }
		]);
	});

	it('A5 geeft één vel per pagina, in de samengestelde volgorde, mét deck-bron', () => {
		const o = defaultOptions();
		o.decks = ['id-een'];
		o.list = [e('match'), e('deck', 1), e('match'), e('notes')];
		expect(pagePlan(o)).toEqual([
			[{ page: 'match', deckRef: null }],
			[{ page: 'deck', deckRef: 1 }],
			[{ page: 'match', deckRef: null }],
			[{ page: 'notes', deckRef: null }]
		]);
	});

	it('A4-snijstapel verdubbelt elk vel op zijn eigen pagina', () => {
		const o = defaultOptions();
		o.list = [e('match'), e('match')];
		o.paper = 'a4';
		const m = { page: 'match', deckRef: null };
		expect(pagePlan(o)).toEqual([
			[m, m],
			[m, m]
		]);
		expect(sheetTotal(o)).toBe(4);
	});

	it('A4 op volgorde bundelt per twee en laat het laatste vak leeg bij oneven', () => {
		const o = defaultOptions();
		o.list = [e('match'), e('match'), e('notes')];
		o.paper = 'a4';
		o.duplicate = false;
		expect(pagePlan(o)).toEqual([
			[
				{ page: 'match', deckRef: null },
				{ page: 'match', deckRef: null }
			],
			[{ page: 'notes', deckRef: null }, null]
		]);
		expect(sheetTotal(o)).toBe(3);
	});
});
