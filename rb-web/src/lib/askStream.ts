// Frame-verwerking van de /ask/stream-NDJSON (#31), bewust zonder runes en
// zonder fetch: de sessie-store ($lib/askSession.svelte.ts) doet de I/O, deze
// module doet de vertaling van bytes naar antwoordstate. Zo is het deel dat
// stuk kan gaan (halve frames, onbekende types, een error-frame midden in het
// antwoord) los unit-testbaar — de store zelf is browser-werk.

import type { AskCitation, AskTurn } from '$lib/types';

/** Eén NDJSON-frame zoals rb-api het stuurt. Alles optioneel: de proxy geeft
 *  frames ongeparseerd door, dus de client is de eerste die ze valideert. */
export interface AskFrame {
	type?: string;
	text?: string;
	questionType?: string;
	citations?: AskCitation[];
	approachReason?: string | null;
	result?: Record<string, unknown>;
	error?: string;
}

/** Het groeiende antwoord tijdens het streamen. */
export interface LiveAnswer {
	question: string;
	history: AskTurn[];
	questionType: string | null;
	citations: AskCitation[];
	answer: string;
	/** #153: terugval-reden uit het meta-frame — de melding hoort niet te
	 *  wachten op het slotframe (agentic heeft een lange stille fase). */
	approachReason: string | null;
}

/** Wat een frame met de sessie doet. `live` levert de nieuwe tussenstand,
 *  `final` het volledige AskResult, `error` een expliciete fout van rb-api. */
export type FrameOutcome =
	| { kind: 'live'; live: LiveAnswer }
	| { kind: 'final'; result: Record<string, unknown> }
	| { kind: 'error'; message: string }
	| { kind: 'ignored' };

/** Splitst de decoder-buffer in complete regels; `rest` is de (mogelijk
 *  halve) staart die op de volgende chunk wacht. Onparseerbare regels worden
 *  overgeslagen — een weggevallen verbinding laat vaak een half frame achter. */
export function parseFrames(buffer: string): { frames: AskFrame[]; rest: string } {
	const frames: AskFrame[] = [];
	let rest = buffer;
	let nl: number;
	while ((nl = rest.indexOf('\n')) >= 0) {
		const line = rest.slice(0, nl).trim();
		rest = rest.slice(nl + 1);
		if (!line) continue;
		try {
			frames.push(JSON.parse(line) as AskFrame);
		} catch {
			// half of corrupt frame: overslaan, de stroom loopt door
		}
	}
	return { frames, rest };
}

export function applyFrame(live: LiveAnswer, frame: AskFrame): FrameOutcome {
	if (frame.type === 'meta') {
		// Citaties zijn vóór het antwoord al bekend: daarmee kunnen
		// [[rule:…]]-widgets tijdens het streamen al renderen.
		return {
			kind: 'live',
			live: {
				...live,
				questionType: frame.questionType ?? null,
				citations: frame.citations ?? [],
				approachReason: frame.approachReason ?? null
			}
		};
	}
	if (frame.type === 'delta' && typeof frame.text === 'string') {
		return { kind: 'live', live: { ...live, answer: live.answer + frame.text } };
	}
	if (frame.type === 'final') {
		return { kind: 'final', result: frame.result ?? {} };
	}
	if (frame.type === 'error') {
		return { kind: 'error', message: String(frame.error ?? 'stream-fout') };
	}
	return { kind: 'ignored' };
}
