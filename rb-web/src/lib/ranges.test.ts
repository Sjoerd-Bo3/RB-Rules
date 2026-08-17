import { describe, expect, it } from 'vitest';
import { compactRanges } from './ranges';

describe('compactRanges', () => {
	it('lege lijst is een lege string', () => {
		expect(compactRanges([])).toBe('');
	});

	it('losse nummers blijven los', () => {
		expect(compactRanges([12, 203])).toBe('12, 203');
	});

	it('opeenvolgende nummers vouwen tot een reeks', () => {
		expect(compactRanges([12, 45, 46, 47, 203])).toBe('12, 45–47, 203');
	});

	it('een paar van twee blijft een reeks van twee', () => {
		expect(compactRanges([4, 5])).toBe('4–5');
	});

	it('sorteert en ontdubbelt de invoer', () => {
		expect(compactRanges([3, 1, 2, 2, 7])).toBe('1–3, 7');
	});

	it('een volledig ontbrekende set is een enkele reeks', () => {
		expect(compactRanges(Array.from({ length: 298 }, (_, i) => i + 1))).toBe('1–298');
	});
});
