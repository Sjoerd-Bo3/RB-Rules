import { describe, expect, it } from 'vitest';
import {
	fromDeckSections,
	parseDeckText,
	piltoverDeckIdFromInput,
	type DeckPrefill
} from './deckPrefill';

describe('fromDeckSections', () => {
	it('normaliseert secties in de vaste volgorde en laat lege secties weg', () => {
		const prefill = fromDeckSections('Jinx Aggro', [
			{ section: 'maindeck', cards: [{ cardCode: 'OGN-100', quantity: 3, cardName: 'Get Excited!' }] },
			{ section: 'bench', cards: [] },
			{ section: 'legend', cards: [{ cardCode: 'OGN-001', quantity: 1, cardName: 'Jinx' }] },
			{ section: 'battlefields', cards: [{ cardCode: 'OGN-200', quantity: 2, cardName: 'The Rift' }] }
		]);
		expect(prefill).toEqual({
			name: 'Jinx Aggro',
			sections: [
				{ section: 'legend', rows: [{ qty: 1, name: 'Jinx' }] },
				{ section: 'battlefields', rows: [{ qty: 2, name: 'The Rift' }] },
				{ section: 'maindeck', rows: [{ qty: 3, name: 'Get Excited!' }] }
			]
		} satisfies DeckPrefill);
	});

	it('valt terug op de cardCode als cardName ontbreekt of leeg is', () => {
		const prefill = fromDeckSections(null, [
			{
				section: 'maindeck',
				cards: [
					{ cardCode: 'OGN-042', quantity: 2, cardName: null },
					{ cardCode: 'OGN-043', quantity: 1, cardName: '  ' },
					{ cardCode: 'OGN-044', quantity: 1 }
				]
			}
		]);
		expect(prefill.sections[0].rows.map((r) => r.name)).toEqual(['OGN-042', 'OGN-043', 'OGN-044']);
	});

	it('zet onbekende secties achteraan, in hun oorspronkelijke volgorde', () => {
		const prefill = fromDeckSections(null, [
			{ section: 'extra', cards: [{ cardCode: 'A-1', quantity: 1, cardName: 'A' }] },
			{ section: 'sideboard', cards: [{ cardCode: 'B-1', quantity: 1, cardName: 'B' }] },
			{ section: 'anders', cards: [{ cardCode: 'C-1', quantity: 1, cardName: 'C' }] }
		]);
		expect(prefill.sections.map((s) => s.section)).toEqual(['sideboard', 'extra', 'anders']);
	});

	it('normaliseert de deck-naam: lege of witruimte-naam wordt null', () => {
		expect(fromDeckSections('  ', []).name).toBeNull();
		expect(fromDeckSections(null, []).name).toBeNull();
		expect(fromDeckSections(' Poro Pile ', []).name).toBe('Poro Pile');
	});

	it('normaliseert sectiesleutels naar lowercase', () => {
		const prefill = fromDeckSections(null, [
			{ section: 'Maindeck', cards: [{ cardCode: 'A-1', quantity: 1, cardName: 'A' }] }
		]);
		expect(prefill.sections[0].section).toBe('maindeck');
	});
});

