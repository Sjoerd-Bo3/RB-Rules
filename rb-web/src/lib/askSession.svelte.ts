// Sessie-store voor /ask (#248). Vraag, antwoord, de lopende stream én de
// AbortController leven hier op module-niveau in plaats van in
// `routes/ask/+page.svelte`: bij client-side navigatie unmount die component,
// en daarmee sneuvelde vroeger de fetch/ReadableStream (en verdween het
// antwoord). De stream-lus draait dus hier; de pagina leest en rendert alleen.
//
// SSR: de module wordt op de server geëvalueerd en zou daar door alle
// bezoekers gedeeld worden — daarom schrijft niets in deze module tijdens het
// renderen. Elke mutatie hangt aan een browser-actie (submit, stream, opslag)
// en `localStorage`/`speechSynthesis` worden expliciet afgetast.

import { deserialize } from '$app/forms';
import type { ActionResult } from '@sveltejs/kit';
import { applyFrame, parseFrames, type LiveAnswer } from '$lib/askStream';
import {
	ASK_CURRENT_KEY,
	decodeSession,
	encodeSession,
	RELOAD_INTERRUPTED,
	type StoredAnswer
} from '$lib/askPersist';
import { quotaMessage } from '$lib/quota';
import type { AskResult, AskTurn } from '$lib/types';

/** Alles wat één vraag nodig heeft — genoeg om hem ook via het
 *  niet-streamende pad opnieuw te versturen (de "Opnieuw proberen"-knop). */
export interface AskRequest {
	question: string;
	/** Doorvragen (#41): eerdere rondes, al ingekort tot de laatste drie. */
	turns: AskTurn[];
	photo: Blob | null;
	/** Aanpak-keuze (#153); alleen meesturen als er een account is. */
	approach?: string;
	/** Hoofdvraag ⇒ invoerveld leeg na versturen; doorvragen ⇒ laten staan. */
	clearQuestion: boolean;
}

const CONNECTION_INTERRUPTED = 'De verbinding viel weg — dit antwoord is mogelijk onvolledig.';
const AI_INTERRUPTED = 'De AI viel halverwege uit — dit antwoord is mogelijk onvolledig.';
const STOPPED = 'Je hebt dit antwoord gestopt — het is onvolledig.';

/** Niet elke delta naar localStorage: dat zou per woord serialiseren. */
const SAVE_INTERVAL_MS = 750;

/** Streamen kan niet overal; zonder ReadableStream/TextDecoder loopt de vraag
 *  via de gewone (niet-streamende) form action. */
const canStream = () =>
	typeof ReadableStream === 'function' && typeof TextDecoder === 'function';

async function blobToBase64(blob: Blob): Promise<string> {
	const bytes = new Uint8Array(await blob.arrayBuffer());
	let bin = '';
	for (let i = 0; i < bytes.length; i += 0x8000)
		bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
	return btoa(bin);
}

export class AskSession {
	/** Concept-vraag in het invoerveld — overleeft navigatie mee. */
	draft = $state('');
	/** Het afgeronde (of onderbroken) antwoord dat de pagina toont. */
	answer = $state<StoredAnswer | null>(null);
	/** De groeiende tussenstand zolang de stream loopt. */
	live = $state<LiveAnswer | null>(null);
	busy = $state(false);
	/** Harde fout (quota, rate limit, rb-api-fout) — vervangt het antwoord. */
	error = $state<string | null>(null);
	/** Alleen de afronding wordt aan screenreaders gemeld; het groeiende
	 *  antwoord is bewust geen live-region. */
	announce = $state('');
	/** Brak de verbinding ná response-start maar vóór het eerste antwoordwoord,
	 *  dan kán rb-api al aan de (betaalde) LLM-call begonnen zijn: geen stille
	 *  herkansing, maar een expliciete knop. */
	retry = $state<AskRequest | null>(null);
	/** Starttijd van de lopende vraag: de wachtfase leidt er zijn tekst uit af,
	 *  ook als je halverwege terugkomt op de pagina. */
	startedAt = $state(0);
	/** Voorlezen (#31) — hier, zodat "Stop voorlezen" ook na navigatie werkt. */
	speaking = $state(false);

	/** De pagina hangt hier het verversen van de duurstatistiek aan
	 *  (invalidateAll). Bewust een haakje in plaats van een `$app/navigation`-
	 *  import: sta je niet meer op /ask, dan hoort er niets herladen te worden. */
	onAnswered: (() => void) | null = null;

