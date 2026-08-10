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

/** Eén gekozen vel in de samenstelling (#346: meerdere decks per boekje).
 *  `deckRef` wijst 1-based in ScorepadOptions.decks naar de deck-bron van dit
 *  vel en is alleen betekenisvol op 'deck'/'registration' — elders altijd
 *  null (een match-vel hééft geen deck-bron). */
export interface SheetEntry {
	kind: SheetKind;
	deckRef: number | null;
}

/** Eén fysieke pagina. Milestone review beslaat twee pagina's; de tweede
 *  bestaat alleen in het paginaplan, niet als los te kiezen veltype. */
export type SheetPage = SheetKind | 'milestone2';

/** Eén fysieke pagina mét zijn deck-bron — het paginaplan draagt de deckRef
 *  mee zodat de UI per vel de juiste prefill kan pakken. */
export interface SheetPageEntry {
	page: SheetPage;
	deckRef: number | null;
}

export type Paper = 'a5' | 'a4';
export type Ink = 'color' | 'bw';
export type NotesStyle = 'dots' | 'lines';
/** Ringband-marge: extra witruimte aan de bind-rand (perforatie/spiraal). */
export type Binding = 'none' | 'top' | 'side';

export interface ScorepadOptions {
	/** Geordende samenstelling — dit ís de afdrukvolgorde. */
	list: SheetEntry[];
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
	/** Piltover-deck-ids voor vooraf ingevulde deck-vellen (#344/#346); vellen
	 *  wijzen er met SheetEntry.deckRef (1-based) naartoe. Alleen een veilige
	 *  id-vorm ([A-Za-z0-9-], max 64) reist mee in de URL — al het andere valt
	 *  stil weg. */
	decks: string[];
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
		list: [{ kind: 'match', deckRef: null }],
		paper: 'a5',
		duplicate: true,
		ink: 'color',
		notesStyle: 'dots',
		binding: 'none',
		c1: null,
		c2: null,
		decks: []
	};
}

function clampCount(n: number): number {
	if (!Number.isFinite(n)) return 0;
	return Math.min(MAX_SHEETS, Math.max(0, Math.trunc(n)));
}

function isKind(v: string): v is SheetKind {
	return (SHEET_ORDER as readonly string[]).includes(v);
}

/** Veilige deck-id-vorm voor de URL — zelfde eis als het oude deck=-param. */
const DECK_ID = /^[A-Za-z0-9-]{1,64}$/;

/** Deck-id waar een deckRef (1-based) naar wijst; null bij geen of verweesde
 *  ref — het vel valt dan terug op leeg, nooit een kapotte pagina. */
export function deckIdAt(o: ScorepadOptions, deckRef: number | null): string | null {
	return deckRef === null ? null : (o.decks[deckRef - 1] ?? null);
}

/** Tolerante parser: onbekende veltypen en rommelige aantallen vallen stil
 *  terug op iets bruikbaars — een gedeelde link mag nooit een kapotte pagina
 *  opleveren. Grammatica per run: `kind[@ref][:n]` — run-length
 *  ("match:2,reflection,match" = match, match, reflection, match; volgorde
 *  blijft behouden), met `@ref` als 1-based verwijzing naar 'decks='
 *  ("deck@1:2,registration@1,match,deck@2&decks=<id1>,<id2>"). Een @-ref
 *  zonder bijbehorende decks-entry valt stil terug op null (leeg vel). */
