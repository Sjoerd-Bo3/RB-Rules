// Score pad-assembler (#342): opties, URL-vorm en paginaplan — zuiver en
// DOM-loos zodat alles testbaar is. De route /scorepad leest de opties uit de
// query-string (deelbaar/bookmarkbaar, en headless te printen) en schrijft ze
// er met replaceState weer in terug. De samenstelling is een GEORDENDE lijst:
// de volgorde in de URL is de afdrukvolgorde.

/** Veltypen die de assembler kan samenstellen. */
export type SheetKind =
	| 'match'
	| 'matchalt'
	| 'solo'
	| 'ffa'
	| 'duo'
	| 'tournament'
	| 'reflection'
	| 'milestone'
	| 'notes'
	| 'deck'
	| 'registration';

/** Eén fysieke pagina. Milestone review beslaat twee pagina's; de tweede
 *  bestaat alleen in het paginaplan, niet als los te kiezen veltype. */
export type SheetPage = SheetKind | 'milestone2';

export type Paper = 'a5' | 'a4';
export type Ink = 'color' | 'bw';
export type NotesStyle = 'dots' | 'lines';
/** Ringband-marge: extra witruimte aan de bind-rand (perforatie/spiraal). */
export type Binding = 'none' | 'top' | 'side';

export interface ScorepadOptions {
	/** Geordende samenstelling — dit ís de afdrukvolgorde. */
	list: SheetKind[];
	paper: Paper;
	/** Alleen relevant bij A4: elk vel 2× naast elkaar (snijstapel — na het
	 *  snijden twee gelijke stapels) i.p.v. de vellen op volgorde 2-up. */
	duplicate: boolean;
	ink: Ink;
	notesStyle: NotesStyle;
	binding: Binding;
	/** Spelerkleur P1: 6 hex-tekens zonder '#', lowercase; null = standaardtoken. */
	c1: string | null;
	/** Spelerkleur P2 — zelfde vorm als c1. */
	c2: string | null;
	/** Piltover-deck-id voor vooraf ingevulde deck-vellen (#344); null = leeg
	 *  vel. Alleen een veilige id-vorm ([A-Za-z0-9-], max 64) reist mee in de
	 *  URL — al het andere valt stil terug op null. */
	deckId: string | null;
}

/** Standaard-spelerkleuren; spiegelen de --paper-p1/--paper-p2-tokens in
 *  rb-web/src/app.css — wijzigt dáár iets, dan hier synchroon houden. */
export const DEFAULT_P1 = 'a3790a';
export const DEFAULT_P2 = 'c03a40';

/** Totaalplafond — houdt de preview en de printtaak hanteerbaar. Bewust de
 *  ENIGE grens: een apart per-run-plafond in de parser maakte de URL-rondgang
 *  lossy (UI stond 25 gelijke vellen toe, de parser klemde ze op 20 — review
 *  #343). Eén plafond dat parser én UI delen houdt serialize∘parse een
 *  identiteit. */
export const MAX_SHEETS = 40;

export const SHEET_ORDER: readonly SheetKind[] = [
	'match',
	'matchalt',
	'solo',
	'ffa',
	'duo',
	'tournament',
	'reflection',
	'milestone',
	'notes',
	'deck',
	'registration'
];

/** UI-metadata (Nederlands; de vellen zelf zijn Engelstalig, zoals al het
 *  officiële spelmateriaal). `pages` = fysieke pagina's per exemplaar.
 *  `shortLabel` is voor smalle contexten (het volgorde-paneel): het
 *  onderscheid zit VOORAAN, zodat afkappen nooit twee gelijk ogende labels
 *  oplevert ("Match sheet…" vs "Match sheet…"). */
export const SHEET_INFO: Record<
	SheetKind,
	{ label: string; shortLabel: string; hint: string; group: 'spel' | 'na' | 'deck'; pages: number }
