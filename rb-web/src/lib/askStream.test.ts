import { describe, expect, it } from 'vitest';
import { applyFrame, parseFrames, type LiveAnswer } from './askStream';

const empty = (): LiveAnswer => ({
	question: 'Wat doet Deflect?',
	history: [],
	questionType: null,
	citations: [],
	answer: '',
	approachReason: null
});

describe('parseFrames (#31)', () => {
	it('houdt een half frame vast tot de volgende chunk', () => {
		const first = parseFrames('{"type":"delta","text":"Een "}\n{"type":"delta","tex');
		expect(first.frames).toHaveLength(1);
		expect(first.rest).toBe('{"type":"delta","tex');
		const second = parseFrames(first.rest + 't":"unit"}\n');
		expect(second.frames).toEqual([{ type: 'delta', text: 'unit' }]);
		expect(second.rest).toBe('');
	});

	it('slaat corrupte en lege regels over zonder de stroom te breken', () => {
		const { frames } = parseFrames('\n{kapot}\n{"type":"delta","text":"ok"}\n');
		expect(frames).toEqual([{ type: 'delta', text: 'ok' }]);
	});
});

describe('applyFrame (#31)', () => {
	it('meta zet citaties en aanpak-reden vóór het antwoord', () => {
		const out = applyFrame(empty(), {
			type: 'meta',
			questionType: 'Ruling',
			citations: [{ n: 1 }] as never,
			approachReason: 'quota'
		});
		expect(out.kind).toBe('live');
		if (out.kind !== 'live') return;
		expect(out.live.questionType).toBe('Ruling');
		expect(out.live.citations).toHaveLength(1);
		expect(out.live.approachReason).toBe('quota');
	});

	it('delta groeit het antwoord aan', () => {
		let live = empty();
		for (const text of ['Een ', 'unit ', 'blijft.']) {
			const out = applyFrame(live, { type: 'delta', text });
			if (out.kind !== 'live') throw new Error('verwacht live');
			live = out.live;
		}
		expect(live.answer).toBe('Een unit blijft.');
	});

	it('final levert het volledige resultaat, error een melding', () => {
		expect(applyFrame(empty(), { type: 'final', result: { answer: 'klaar' } })).toEqual({
			kind: 'final',
			result: { answer: 'klaar' }
		});
		expect(applyFrame(empty(), { type: 'error', error: 'rb-ai plat' })).toEqual({
			kind: 'error',
			message: 'rb-ai plat'
		});
	});

	it('negeert onbekende frames en een delta zonder tekst', () => {
		expect(applyFrame(empty(), { type: 'iets-nieuws' }).kind).toBe('ignored');
		expect(applyFrame(empty(), { type: 'delta' }).kind).toBe('ignored');
	});
});
