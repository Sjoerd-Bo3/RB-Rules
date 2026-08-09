import { describe, expect, it } from 'vitest';
import {
	MAX_SHEETS,
	defaultOptions,
	expandPages,
	pagePlan,
	parseOptions,
	serializeOptions,
	sheetTotal
} from './scorepad';

describe('scorepad-opties', () => {
	it('serialiseert de standaardsituatie naar een lege query-string', () => {
		expect(serializeOptions(defaultOptions())).toBe('');
	});

	it('bewaart de volgorde en overleeft een parse/serialize-rondgang', () => {
		const qs =
			'sheets=match%3A2%2Creflection%2Cmatch&paper=a4&dup=0&ink=bw&notes=lines&bind=side&c1=aabb01&c2=112233';
		const parsed = parseOptions(new URLSearchParams(qs));
		expect(parsed.list).toEqual(['match', 'match', 'reflection', 'match']);
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
		expect(parsed.list).toEqual(['matchalt', 'match']);
		expect(serializeOptions(parsed)).toBe(qs);
	});

	it('negeert een onbekende bind-waarde', () => {
		expect(parseOptions(new URLSearchParams('bind=diagonaal')).binding).toBe('none');
	});

	it('een expliciete sheets-parameter vervangt de standaardlijst volledig', () => {
		const parsed = parseOptions(new URLSearchParams('sheets=notes:2'));
		expect(parsed.list).toEqual(['notes', 'notes']);
	});

	it('klemt rommelige aantallen en negeert onbekende veltypen', () => {
		const parsed = parseOptions(
			new URLSearchParams('sheets=solo:-3,ffa:abc,onzin:2,duo,match:9999')
		);
		// solo:-3 en ffa:NaN → 0 exemplaren; onbekend type valt weg; kaal type = 1.
		expect(parsed.list[0]).toBe('duo');
		// match:9999 vult tot het totaalplafond en niet verder.
		expect(parsed.list.length).toBe(MAX_SHEETS);
		expect(parsed.list.filter((k) => k === 'match').length).toBe(MAX_SHEETS - 1);
	});

	it('hanteert het totaalplafond over runs heen', () => {
		const parsed = parseOptions(
			new URLSearchParams('sheets=match:20,notes:20,solo:20')
		);
		expect(parsed.list.length).toBe(MAX_SHEETS);
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
		o.list = ['milestone', 'milestone'];
		expect(expandPages(o)).toEqual(['milestone', 'milestone2', 'milestone', 'milestone2']);
	});

	it('A5 geeft één vel per pagina, in de samengestelde volgorde', () => {
		const o = defaultOptions();
		o.list = ['match', 'reflection', 'match', 'notes'];
		expect(pagePlan(o)).toEqual([['match'], ['reflection'], ['match'], ['notes']]);
	});

	it('A4-snijstapel verdubbelt elk vel op zijn eigen pagina', () => {
		const o = defaultOptions();
		o.list = ['match', 'match'];
		o.paper = 'a4';
		expect(pagePlan(o)).toEqual([
			['match', 'match'],
			['match', 'match']
		]);
		expect(sheetTotal(o)).toBe(4);
	});

	it('A4 op volgorde bundelt per twee en laat het laatste vak leeg bij oneven', () => {
		const o = defaultOptions();
		o.list = ['match', 'match', 'notes'];
		o.paper = 'a4';
		o.duplicate = false;
		expect(pagePlan(o)).toEqual([
			['match', 'match'],
			['notes', null]
		]);
		expect(sheetTotal(o)).toBe(3);
	});
});