> = {
	match: {
		label: 'Match sheet (Bo3)',
		shortLabel: 'Bo3 match',
		hint: 'Drie games met Conquer/Hold-punttracks, battlefields en first player',
		group: 'spel',
		pages: 1
	},
	matchalt: {
		label: 'Match sheet — variant B',
		shortLabel: 'Match var. B',
		hint: 'Getrapte game-blokken met kleurbanden, zonder battlefield/first-velden, groot notes-vlak',
		group: 'spel',
		pages: 1
	},
	solo: {
		label: 'Losse game',
		shortLabel: 'Losse game',
		hint: 'Eén game groot, met schrijfruimte per gescoord punt',
		group: 'spel',
		pages: 1
	},
	ffa: {
		label: 'Free-for-all (3–4 spelers)',
		shortLabel: 'FFA 3–4',
		hint: 'FFA3 Skirmish / FFA4 War — ieder een eigen track naar 8',
		group: 'spel',
		pages: 1
	},
	duo: {
		label: '2v2 Magma Chamber',
		shortLabel: '2v2',
		hint: 'Teamtracks naar 11 — per punt turven welke teammate scoorde (§489)',
		group: 'spel',
		pages: 1
	},
	tournament: {
		label: 'Toernooi-dag',
		shortLabel: 'Toernooi-dag',
		hint: 'Rondes, tegenstanders, resultaten en eindrecord op één vel',
		group: 'spel',
		pages: 1
	},
	reflection: {
		label: 'Match reflection',
		shortLabel: 'Reflection',
		hint: 'Verwachtingen vooraf, verloop en lessen achteraf',
		group: 'na',
		pages: 1
	},
	milestone: {
		label: 'Milestone review',
		shortLabel: 'Milestone',
		hint: 'Periodieke evaluatie: resultaten, deck, meta en mindset (2 pagina’s)',
		group: 'na',
		pages: 2
	},
	notes: {
		label: 'Notes',
		shortLabel: 'Notes',
		hint: 'Losse notitiepagina (dots of lijntjes)',
		group: 'na',
		pages: 1
	},
	deck: {
		label: 'Deckoverzicht',
		shortLabel: 'Deck',
		hint: 'Decklijst compact + matchup-notities — leeg of vooraf ingevuld',
		group: 'deck',
		pages: 1
	},
	registration: {
		label: 'Registration sheet',
		shortLabel: 'Registration',
		hint: 'Toernooi-decklijst (Piltover-opzet): legend, battlefields, main, runes, sideboard + judge-blok',
		group: 'deck',
		pages: 1
	}
};

export function defaultOptions(): ScorepadOptions {
	return {
		list: ['match'],
		paper: 'a5',
		duplicate: true,
		ink: 'color',
		notesStyle: 'dots',
		binding: 'none',
		c1: null,
		c2: null,
		deckId: null
	};
}

function clampCount(n: number): number {
	if (!Number.isFinite(n)) return 0;
	return Math.min(MAX_SHEETS, Math.max(0, Math.trunc(n)));
}

function isKind(v: string): v is SheetKind {
	return (SHEET_ORDER as readonly string[]).includes(v);
}

/** Tolerante parser: onbekende veltypen en rommelige aantallen vallen stil
 *  terug op iets bruikbaars — een gedeelde link mag nooit een kapotte pagina
 *  opleveren. `kind:n` is run-length ("match:2,reflection,match" = match,
 *  match, reflection, match), volgorde blijft behouden. */
