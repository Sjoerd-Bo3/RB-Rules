import { describe, expect, it } from 'vitest';
import { errorCopy } from './errorCopy';

describe('errorCopy', () => {
	it('404 geeft de niet-gevonden-variant', () => {
		const c = errorCopy(404);
		expect(c.is404).toBe(true);
		expect(c.heading).toContain('404');
		expect(c.body.length).toBeGreaterThan(0);
	});

	it('een boodschap negeren bij 404 (vaste kop)', () => {
		expect(errorCopy(404, 'Not Found').heading).toBe('404 — deze pagina bestaat niet (meer)');
	});

	it('andere status toont status + boodschap in de kop', () => {
		const c = errorCopy(500, 'Interne fout');
		expect(c.is404).toBe(false);
		expect(c.heading).toBe('500 — Interne fout');
	});

	it('valt zonder boodschap terug op een nette tekst', () => {
		const c = errorCopy(503);
		expect(c.heading.startsWith('503 — ')).toBe(true);
		expect(c.heading).not.toBe('503 — ');
	});

	it('server-fout (5xx) krijgt de server-fallback', () => {
		expect(errorCopy(500).heading).toBe('500 — Er ging iets mis aan onze kant.');
	});

	it('client-fout (4xx, niet 404) krijgt de client-fallback', () => {
		expect(errorCopy(403).heading).toBe('403 — Deze pagina kon niet worden geladen.');
	});

	it('lege/whitespace boodschap telt als geen boodschap', () => {
		expect(errorCopy(500, '   ').heading).toBe('500 — Er ging iets mis aan onze kant.');
	});
});