	#abort: AbortController | null = null;
	#restored = false;
	#lastSave = 0;
	/** Tijdens het verlaten van de pagina breekt de browser de stream af en
	 *  loopt onze catch nog één keer. Die zou "de verbinding viel weg" over de
	 *  opgeslagen momentopname schrijven, terwijl de bezoeker gewoon herlaadde —
	 *  dus vanaf `pagehide` schrijven we niets meer. */
	#unloading = false;

	constructor() {
		if (typeof window === 'undefined') return;
		window.addEventListener('pagehide', () => (this.#unloading = true));
		// bfcache-terugkeer: de sessie leeft nog, dus opslaan mag weer.
		window.addEventListener('pageshow', () => (this.#unloading = false));
	}

	/** Eenmalig terughalen wat er vóór een reload stond. In-memory state wint
	 *  altijd: kom je via client-side navigatie terug, dan loopt de sessie nog. */
	restore() {
		if (this.#restored) return;
		this.#restored = true;
		if (this.busy || this.answer || this.live) return;
		const stored = this.#read();
		if (stored) this.answer = stored;
	}

	/** Antwoord van tafel (en uit de opslag). */
	clear() {
		this.stopSpeech();
		this.answer = null;
		this.live = null;
		this.error = null;
		this.retry = null;
		this.announce = '';
		this.#forget();
	}

	/** Lopende stream afbreken op verzoek van de bezoeker. Wat er al staat
	 *  blijft staan — als onderbroken antwoord. */
	stop() {
		this.#abort?.abort();
	}

	toggleSpeech() {
		if (typeof speechSynthesis === 'undefined') return;
		if (this.speaking) {
			this.stopSpeech();
			return;
		}
		const plain = (this.answer?.answer ?? '')
			.replace(/\[\[(rule|card):[^\]]+\]\]/g, '')
			.replace(/[#*_`>|-]/g, ' ')
			.replace(/\[(\d+)\]/g, '')
			.replace(/\s+/g, ' ')
			.trim();
		if (!plain) return;
		const u = new SpeechSynthesisUtterance(plain);
		u.lang = 'nl-NL';
		u.onend = () => (this.speaking = false);
		u.onerror = () => (this.speaking = false);
		speechSynthesis.speak(u);
		this.speaking = true;
	}

	stopSpeech() {
		if (typeof speechSynthesis !== 'undefined') speechSynthesis.cancel();
		this.speaking = false;
	}

	/** Start een vraag. Loopt door als de pagina ondertussen unmount. */
	ask(req: AskRequest) {
		if (this.busy) return;
		this.busy = true;
		this.error = null;
		this.retry = null;
		this.announce = '';
		this.live = null;
		this.answer = null;
		this.startedAt = Date.now();
		this.stopSpeech();
		this.#forget();
		void this.#run(req);
	}

	/** De expliciete herkansing na een afgebroken maar wél gestarte stream —
	 *  via de niet-streamende route, zodat het antwoord in één keer landt. */
	async retryAsk() {
		const pending = this.retry;
		if (!pending || this.busy) return;
		this.retry = null;
		this.busy = true;
		this.startedAt = Date.now();
		try {
			await this.#fallback(pending);
		} finally {
			this.busy = false;
		}
	}

	async #run(req: AskRequest) {
		if (!canStream()) {
			try {
				await this.#fallback(req);
			} finally {
				this.busy = false;
			}
			return;
		}
		const controller = new AbortController();
		this.#abort = controller;
		// Expliciet error-frame van rb-api (fout ná de 200) — apart van een
		// verbindingsbreuk, zodat een echte fout als fout getoond wordt en
		// alleen een breuk de retry-knop geeft (#103/#107).
		let serverError: string | null = null;
		try {
			const images = req.photo
				? // mediaType uit het bestand zelf: downscale() kan het originele
					// File (PNG/WebP) teruggeven — een vast 'image/jpeg'-label laat
					// de Anthropic-API de mismatch weigeren.
					[{ mediaType: req.photo.type || 'image/jpeg', data: await blobToBase64(req.photo) }]
				: undefined;
			let res: Response;
			try {
				res = await fetch('/ask/stream', {
					method: 'POST',
					headers: { 'content-type': 'application/json' },
					signal: controller.signal,
					body: JSON.stringify({
						question: req.question,
						history: req.turns.length ? req.turns : undefined,
						images,
						approach: req.approach
					})
				});
			} catch {
				if (controller.signal.aborted) {
					this.announce = 'Antwoord gestopt.';
					return;
				}
				// Geen response-headers gezien: veilige automatische terugval.
				await this.#fallback(req);
				return;
			}
			const gate = quotaMessage(res.status);
			if (gate) {
				// Rate-limit/quota/sessiepoort (#42): terugvallen raakt exact
				// dezelfde poort — gewoon melden.
				this.error = gate;
				this.announce = 'Antwoord mislukt.';
				return;
			}
			if (!res.ok || !res.body) {
				// Nette foutstatus vóór het streamen. De proxy markeert met
				// retry:true dat rb-api al aan het werk kán zijn (verbinding brak
				// i.p.v. geweigerd) — dan expliciete knop, geen stille terugval.
				let retry = false;
				try {
					retry = Boolean(((await res.json()) as { retry?: boolean }).retry);
				} catch {
					// geen JSON-body — behandel als veilige terugval
				}
				if (retry) {
					this.retry = req;
					this.announce = 'Antwoord mislukt.';
					return;
				}
				await this.#fallback(req);
				return;
			}

			let live: LiveAnswer = {
				question: req.question,
				history: req.turns,
				questionType: null,
				citations: [],
				answer: '',
				approachReason: null
			};
			this.live = live;
			const reader = res.body.getReader();
			const decoder = new TextDecoder();
			let buffer = '';
			let finalData: Record<string, unknown> | null = null;
			for (;;) {
				const { done, value } = await reader.read();
				if (done) break;
				buffer += decoder.decode(value, { stream: true });
				const { frames, rest } = parseFrames(buffer);
				buffer = rest;
				for (const frame of frames) {
					const outcome = applyFrame(live, frame);
					if (outcome.kind === 'live') {
						live = outcome.live;
						this.live = live;
						this.#saveLive();
					} else if (outcome.kind === 'final') {
						finalData = outcome.result;
					} else if (outcome.kind === 'error') {
						serverError = outcome.message;
						throw new Error(serverError);
					}
				}
			}
			if (finalData) {
				if (finalData.ok === false && this.live?.answer) {
					// AI viel halverwege uit: het deelantwoord dat er al staat is
					// meer waard dan de kale uitvalmelding in het slotframe.
					this.#interrupt(AI_INTERRUPTED);
				} else {
					// Slotframe = het volledige AskResult (incl. kaarten, claims,
					// misvattingen) — dat vervangt de tussenstand.
					this.#settle(req, finalData as Partial<AskResult>);
				}
			} else if (!this.live?.answer) {
				// Gebroken vóór de eerste antwoordtekst: rb-api kan al aan de
				// LLM-call begonnen zijn — expliciete knop, geen stille dubbele
				// kosten. Niet op "zag ik al een frame" toetsen: het meta-frame
				// komt vóór het antwoord, en bij agentic (#107) zit daar een lange
				// stille wachtfase achter.
				this.live = null;
				this.retry = req;
				this.announce = 'Antwoord mislukt.';
			} else {
				// Midden in het antwoord gebroken: partial behouden, geen dure
				// herkansing.
				this.#interrupt(CONNECTION_INTERRUPTED);
			}
		} catch (e) {
			if (controller.signal.aborted) {
				if (this.live?.answer) this.#interrupt(STOPPED);
				else this.live = null;
				this.announce = 'Antwoord gestopt.';
			} else if (this.live?.answer) {
				this.#interrupt(CONNECTION_INTERRUPTED);
			} else if (serverError) {
				// Expliciete fout van rb-api (error-frame): eerlijk als fout tonen —
				// dit is geen verbindingskwestie die een retry oplost.
				this.live = null;
				this.error = `Vraag mislukt (${e instanceof Error ? e.message : e})`;
				this.announce = 'Antwoord mislukt.';
			} else {
				// Verbindingsbreuk zonder antwoordtekst — ook mét al gezien
				// meta-frame (agentic-wachtfase, #107): expliciete herkansing.
				this.live = null;
				this.retry = req;
				this.announce = 'Antwoord mislukt.';
			}
		} finally {
			this.#abort = null;
			this.busy = false;
		}
	}

	/** Vangnet: de bestaande niet-streamende form action, handmatig aangeroepen.
	 *  Het pad is absoluut — deze store draait door nadat je /ask verlaten hebt,
	 *  en `?/ask` zou dan tegen de verkeerde route posten. */
	async #fallback(req: AskRequest) {
		try {
			const res = await fetch('/ask?/ask', {
				method: 'POST',
				headers: { 'x-sveltekit-action': 'true' },
				body: toFormData(req)
			});
			const result = deserialize(await res.text()) as ActionResult;
			if (result.type === 'success') {
				this.#settle(req, (result.data ?? {}) as Partial<AskResult>);
			} else if (result.type === 'failure') {
				this.live = null;
				this.error = String((result.data as { error?: string })?.error ?? 'Vraag mislukt.');
				this.announce = 'Antwoord mislukt.';
			} else if (result.type === 'redirect') {
				location.href = result.location;
			} else {
				this.live = null;
				this.error = `Vraag mislukt (${result.error?.message ?? 'onbekende fout'})`;
				this.announce = 'Antwoord mislukt.';
			}
		} catch (e) {
			this.live = null;
			this.error = `Vraag mislukt (${e instanceof Error ? e.message : e})`;
			this.announce = 'Antwoord mislukt.';
		}
	}

	/** Antwoord compleet: tussenstand vervangen door het volledige resultaat. */
	#settle(req: AskRequest, data: Partial<AskResult>) {
		this.answer = {
			question: req.question,
			history: req.turns,
			answer: String(data.answer ?? ''),
			citations: data.citations ?? [],
			cards: data.cards ?? [],
			claims: data.claims ?? null,
			misconceptions: data.misconceptions ?? null,
			questionType: data.questionType ?? null,
			approachReason: data.approachReason ?? null,
			interrupted: null
		};
		this.live = null;
		if (req.clearQuestion) this.draft = '';
		this.#save();
		this.announce = 'Antwoord compleet.';
		this.onAnswered?.();
	}

	/** Onvolledig antwoord: bewaren wat er staat, eerlijk gelabeld. */
	#interrupt(reason: string) {
		const live = this.live;
		if (!live) return;
		this.answer = {
			question: live.question,
			history: live.history,
			answer: live.answer,
			citations: live.citations,
			cards: [],
			claims: null,
			misconceptions: null,
			questionType: live.questionType,
			approachReason: live.approachReason,
			interrupted: reason
		};
		this.live = null;
		this.#save();
		this.announce = 'Antwoord onderbroken.';
	}

	#saveLive() {
		const live = this.live;
		if (!live?.answer) return;
		const now = Date.now();
		if (now - this.#lastSave < SAVE_INTERVAL_MS) return;
		this.#lastSave = now;
		// Wat nú in de opslag staat is per definitie "wat je zou zien als je
		// hier herlaadt": een stream die de reload niet overleeft.
		this.#write({
			question: live.question,
			history: live.history,
			answer: live.answer,
			citations: live.citations,
			cards: [],
			claims: null,
			misconceptions: null,
			questionType: live.questionType,
			approachReason: live.approachReason,
			interrupted: RELOAD_INTERRUPTED
		});
	}

	#save() {
		this.#lastSave = Date.now();
		if (this.answer) this.#write(this.answer);
	}

	#write(answer: StoredAnswer) {
		if (this.#unloading || typeof localStorage === 'undefined') return;
		try {
			localStorage.setItem(ASK_CURRENT_KEY, encodeSession(answer));
		} catch {
			// private mode of vol quotum: persistentie is een extraatje
		}
	}

	#read(): StoredAnswer | null {
		if (typeof localStorage === 'undefined') return null;
		try {
			return decodeSession(localStorage.getItem(ASK_CURRENT_KEY));
		} catch {
			return null;
		}
	}

	#forget() {
		if (typeof localStorage === 'undefined') return;
		try {
			localStorage.removeItem(ASK_CURRENT_KEY);
		} catch {
			// zie #write
		}
	}
}

function toFormData(req: AskRequest): FormData {
	const fd = new FormData();
	fd.set('question', req.question);
	if (req.turns.length) fd.set('history', JSON.stringify(req.turns));
	if (req.approach) fd.set('approach', req.approach);
	if (req.photo) fd.set('photo', req.photo, 'board.jpg');
	return fd;
}

/** Eén sessie per browsertab; module-state, dus navigatiebestendig. */
export const askSession = new AskSession();
