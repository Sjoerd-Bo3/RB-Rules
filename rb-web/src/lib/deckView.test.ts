import { describe, it, expect } from 'vitest';
import { parseDeckView, sectionTotal } from './deckView';

describe('parseDeckView', () => {
	it('geeft het grid als er niets bewaard is', () => {
		expect(parseDeckView(null)).toBe('grid');
	});

	it('herstelt een bewaarde lijstkeuze', () => {
		expect(parseDeckView('list')).toBe('list');
	});

	it('houdt grid expliciet grid', () => {
		expect(parseDeckView('grid')).toBe('grid');
	});

	it('valt terug op het grid bij rommel in localStorage', () => {
		// Een gebruiker (of een oude versie) kan er van alles in gezet hebben;
		// de pagina mag daar nooit op vastlopen of leeg van blijven.
		for (const raw of ['', 'GRID', 'List', 'tabel', '{"view":"list"}', 'null']) {
			expect(parseDeckView(raw)).toBe('grid');
		}
	});
});

describe('sectionTotal', () => {
	it('telt aantallen op, niet regels', () => {
		expect(sectionTotal([{ quantity: 3 }, { quantity: 1 }])).toBe(4);
	});

	it('telt één regel met een hoog aantal volledig mee', () => {
		// Runes zitten typisch als 8× of 12× in één regel — het sectietotaal
		// moet 12 tonen, niet 1.
		expect(sectionTotal([{ quantity: 12 }])).toBe(12);
	});

	it('is 0 voor een lege sectie', () => {
		expect(sectionTotal([])).toBe(0);
	});
});