export function parseOptions(params: URLSearchParams): ScorepadOptions {
	const o = defaultOptions();

	const sheets = params.get('sheets');
	if (sheets !== null) {
		const list: SheetKind[] = [];
		for (const part of sheets.split(',')) {
			const [kind, num] = part.split(':');
			if (!kind || !isKind(kind)) continue;
			const n = clampCount(num === undefined ? 1 : Number(num));
			for (let i = 0; i < n && list.length < MAX_SHEETS; i++) list.push(kind);
		}
		o.list = list;
	}

	if (params.get('paper') === 'a4') o.paper = 'a4';
	if (params.get('dup') === '0') o.duplicate = false;
	if (params.get('ink') === 'bw') o.ink = 'bw';
	if (params.get('notes') === 'lines') o.notesStyle = 'lines';
	const bind = params.get('bind');
	if (bind === 'top' || bind === 'side') o.binding = bind;
	// Spelerkleuren: alleen een exacte 6-hex-waarde telt; al het andere valt
	// stil terug op de standaardtokens (null) — nooit een kapotte kleur printen.
	const c1 = params.get('c1');
	if (c1 !== null && /^[0-9a-fA-F]{6}$/.test(c1)) o.c1 = c1.toLowerCase();
	const c2 = params.get('c2');
	if (c2 !== null && /^[0-9a-fA-F]{6}$/.test(c2)) o.c2 = c2.toLowerCase();
	// Deck-prefill (#344): alleen een veilige id-vorm telt; rommel valt stil
	// terug op null (leeg deck-vel), nooit een kapotte pagina.
	const deck = params.get('deck');
	if (deck !== null && /^[A-Za-z0-9-]{1,64}$/.test(deck)) o.deckId = deck;
	return o;
}

/** Compacte query-string; de standaardsituatie serialiseert naar '' zodat de
 *  kale URL schoon blijft. Opeenvolgende gelijke vellen worden run-length
 *  gecodeerd ("match:2,reflection"). */
export function serializeOptions(o: ScorepadOptions): string {
	const params = new URLSearchParams();
	const d = defaultOptions();

	const differs = o.list.length !== d.list.length || o.list.some((k, i) => k !== d.list[i]);
	if (differs) {
		const runs: string[] = [];
		let i = 0;
		while (i < o.list.length) {
			let j = i;
			while (j < o.list.length && o.list[j] === o.list[i]) j++;
			const n = j - i;
			runs.push(n === 1 ? o.list[i] : `${o.list[i]}:${n}`);
			i = j;
		}
		params.set('sheets', runs.join(','));
	}
	if (o.paper !== d.paper) params.set('paper', o.paper);
	if (!o.duplicate) params.set('dup', '0');
	if (o.ink !== d.ink) params.set('ink', o.ink);
	if (o.notesStyle !== d.notesStyle) params.set('notes', o.notesStyle);
	if (o.binding !== d.binding) params.set('bind', o.binding);
	if (o.c1 !== null) params.set('c1', o.c1);
	if (o.c2 !== null) params.set('c2', o.c2);
	if (o.deckId !== null) params.set('deck', o.deckId);
	return params.toString();
}

/** Alle fysieke pagina's in afdrukvolgorde (milestone → 2 pagina's). */
export function expandPages(o: ScorepadOptions): SheetPage[] {
	return o.list.flatMap<SheetPage>((k) => (k === 'milestone' ? [k, 'milestone2'] : [k]));
}

/** Printpagina's: A5 → één vel per pagina; A4 → twee A5's naast elkaar.
 *  In snijstapel-modus wordt élk vel verdubbeld ([p, p] per pagina), zodat er
 *  na het snijden twee identieke stapels liggen; op volgorde wordt gewoon per
 *  twee gebundeld en kan het laatste vak leeg blijven (null). */
export function pagePlan(o: ScorepadOptions): (SheetPage | null)[][] {
	const expanded = expandPages(o);
	if (o.paper === 'a5') return expanded.map((p) => [p]);
	if (o.duplicate) return expanded.map((p) => [p, p]);
	const pages: (SheetPage | null)[][] = [];
	for (let i = 0; i < expanded.length; i += 2) {
		pages.push([expanded[i], expanded[i + 1] ?? null]);
	}
	return pages;
}

/** Aantal losse A5-vellen dat een plan ná eventueel snijden oplevert. */
export function sheetTotal(o: ScorepadOptions): number {
	const n = expandPages(o).length;
	return o.paper === 'a4' && o.duplicate ? n * 2 : n;
}