describe('parseDeckText', () => {
	it('leest alle vier de regelvormen en hangt ze zonder kop onder maindeck', () => {
		const prefill = parseDeckText('3x Get Excited!\n2 Mysterious Portal\nFlame Chompers x4\nJinx');
		expect(prefill).toEqual({
			name: null,
			sections: [
				{
					section: 'maindeck',
					rows: [
						{ qty: 3, name: 'Get Excited!' },
						{ qty: 2, name: 'Mysterious Portal' },
						{ qty: 4, name: 'Flame Chompers' },
						{ qty: 1, name: 'Jinx' }
					]
				}
			]
		} satisfies DeckPrefill);
	});

	it('herkent sectiekoppen (met en zonder dubbele punt, case-insensitief) en sorteert de secties', () => {
		const text = ['Sideboard:', '2x Tech Card', 'LEGEND', '1x Jinx', 'Main Deck:', '3 Filler'].join('\n');
		const prefill = parseDeckText(text);
		expect(prefill.sections).toEqual([
			{ section: 'legend', rows: [{ qty: 1, name: 'Jinx' }] },
			{ section: 'maindeck', rows: [{ qty: 3, name: 'Filler' }] },
			{ section: 'sideboard', rows: [{ qty: 2, name: 'Tech Card' }] }
		]);
	});

	it('voegt secties met dezelfde kop samen — ook door elkaar geplakt', () => {
		const text = ['Deck:', '1x Eerste', 'Runes:', '2x Rune', 'Maindeck:', '1x Tweede'].join('\n');
		const prefill = parseDeckText(text);
		expect(prefill.sections).toEqual([
			{ section: 'runes', rows: [{ qty: 2, name: 'Rune' }] },
			{
				section: 'maindeck',
				rows: [
					{ qty: 1, name: 'Eerste' },
					{ qty: 1, name: 'Tweede' }
				]
			}
		]);
	});

	it('mapt enkel- en meervoudige koppen op dezelfde canonieke sectie', () => {
		const text = ['Battlefield:', '1x The Rift', 'Champions:', '1x Vi'].join('\n');
		const prefill = parseDeckText(text);
		expect(prefill.sections.map((s) => s.section)).toEqual(['champions', 'battlefields']);
	});

	it('hangt onbekende koppen als eigen sectie achteraan', () => {
		const prefill = parseDeckText('Removal:\n2x Zap\nLegend:\n1x Jinx');
		expect(prefill.sections).toEqual([
			{ section: 'legend', rows: [{ qty: 1, name: 'Jinx' }] },
			{ section: 'removal', rows: [{ qty: 2, name: 'Zap' }] }
		]);
	});

	it('slaat lege regels en //-commentaar over, ook achteraan een regel', () => {
		const text = ['// mijn lijst', '', '3x Get Excited! // beste kaart', '   ', '1x Jinx'].join('\n');
		const prefill = parseDeckText(text);
		expect(prefill.sections).toEqual([
			{
				section: 'maindeck',
				rows: [
					{ qty: 3, name: 'Get Excited!' },
					{ qty: 1, name: 'Jinx' }
				]
			}
		]);
	});

	it('gooit nooit: rommel levert hoogstens een lege lijst op', () => {
		expect(parseDeckText('')).toEqual({ name: null, sections: [] });
		expect(parseDeckText('\n\n// alleen commentaar\n:')).toEqual({ name: null, sections: [] });
		// Kop zonder rijen eronder → sectie bestaat niet (lege secties weglaten).
		expect(parseDeckText('Sideboard:')).toEqual({ name: null, sections: [] });
		// qty 0 is rommel en valt stil weg.
		expect(parseDeckText('0x Niks\n0 Nada')).toEqual({ name: null, sections: [] });
	});

	it('overleeft Windows-regeleindes', () => {
		const prefill = parseDeckText('Legend:\r\n1x Jinx\r\n');
		expect(prefill.sections).toEqual([{ section: 'legend', rows: [{ qty: 1, name: 'Jinx' }] }]);
	});

	it('rondgang: geplakte tekst en API-secties leveren dezelfde prefill op', () => {
		// Dezelfde decklijst langs beide wegen — de vellen mogen geen verschil zien.
		const viaText = parseDeckText('Legend:\n1x Jinx\nMaindeck:\n3x Get Excited!\nSideboard:\n2x Tech Card');
		const viaApi = fromDeckSections(null, [
			{ section: 'sideboard', cards: [{ cardCode: 'S-1', quantity: 2, cardName: 'Tech Card' }] },
			{ section: 'maindeck', cards: [{ cardCode: 'M-1', quantity: 3, cardName: 'Get Excited!' }] },
			{ section: 'legend', cards: [{ cardCode: 'L-1', quantity: 1, cardName: 'Jinx' }] }
		]);
		expect(viaText).toEqual(viaApi);
	});
});

describe('piltoverDeckIdFromInput', () => {
	const uuid = '0b154c76-14c9-4d7a-9d3f-2a06fe63b1af';

	it('haalt de id uit een Piltover-link, ook met query, fragment of trailing slash', () => {
		expect(piltoverDeckIdFromInput(`https://piltoverarchive.com/decks/view/${uuid}`)).toBe(uuid);
		expect(piltoverDeckIdFromInput(`https://piltoverarchive.com/decks/view/${uuid}/`)).toBe(uuid);
		expect(piltoverDeckIdFromInput(`https://piltoverarchive.com/decks/view/${uuid}?utm=x`)).toBe(uuid);
		expect(piltoverDeckIdFromInput(`https://piltoverarchive.com/decks/view/${uuid}#lijst`)).toBe(uuid);
		expect(piltoverDeckIdFromInput(`  /decks/view/${uuid}  `)).toBe(uuid);
	});

	it('accepteert een kale uuid of id, met witruimte eromheen', () => {
		expect(piltoverDeckIdFromInput(uuid)).toBe(uuid);
		expect(piltoverDeckIdFromInput(`  ${uuid}\n`)).toBe(uuid);
		expect(piltoverDeckIdFromInput('abcd1234')).toBe('abcd1234');
	});

	it('wijst rommel af', () => {
		expect(piltoverDeckIdFromInput('')).toBeNull();
		expect(piltoverDeckIdFromInput('   ')).toBeNull();
		expect(piltoverDeckIdFromInput('kort')).toBeNull();
		expect(piltoverDeckIdFromInput('geen id, gewoon tekst')).toBeNull();
		expect(piltoverDeckIdFromInput('https://piltoverarchive.com/decks/')).toBeNull();
		// Te lange id: afwijzen, niet stil afkappen.
		expect(piltoverDeckIdFromInput('a'.repeat(65))).toBeNull();
		expect(piltoverDeckIdFromInput(`/decks/view/${'a'.repeat(65)}`)).toBeNull();
		// 64 tekens is precies de grens en mag nog wél.
		expect(piltoverDeckIdFromInput('a'.repeat(64))).toBe('a'.repeat(64));
	});
});
