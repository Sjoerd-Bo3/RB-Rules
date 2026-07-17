import { afterEach, describe, expect, it, vi } from 'vitest';
import {
	diffLines,
	formatChangeDate,
	formatChangeWhen,
	severityBadgeClass,
	trustLabel
} from './changeCard';

describe('diffLines', () => {
	it('herkent +/- koppen en kleurt de regels erna', () => {
		expect(diffLines('+ nieuw\noude regel\n- oud\nweg')).toEqual([
			{ text: '+ nieuw', kind: 'head' },
			{ text: 'oude regel', kind: 'add' },
			{ text: '- oud', kind: 'head' },
			{ text: 'weg', kind: 'del' }
		]);
	});

	it('slaat lege (of alleen-witruimte) regels over', () => {
		expect(diffLines('+ kop\n\n   \ntekst')).toEqual([
			{ text: '+ kop', kind: 'head' },
			{ text: 'tekst', kind: 'add' }
		]);
	});

	it('begint standaard in add-modus zonder voorafgaande kop', () => {
		expect(diffLines('los stukje tekst')).toEqual([{ text: 'los stukje tekst', kind: 'add' }]);
	});
});

describe('severityBadgeClass', () => {
	it('hoog is rood, gemiddeld geel, de rest (incl. onbekend) groen', () => {
		expect(severityBadgeClass('high')).toBe('err');
		expect(severityBadgeClass('medium')).toBe('warn-b');
		expect(severityBadgeClass('low')).toBe('ok-b');
		expect(severityBadgeClass('onbekend')).toBe('ok-b');
	});
});

describe('trustLabel', () => {
	it('tier 1 en 2 zijn officieel', () => {
		expect(trustLabel(1)).toEqual({ label: 'officieel', tone: 'official' });
		expect(trustLabel(2)).toEqual({ label: 'officieel', tone: 'official' });
	});

	it('tier 3 en hoger is community', () => {
		expect(trustLabel(3)).toEqual({ label: 'community', tone: 'community' });
		expect(trustLabel(4)).toEqual({ label: 'community', tone: 'community' });
	});

	it('zonder trustTier geen label', () => {
		expect(trustLabel(null)).toBeNull();
		expect(trustLabel(undefined)).toBeNull();
	});
});

describe('formatChangeWhen', () => {
	afterEach(() => vi.useRealTimers());

	it('toont "vandaag HH:MM" voor het huidige moment', () => {
		vi.useFakeTimers();
		vi.setSystemTime(new Date('2026-07-17T14:32:00+02:00'));
		expect(formatChangeWhen(new Date().toISOString())).toMatch(/^vandaag \d{2}:\d{2}$/);
	});

	it('toont een datum (geen "vandaag") voor eerdere dagen', () => {
		vi.useFakeTimers();
		vi.setSystemTime(new Date('2026-07-17T12:00:00Z'));
		expect(formatChangeWhen('2026-07-10T09:15:00Z')).not.toMatch(/^vandaag/);
	});
});

describe('formatChangeDate', () => {
	it('geeft een korte nl-NL datum terug', () => {
		const iso = '2026-07-10T09:15:00Z';
		expect(formatChangeDate(iso)).toBe(new Date(iso).toLocaleDateString('nl-NL'));
	});
});
