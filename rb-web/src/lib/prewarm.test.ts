import { describe, expect, it } from 'vitest';
import { firePrewarm } from './prewarm';

describe('firePrewarm (#154)', () => {
	it('wacht nooit op het signaal — ook niet op een hangende call', () => {
		let called = false;
		// Een promise die nooit resolvet: firePrewarm moet toch meteen terugkeren.
		firePrewarm(() => {
			called = true;
			return new Promise(() => {});
		});
		expect(called).toBe(true); // afgevuurd…
		// …en we staan alweer hier: geen await, de page-load blokkeert nooit.
	});

	it('slikt rejections in (geen unhandled rejection de page-load in)', async () => {
		firePrewarm(() => Promise.reject(new Error('rb-api plat')));
		// Eén microtask-tik: als de rejection niet afgehandeld was, faalt de
		// testrunner op een unhandled rejection.
		await new Promise((r) => setTimeout(r, 0));
	});

	it('slikt ook synchrone fouten in', () => {
		expect(() =>
			firePrewarm(() => {
				throw new Error('kapot vóór de fetch');
			})
		).not.toThrow();
	});
});
