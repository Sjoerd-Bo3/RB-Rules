// Hover-preview voor §-verwijzings-chips in /ask-antwoorden (#370). De chips
// zijn kale `<a href="/rules/…">`-links binnen {@html}-markdown (#363); dit
// module levert de pure bouwstenen — href→code, positionering, citatie-lookup
// en de fetch-cache — zodat AnswerView alleen nog DOM-orkestratie doet en de
// logica unit-testbaar blijft.

/** Wat de popover toont: regeltekst plus (hooguit) de directe ouderregel —
 *  compacter dan RuleWidget, want dit is een blik vooraf, geen dossier. */
export interface RulePreviewData {
	code: string;
	text: string | null;
	parent: { code: string; text: string } | null;
}

/** De citatie-vorm zoals AnswerView hem doorkrijgt (bewust minimaal getypeerd,
 *  net als RuleWidget: we valideren alleen wat we zelf gebruiken). */
export interface PreviewCitation {
	section?: string | null;
	text?: string | null;
	parents?: { code: string; text: string }[] | null;
}

/** §-code uit een interne chip-href (`/rules/348.2a`, eventueel met query of
 *  fragment). Alles wat geen enkel-segments /rules/-pad is levert null —
 *  de /rules-hub zelf, diepere paden (waaronder onze eigen /preview-proxy) en
 *  onherstelbaar kapotte percent-encoding doen dus niet mee. */
export function ruleCodeFromHref(href: string | null | undefined): string | null {
	if (!href || !href.startsWith('/rules/')) return null;
	let rest = href.slice('/rules/'.length);
	const cut = rest.search(/[?#]/);
	if (cut >= 0) rest = rest.slice(0, cut);
	if (!rest || rest.includes('/')) return null;
	try {
		const code = decodeURIComponent(rest).trim();
		return code || null;
	} catch {
		return null;
	}
}

export interface RectLike {
	top: number;
	bottom: number;
	left: number;
	right: number;
}

export interface SizeLike {
	width: number;
	height: number;
}

export interface PopoverPlacement {
	left: number;
	top: number;
	below: boolean;
}

const clamp = (v: number, lo: number, hi: number) => Math.min(Math.max(v, lo), Math.max(lo, hi));

/** Plaatst de popover bij de link binnen het viewport: onder de link als daar
 *  ruimte is, anders erboven; past geen van beide, dan de kant met de meeste
 *  ruimte (top alsnog geklemd, zodat de kop leesbaar blijft). Horizontaal
 *  uitgelijnd op de linker linkrand, geklemd op de viewportranden. Coördinaten
 *  zijn viewport-relatief (position: fixed). */
export function placePopover(
	link: RectLike,
	pop: SizeLike,
	viewport: SizeLike,
	gap = 6,
	margin = 8
): PopoverPlacement {
	const roomBelow = viewport.height - link.bottom - gap - margin;
	const roomAbove = link.top - gap - margin;
	const below = pop.height <= roomBelow || roomBelow >= roomAbove;
	const rawTop = below ? link.bottom + gap : link.top - gap - pop.height;
	return {
		left: clamp(link.left, margin, viewport.width - pop.width - margin),
		top: clamp(rawTop, margin, viewport.height - pop.height - margin),
		below
	};
}

/** Preview uit de al aanwezige citatielijst — zelfde exacte match als
 *  RuleWidget (sectiecodes zijn genormaliseerd opgeslagen). Als ouder tonen we
 *  alleen de láátste uit de keten: RuleParentLookup levert ze van boven naar
 *  beneden, dus dat is de directe ouder. */
export function citationPreview(
	citations: PreviewCitation[] | null | undefined,
	code: string
): RulePreviewData | null {
	const cite = citations?.find((c) => c.section === code);
	if (!cite) return null;
	return {
		code,
		text: cite.text ?? null,
		parent: cite.parents?.length ? cite.parents[cite.parents.length - 1] : null
	};
}

/** Contract van de fetcher: data bij succes, null bij een echte 404 (sectie
 *  bestaat niet), throw bij transporteproblemen. */
export type PreviewFetcher = (code: string) => Promise<RulePreviewData | null>;

/** Client-side cache met één in-flight-verzoek per code (#370: geen
 *  fetch-storm bij heen-en-weer hoveren). Drie uitkomsten van `get`:
 *  - data — gevonden (gecachet);
 *  - null — sectie bestaat niet; óók gecachet, want een 404 is een antwoord;
 *  - undefined — transportfout; bewust NIET gecachet, zodat een hikje van de
 *    proxy een latere hover niet permanent stil houdt. */
export class RulePreviewCache {
	#settled = new Map<string, RulePreviewData | null>();
	#inflight = new Map<string, Promise<RulePreviewData | null | undefined>>();
	#fetcher: PreviewFetcher;

	constructor(fetcher: PreviewFetcher) {
		this.#fetcher = fetcher;
	}

	get(code: string): Promise<RulePreviewData | null | undefined> {
		if (this.#settled.has(code)) return Promise.resolve(this.#settled.get(code));
		const running = this.#inflight.get(code);
		if (running) return running;
		const p = this.#fetcher(code)
			.then((data) => {
				this.#settled.set(code, data);
				return data;
			})
			.catch((): undefined => undefined)
			.finally(() => {
				this.#inflight.delete(code);
			});
		this.#inflight.set(code, p);
		return p;
	}
}

interface PreviewResponse {
	code: string;
	text: string | null;
	parents: { code: string; text: string }[];
}

/** Gedeelde cache over alle AnswerView-instanties (de thread-weergave rendert
 *  er velen; §348 twee keer hoveren mag maar één verzoek kosten). Module-state
 *  is tijdens SSR gedeeld tussen bezoekers (#248) — dit is een pure data-Map
 *  die uitsluitend vanuit browser-hover-handlers gevuld wordt, nooit tijdens
 *  het renderen, en het absolute pad kent geen route-binding. */
export const sharedPreviewCache = new RulePreviewCache(async (code) => {
	const res = await fetch(`/rules/${encodeURIComponent(code)}/preview`);
	if (res.status === 404) return null;
	if (!res.ok) throw new Error(`preview ${res.status}`);
	const body = (await res.json()) as PreviewResponse;
	return {
		code: body.code,
		text: body.text,
		parent: body.parents.length ? body.parents[body.parents.length - 1] : null
	};
});
