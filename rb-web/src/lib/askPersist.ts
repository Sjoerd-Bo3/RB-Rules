// Persistentie van het huidige /ask-antwoord (#248). De sessie-store houdt
// het antwoord buiten de component (dat overleeft client-side navigatie);
// deze codec laat het óók een page reload overleven. Bewust pure functies:
// de store doet de localStorage-I/O, hier zit alleen de vorm-, versie- en
// houdbaarheidsbewaking — en die is unit-testbaar.

import type { AskCard, AskCitation, AskClaim, AskMisconception, AskTurn } from '$lib/types';

/** Naast (niet in plaats van) `rb-ask-history`, de lijst eerdere vragen. */
export const ASK_CURRENT_KEY = 'rb-ask-current';

/** Ouder dan dit tonen we niet meer terug: een antwoord van gisteren dat bij
 *  het openen van /ask weer opduikt is verwarrend, geen service. */
export const ASK_MAX_AGE_MS = 12 * 60 * 60 * 1000;

/** Een stream die door het herladen sneuvelde: het deelantwoord is meer waard
 *  dan een lege pagina, mits eerlijk gelabeld (#248). */
export const RELOAD_INTERRUPTED =
	'Het herladen van de pagina onderbrak dit antwoord — het is mogelijk onvolledig.';

/** Het antwoord zoals de pagina het toont. Eén vorm voor alle wegen ernaartoe
 *  (streaming-slotframe, niet-streamende action, hersteld uit opslag), zodat
 *  het antwoordpaneel maar één bron kent. */
export interface StoredAnswer {
	question: string;
	history: AskTurn[];
	answer: string;
	citations: AskCitation[];
	cards: AskCard[];
	claims: AskClaim[] | null;
	misconceptions: AskMisconception[] | null;
	questionType: string | null;
	approachReason: string | null;
	/** Niet-null ⇒ onvolledig antwoord (verbinding weg, zelf gestopt, of door
	 *  een reload afgebroken); de tekst is de melding die erbij hoort. */
	interrupted: string | null;
}

interface StoredSession {
	v: 1;
	at: number;
	answer: StoredAnswer;
}

const VERSION = 1;

export function encodeSession(answer: StoredAnswer, now = Date.now()): string {
	return JSON.stringify({ v: VERSION, at: now, answer } satisfies StoredSession);
}

/** Leest terug wat er stond. Alles wat niet klopt (corrupt, oude versie, te
 *  oud, geen antwoordtekst) geeft null: liever een schone pagina dan een
 *  half hersteld antwoord. */
export function decodeSession(raw: string | null | undefined, now = Date.now()): StoredAnswer | null {
	if (!raw) return null;
	let parsed: unknown;
	try {
		parsed = JSON.parse(raw);
	} catch {
		return null;
	}
	if (!parsed || typeof parsed !== 'object') return null;
	const session = parsed as Partial<StoredSession>;
	if (session.v !== VERSION) return null;
	if (typeof session.at !== 'number' || now - session.at > ASK_MAX_AGE_MS) return null;
	const answer = session.answer;
	if (!answer || typeof answer !== 'object') return null;
	if (typeof answer.answer !== 'string' || !answer.answer.trim()) return null;
	if (typeof answer.question !== 'string') return null;
	return {
		question: answer.question,
		history: Array.isArray(answer.history) ? answer.history : [],
		answer: answer.answer,
		citations: Array.isArray(answer.citations) ? answer.citations : [],
		cards: Array.isArray(answer.cards) ? answer.cards : [],
		claims: Array.isArray(answer.claims) ? answer.claims : null,
		misconceptions: Array.isArray(answer.misconceptions) ? answer.misconceptions : null,
		questionType: typeof answer.questionType === 'string' ? answer.questionType : null,
		approachReason: typeof answer.approachReason === 'string' ? answer.approachReason : null,
		interrupted: typeof answer.interrupted === 'string' ? answer.interrupted : null
	};
}
