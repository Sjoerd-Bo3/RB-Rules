import { describe, expect, it } from 'vitest';
import {
	ASK_MAX_AGE_MS,
	decodeSession,
	encodeSession,
	RELOAD_INTERRUPTED,
	type StoredAnswer
} from './askPersist';

const answer = (over: Partial<StoredAnswer> = {}): StoredAnswer => ({
	question: 'Mag ik reageren tijdens een showdown?',
	history: [],
	answer: '**Oordeel:** ja, met de gebruikelijke prioriteit.',
	citations: [],
	cards: [],
	claims: null,
	misconceptions: null,
	questionType: 'Ruling',
	approachReason: null,
	interrupted: null,
	...over
});

describe('ask-sessie-persistentie (#248)', () => {
	it('herstelt het antwoord na een reload', () => {
		const restored = decodeSession(encodeSession(answer()));
		expect(restored?.answer).toContain('Oordeel');
		expect(restored?.question).toBe('Mag ik reageren tijdens een showdown?');
		expect(restored?.interrupted).toBeNull();
	});

	it('houdt de onderbroken-markering vast: een door de reload gesneuvelde stream komt niet als leeg of compleet terug', () => {
		const raw = encodeSession(answer({ answer: 'Half antw', interrupted: RELOAD_INTERRUPTED }));
		const restored = decodeSession(raw);
		expect(restored?.answer).toBe('Half antw');
		expect(restored?.interrupted).toBe(RELOAD_INTERRUPTED);
	});

	it('vergeet een sessie die te oud is', () => {
		const raw = encodeSession(answer(), 1_000);
		expect(decodeSession(raw, 1_000 + ASK_MAX_AGE_MS - 1)).not.toBeNull();
		expect(decodeSession(raw, 1_000 + ASK_MAX_AGE_MS + 1)).toBeNull();
	});

	it('geeft null op alles wat niet klopt — liever leeg dan half hersteld', () => {
		expect(decodeSession(null)).toBeNull();
		expect(decodeSession('')).toBeNull();
		expect(decodeSession('{niet eens json')).toBeNull();
		expect(decodeSession('"een string"')).toBeNull();
		expect(decodeSession(JSON.stringify({ v: 99, at: Date.now(), answer: answer() }))).toBeNull();
		expect(decodeSession(JSON.stringify({ v: 1, answer: answer() }))).toBeNull();
		// Leeg antwoord is geen antwoord: dan liever een schone pagina.
		expect(decodeSession(encodeSession(answer({ answer: '   ' })))).toBeNull();
	});

	it('vult ontbrekende lijsten aan in plaats van te crashen op oude opslag', () => {
		const raw = JSON.stringify({
			v: 1,
			at: Date.now(),
			answer: { question: 'q', answer: 'a' }
		});
		const restored = decodeSession(raw);
		expect(restored).not.toBeNull();
		expect(restored?.citations).toEqual([]);
		expect(restored?.cards).toEqual([]);
		expect(restored?.claims).toBeNull();
		expect(restored?.questionType).toBeNull();
	});
});
