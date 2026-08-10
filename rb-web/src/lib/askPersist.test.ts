import { describe, expect, it } from 'vitest';
import {
	appendThreadTurn,
	ASK_MAX_AGE_MS,
	decodeSession,
	encodeSession,
	RELOAD_INTERRUPTED,
	type StoredAnswer
} from './askPersist';
import type { AskTurn } from '$lib/types';

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

describe('weergave-thread in de opslag (#365)', () => {
	it('overleeft de round-trip: de thread komt beurt voor beurt terug', () => {
		const thread: AskTurn[] = [
			{ question: 'Mag ik moven?', answer: 'Ja, tijdens je Action Phase.' },
			{ question: 'En als hij exhausted is?', answer: 'Nee — een exhausted unit movet niet.' }
		];
		const restored = decodeSession(encodeSession(answer({ thread })));
		expect(restored?.thread).toEqual(thread);
	});

	it('geeft een lege thread op opslag van vóór dit veld', () => {
		const raw = JSON.stringify({
			v: 1,
			at: Date.now(),
			answer: { question: 'q', answer: 'a' }
		});
		expect(decodeSession(raw)?.thread).toEqual([]);
	});

	it('filtert kapotte beurten weg en houdt alleen vraag + antwoord over', () => {
		const raw = JSON.stringify({
			v: 1,
			at: Date.now(),
			answer: {
				question: 'q',
				answer: 'a',
				thread: [
					null,
					'geen object',
					{ question: 'zonder antwoord' },
					{ question: 'geldig', answer: 'ook geldig', citations: ['hoort er niet in'] }
				]
			}
		});
		expect(decodeSession(raw)?.thread).toEqual([{ question: 'geldig', answer: 'ook geldig' }]);
	});

	it('capt een uit de hand gelopen opgeslagen thread op 6 — de nieuwste blijven', () => {
		const thread = Array.from({ length: 9 }, (_, i) => ({ question: `v${i}`, answer: `a${i}` }));
		const restored = decodeSession(encodeSession(answer({ thread })));
		expect(restored?.thread).toHaveLength(6);
		expect(restored?.thread?.[0]).toEqual({ question: 'v3', answer: 'a3' });
		expect(restored?.thread?.[5]).toEqual({ question: 'v8', answer: 'a8' });
	});
});

describe('appendThreadTurn (#365)', () => {
	it('schuift de beurt achteraan en laat de bestaande staan', () => {
		const next = appendThreadTurn([{ question: 'v1', answer: 'a1' }], {
			question: 'v2',
			answer: 'a2'
		});
		expect(next).toEqual([
			{ question: 'v1', answer: 'a1' },
			{ question: 'v2', answer: 'a2' }
		]);
	});

	it('capt op 6 beurten: de oudste valt eraf, de nieuwste komt erbij', () => {
		let thread: AskTurn[] = [];
		for (let i = 0; i < 7; i++) thread = appendThreadTurn(thread, { question: `v${i}`, answer: `a${i}` });
		expect(thread).toHaveLength(6);
		expect(thread[0]).toEqual({ question: 'v1', answer: 'a1' });
		expect(thread[5]).toEqual({ question: 'v6', answer: 'a6' });
	});

	it('neemt per beurt alleen vraag + antwoord over — geen meereizende extra velden', () => {
		const dik = {
			question: 'v',
			answer: 'a',
			citations: [{ n: 1 }]
		} as AskTurn;
		expect(appendThreadTurn([], dik)).toEqual([{ question: 'v', answer: 'a' }]);
	});
});