export function parseOptions(params: URLSearchParams): ScorepadOptions {
	const o = defaultOptions();

	// Deck-bronnen éérst: de sheets-refs verwijzen ernaar. Nieuwe vorm is
	// 'decks=<id1>,<id2>'; het oude 'deck=<id>' (#344) blijft geldig als alias
	// voor decks=<id> + ref 1 op alle deck-vellen, zodat gedeelde links van
	// vóór #346 blijven werken. Ongeldige ids vallen stil weg; refMap onthoudt
	// per RUWE positie waar een id terechtkwam, zodat '@2' na een weggevallen
	// eerste id bij zíjn id blijft horen i.p.v. naar de verkeerde te schuiven.
	const decksParam = params.get('decks');
	const refMap: (number | null)[] = [];
	let aliasRef = false;
	if (decksParam !== null) {
		for (const raw of decksParam.split(',')) {
			if (!DECK_ID.test(raw)) {
				refMap.push(null);
				continue;
			}
			// Dedup mét ref-behoud: een dubbele id verwijst naar zijn eerste
			// exemplaar. En hetzelfde plafond als de vellen (review #346):
			// meer bronnen dan vellen kan per constructie nooit nuttig zijn,
			// en onbegrensd betekende honderden parallelle rb-api-fetches per
			// gecrafte URL — óók server-side bij SSR.
			const dup = o.decks.indexOf(raw);
			if (dup >= 0) {
				refMap.push(dup + 1);
				continue;
			}
			if (o.decks.length >= MAX_SHEETS) {
				refMap.push(null);
				continue;
			}
			o.decks.push(raw);
			refMap.push(o.decks.length);
		}
	} else {
		const deck = params.get('deck');
		if (deck !== null && DECK_ID.test(deck)) {
			o.decks = [deck];
			aliasRef = true;
		}
	}

	const sheets = params.get('sheets');
	if (sheets !== null) {
		const list: SheetEntry[] = [];
		for (const part of sheets.split(',')) {
			const [head, num] = part.split(':');
			const [kind, refRaw] = head.split('@');
			if (!kind || !isKind(kind)) continue;
			const n = clampCount(num === undefined ? 1 : Number(num));
			// deckRef alleen op deck-vellen; elders is '@' betekenisloos en
			// wordt hij genegeerd. In alias-modus krijgt élk deck-vel ref 1 —
			// dat wás het gedrag van het oude ene deck=-param.
			let deckRef: number | null = null;
			if (kind === 'deck' || kind === 'registration') {
				if (aliasRef) {
					deckRef = 1;
				} else if (refRaw !== undefined) {
					const r = Number(refRaw);
					if (Number.isInteger(r) && r >= 1 && r <= refMap.length) deckRef = refMap[r - 1];
				}
			}
			for (let i = 0; i < n && list.length < MAX_SHEETS; i++) list.push({ kind, deckRef });
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
	return o;
}

/** Compacte query-string; de standaardsituatie serialiseert naar '' zodat de
 *  kale URL schoon blijft. Run-length groepeert alleen over gelijke
 *  (kind, deckRef)-paren ("deck@1:2,deck@2"); 'decks=' wordt alleen
 *  geschreven als er deck-ids zijn. Er wordt altijd naar de nieuwe vorm
 *  geserialiseerd — het oude 'deck='-param is alleen een parse-alias. */
export function serializeOptions(o: ScorepadOptions): string {
	const params = new URLSearchParams();
	const d = defaultOptions();

	const differs =
		o.list.length !== d.list.length ||
		o.list.some((e, i) => e.kind !== d.list[i].kind || e.deckRef !== d.list[i].deckRef);
	if (differs) {
		const runs: string[] = [];
		let i = 0;
		while (i < o.list.length) {
			let j = i;
			while (
				j < o.list.length &&
				o.list[j].kind === o.list[i].kind &&
				o.list[j].deckRef === o.list[i].deckRef
			) {
				j++;
			}
			const n = j - i;
			const head =
				o.list[i].deckRef === null ? o.list[i].kind : `${o.list[i].kind}@${o.list[i].deckRef}`;
			runs.push(n === 1 ? head : `${head}:${n}`);
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
	if (o.decks.length > 0) params.set('decks', o.decks.join(','));
	return params.toString();
}

/** Alle fysieke pagina's in afdrukvolgorde (milestone → 2 pagina's), elk mét
 *  hun deck-bron. */
export function expandPages(o: ScorepadOptions): SheetPageEntry[] {
	return o.list.flatMap<SheetPageEntry>((e) =>
		e.kind === 'milestone'
			? [
					{ page: 'milestone', deckRef: null },
					{ page: 'milestone2', deckRef: null }
				]
			: [{ page: e.kind, deckRef: e.deckRef }]
	);
}

/** Printpagina's: A5 → één vel per pagina; A4 → twee A5's naast elkaar.
 *  In snijstapel-modus wordt élk vel verdubbeld ([p, p] per pagina), zodat er
 *  na het snijden twee identieke stapels liggen; op volgorde wordt gewoon per
 *  twee gebundeld en kan het laatste vak leeg blijven (null). */
export function pagePlan(o: ScorepadOptions): (SheetPageEntry | null)[][] {
	const expanded = expandPages(o);
	if (o.paper === 'a5') return expanded.map((p) => [p]);
	if (o.duplicate) return expanded.map((p) => [p, p]);
	const pages: (SheetPageEntry | null)[][] = [];
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
