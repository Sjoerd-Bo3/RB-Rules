<script lang="ts">
	import { browser } from '$app/environment';
	import { afterNavigate, replaceState } from '$app/navigation';
	import type { PageProps } from './$types';
	import { SvelteSet } from 'svelte/reactivity';
	import {
		DEFAULT_P1,
		DEFAULT_P2,
		MAX_SHEETS,
		SHEET_INFO,
		SHEET_ORDER,
		deckIdAt,
		defaultOptions,
		pagePlan,
		serializeOptions,
		sheetTotal,
		type ScorepadOptions,
		type SheetKind,
		type SheetPageEntry
	} from '$lib/scorepad';
	import {
		fromDeckSections,
		parseDeckText,
		piltoverDeckIdFromInput,
		type DeckPrefill,
		type DeckSectionInput
	} from '$lib/deckPrefill';
	import MatchSheet from './MatchSheet.svelte';
	import MatchAltSheet from './MatchAltSheet.svelte';
	import SoloSheet from './SoloSheet.svelte';
	import FfaSheet from './FfaSheet.svelte';
	import DuoSheet from './DuoSheet.svelte';
	import TournamentSheet from './TournamentSheet.svelte';
	import ReflectionSheet from './ReflectionSheet.svelte';
	import MilestoneSheet from './MilestoneSheet.svelte';
	import NotesSheet from './NotesSheet.svelte';
	import DeckSheet from './DeckSheet.svelte';
	import RegistrationSheet from './RegistrationSheet.svelte';
	import type { RegistrationPerson } from './RegistrationSheet.svelte';

	let { data }: PageProps = $props();

	// Kopie van de load-opties: wij muteren lokaal en spiegelen naar de URL —
	// het load-resultaat zelf blijft onaangeroerd. Hier bewust alleen de
	// beginwaarde; bij een échte navigatie zet afterNavigate (hieronder) de
	// vers geparste load-opties opnieuw — anders schreef de URL-spiegel de
	// oude staat over een aangeklikte /scorepad?-link heen (review #343).
	// svelte-ignore state_referenced_locally
	let opts = $state(structuredClone(data.options));

	const plan = $derived(pagePlan(opts));
	const total = $derived(sheetTotal(opts));
	const bw = $derived(opts.ink === 'bw');
	const counts = $derived(
		Object.fromEntries(
			SHEET_ORDER.map((k) => [k, opts.list.filter((x) => x.kind === k).length])
		) as Record<SheetKind, number>
	);
	const hasNotes = $derived(opts.list.some((e) => e.kind === 'notes'));
	const hasDeckSheet = $derived(
		opts.list.some((e) => e.kind === 'deck' || e.kind === 'registration')
	);
	const hasRegistration = $derived(opts.list.some((e) => e.kind === 'registration'));

	// ── Deck-bronnen (#344/#346): een LIJST bronnen voor de deck-/registration-
	// vellen; elk vel wijst met SheetEntry.deckRef naar zijn bron.
	// INGEST-bronnen (positieve refs, 1-based in opts.decks) reizen mee in de
	// URL — deelbaar en headless printbaar; hun opgehaalde lijsten staan hier in
	// een cache op id. LOKALE bronnen (geplakte deck-code of decklijst-tekst)
	// zijn bewust alléén component-state en krijgen NEGATIEVE refs
	// (-1 = eerste lokale bron): een code is geen stabiele referentie, dus die
	// refs horen niet in de URL — urlOptions() hieronder serialiseert ze als
	// null, zodat zo'n vel in een gedeelde link gewoon blanco is.
	interface IngestState {
		deck: DeckPrefill | null;
		/** Weergave-melding; null = geladen of nog bezig. */
		error: string | null;
		loading: boolean;
	}
	interface LocalSource {
		origin: 'code' | 'tekst';
		prefill: DeckPrefill;
		note: string | null;
	}
	const LOAD_ERROR_TEXT = {
		notfound: 'Dit deck staat niet (meer) in de ingest — het vel blijft blanco.',
		unavailable: 'Deck laden lukt nu niet — de bron is even niet bereikbaar; het vel blijft blanco.'
	} as const;
	function ingestFromData(rows: typeof data.decks): Record<string, IngestState> {
		return Object.fromEntries(
			rows.map((r) => [
				r.id,
				{ deck: r.deck, error: r.error === null ? null : LOAD_ERROR_TEXT[r.error], loading: false }
			])
		);
	}
	// Cache op deck-id; bij verwijderen van een bron blijft de cache-entry
	// bewust staan, zodat "Herstel" na een Reset de prefill direct terug heeft.
	// svelte-ignore state_referenced_locally
	let ingestState = $state(ingestFromData(data.decks));
	let localSources = $state<LocalSource[]>([]);
	// De laatst toegevoegde bron — nieuwe deck-vellen krijgen die als standaard.
	let lastRef = $state<number | null>(null);

	/** Prefill waar een deckRef naar wijst; null = blanco vel (geen ref,
	 *  verweesde ref, of bron nog niet geladen/gefaald). */
	function prefillFor(ref: number | null): DeckPrefill | null {
		if (ref === null) return null;
		if (ref < 0) return localSources[-ref - 1]?.prefill ?? null;
		const id = deckIdAt(opts, ref);
		return id === null ? null : (ingestState[id]?.deck ?? null);
	}

	/** Opties zoals ze de URL in mogen: lokale (negatieve) refs worden null —
	 *  zie het blok-commentaar hierboven. Ook de undo-vergelijkingen gebruiken
	 *  deze vorm, zodat 'deck@-1' nooit ergens serialiseert. */
	function urlOptions(o: ScorepadOptions): ScorepadOptions {
		return {
			...o,
			list: o.list.map((e) => (e.deckRef !== null && e.deckRef < 0 ? { ...e, deckRef: null } : e))
		};
	}

	// Bronnenlijst voor het paneel en de bron-selects: ingest-bronnen eerst
	// (volgorde = opts.decks), dan de lokale; `num` is het weergavenummer.
	const sourceRows = $derived.by(() => {
		const rows: {
			ref: number;
			name: string;
			origin: string;
			loading: boolean;
			error: string | null;
			note: string | null;
		}[] = [];
		opts.decks.forEach((id, i) => {
			const st = ingestState[id];
			rows.push({
				ref: i + 1,
				name: st?.deck?.name ?? id,
				origin: 'ingest — reist mee in de URL',
				loading: st?.loading ?? false,
				error: st?.error ?? null,
				note: null
			});
		});
		localSources.forEach((s, i) => {
			rows.push({
				ref: -(i + 1),
				name: s.prefill.name ?? (s.origin === 'code' ? 'Geplakte deck-code' : 'Geplakte decklijst'),
				origin: `geplakte ${s.origin === 'code' ? 'deck-code' : 'decklijst'} — alleen dit apparaat`,
				loading: false,
				error: null,
				note: s.note
			});
		});
		return rows.map((r, i) => ({ ...r, num: i + 1 }));
	});

	/** Nieuwe bron aangenomen: bestaande blanco deck-/registration-vellen
	 *  nemen hem over (het vertrouwde één-bron-gedrag: link plakken vulde de
	 *  al gekozen vellen) — een vel dat al expliciet naar een andere bron
	 *  wijst blijft ongemoeid. Nieuwe vellen volgen via lastRef. */
	function adoptRef(ref: number) {
		lastRef = ref;
		for (const e of opts.list) {
			if ((e.kind === 'deck' || e.kind === 'registration') && e.deckRef === null) e.deckRef = ref;
		}
	}

	/** Bron weg: vellen die ernaar wezen worden blanco; hogere refs met
	 *  hetzelfde teken schuiven één op zodat ze bij hún bron blijven horen. */
	function remapAfterRemove(removed: number) {
		const shift = (r: number | null): number | null => {
			if (r === null) return null;
			if (r === removed) return null;
			if (removed > 0 && r > removed) return r - 1;
			if (removed < 0 && r < removed) return r + 1;
			return r;
		};
		for (const e of opts.list) e.deckRef = shift(e.deckRef);
		lastRef = shift(lastRef);
	}
	function removeSource(ref: number) {
		if (ref > 0) opts.decks.splice(ref - 1, 1);
		else localSources.splice(-ref - 1, 1);
		remapAfterRemove(ref);
	}

	// Personalia voor het registration sheet — bewust ALLEEN lokale $state,
	// nooit in de URL en nergens gepersisteerd: persoonsgegevens horen niet in
	// query-strings (gedeelde links, serverlogs, browsergeschiedenis). Weg bij
	// herladen — dat is de privacy-keuze; het vel is dan gewoon weer blanco.
	let person = $state<RegistrationPerson>({
		firstName: '',
		lastName: '',
		riotId: '',
		event: '',
		location: '',
		date: '',
		designer: ''
	});

	// Zoeken in de deck-ingest: debounce ~300ms, en een volgnummer zodat een
	// traag antwoord op een oude query nooit een nieuwere overschrijft.
	let deckQuery = $state('');
	let deckResults = $state<{ id: string; name: string | null; cardCount: number }[] | null>(null);
	let deckSearchError = $state<string | null>(null);
	let searchTimer: ReturnType<typeof setTimeout> | undefined;
	let searchSeq = 0;
	function onDeckQuery(v: string) {
		deckQuery = v;
		clearTimeout(searchTimer);
		searchSeq++;
		const q = v.trim();
		if (!q) {
			deckResults = null;
			deckSearchError = null;
			return;
		}
		searchTimer = setTimeout(() => void searchDecks(q, searchSeq), 300);
	}
	async function searchDecks(q: string, seq: number) {
		try {
			const res = await fetch(`/scorepad/decks?q=${encodeURIComponent(q)}`);
			const body = (await res.json()) as {
				items?: { id: string; name: string | null; cardCount: number }[];
				error?: string;
			};
			if (seq !== searchSeq) return; // verouderd antwoord — negeren
			if (!res.ok || !body.items) {
				deckResults = null;
				deckSearchError = body.error ?? 'Decks zoeken lukt nu niet.';
				return;
			}
			deckResults = body.items;
			deckSearchError = null;
		} catch {
			if (seq !== searchSeq) return;
			deckResults = null;
			deckSearchError = 'Decks zoeken lukt nu niet — rb-api is niet bereikbaar.';
		}
	}

	/** Ingest-bron toevoegen: id in opts.decks (→ URL) en de lijst ophalen via
	 *  de proxy. Kent de ingest de id niet (404), dan probeert POST
	 *  /scorepad/fetch het deck éénmalig van Piltover Archive te halen (#346) —
	 *  pas als óók dat faalt blijft het vel blanco met een nette melding; de
	 *  pagina werkt altijd door. */
	async function addIngestSource(id: string) {
		const existing = opts.decks.indexOf(id);
		if (existing >= 0) {
			adoptRef(existing + 1);
			// Retry-pad (review #346): de foutmelding belooft "probeer straks" —
			// dezelfde link opnieuw koppelen moet dan écht opnieuw laden in
			// plaats van stil niets doen. Alleen bij een eerdere mislukking;
			// een geladen of nog ladende bron blijft met rust.
			const st = ingestState[id];
			if (!st || (st.deck === null && !st.loading)) await loadIngestDeck(id);
			return;
		}
		opts.decks.push(id);
		adoptRef(opts.decks.length);
		await loadIngestDeck(id);
	}

	/** Eén laadpoging voor een ingest-bron — gedeeld door toevoegen en retry. */
	async function loadIngestDeck(id: string) {
		ingestState[id] = { deck: null, error: null, loading: true };
		try {
			let res = await fetch(`/scorepad/decks/${id}`);
			if (res.status === 404) {
				// Auto-fetch: nog niet in de ingest — één gerichte Piltover-fetch.
				res = await fetch('/scorepad/fetch', {
					method: 'POST',
					headers: { 'content-type': 'application/json' },
					body: JSON.stringify({ id })
				});
			}
			// Intussen verwijderd? Dan is dit antwoord van niemand.
			if (!opts.decks.includes(id)) return;
			if (!res.ok) {
				ingestState[id] = {
					deck: null,
					error:
						res.status === 404
							? 'Deck niet gevonden op Piltover Archive.'
							: res.status === 400
								? (((await res.json().catch(() => null)) as { error?: string } | null)?.error ??
									'Geen geldige Piltover Archive deck-id.')
								: 'Ophalen kan nu niet — probeer straks, of plak de decklijst als tekst.',
					loading: false
				};
				return;
			}
			const d = (await res.json()) as { name: string | null; sections: DeckSectionInput[] };
			if (!opts.decks.includes(id)) return;
			ingestState[id] = { deck: fromDeckSections(d.name, d.sections), error: null, loading: false };
		} catch {
			if (!opts.decks.includes(id)) return;
			ingestState[id] = {
				deck: null,
				error: 'Ophalen kan nu niet — probeer straks, of plak de decklijst als tekst.',
				loading: false
			};
		}
	}
	function chooseDeck(id: string) {
		deckQuery = '';
		deckResults = null;
		deckSearchError = null;
		void addIngestSource(id);
	}

	// Piltover-link of kale deck-id plakken. De bezig-status dekt ook de
	// auto-fetch: een Piltover-rondgang kan even duren.
	let linkInput = $state('');
	let linkError = $state<string | null>(null);
	let linkBusy = $state(false);
	async function linkFromInput() {
		const id = piltoverDeckIdFromInput(linkInput);
		if (id === null) {
			linkError = 'Geen Piltover-link of deck-id herkend — plak de volledige /decks/view/…-link.';
			return;
		}
		linkError = null;
		linkInput = '';
		linkBusy = true;
		try {
			await addIngestSource(id);
		} finally {
			linkBusy = false;
		}
	}

	// Deck-code plakken → decode via de proxy → LOKALE bron (niet in de URL).
	// De verrijkte decode (#346) levert type/supertype mee, zodat
	// fromDeckSections runes/legend/battlefields uit het maindeck licht en
	// chosen-champion de CC-badge voedt.
	let codeInput = $state('');
	let codeError = $state<string | null>(null);
	let codeBusy = $state(false);
	async function decodeCode() {
		const code = codeInput.trim();
		if (!code) {
			codeError = 'Plak eerst een deck-code.';
			return;
		}
		codeBusy = true;
		try {
			const res = await fetch('/scorepad/decode', {
				method: 'POST',
				headers: { 'content-type': 'application/json' },
				body: JSON.stringify({ code })
			});
			const body = (await res.json()) as {
				sections?: DeckSectionInput[];
				unknownCount?: number;
				error?: string;
			};
			if (!res.ok || !body.sections) {
				codeError = body.error ?? 'Deck-code lezen mislukt.';
				return;
			}
			codeError = null;
			codeInput = '';
			const unknown = body.unknownCount ?? 0;
			localSources.push({
				origin: 'code',
				prefill: fromDeckSections(null, body.sections),
				note:
					unknown > 0
						? `${unknown} regel${unknown === 1 ? '' : 's'} niet aan een kaart gekoppeld — die staan als code op het vel.`
						: null
			});
			adoptRef(-localSources.length);
		} catch {
			codeError = 'Deck-code lezen lukt nu niet.';
		} finally {
			codeBusy = false;
		}
	}

	// Decklijst-tekst plakken → lokale parser → LOKALE bron (niet in de URL).
	let textInput = $state('');
	let textError = $state<string | null>(null);
	function useDeckText() {
		const parsed = parseDeckText(textInput);
		if (parsed.sections.length === 0) {
			textError = 'Geen kaartregels herkend — één kaart per regel, bv. "3x Vi".';
			return;
		}
		textError = null;
		textInput = '';
		localSources.push({ origin: 'tekst', prefill: parsed, note: null });
		adoptRef(-localSources.length);
	}

	// Gekozen spelerkleuren als CSS-vars op de .ppage: overschrijven de
	// --paper-p1/--paper-p2-tokens plus hun -soft-tint (dezelfde lichte meng
	// als de vaste tokens in app.css). Null = standaardtokens, dus geen
	// override. De waarden zijn altijd gevalideerde 6-hex (parser + picker),
	// dus veilig in een style-attribuut.
	// De -ink-variant (tekstkleur op wit) reist mee als donkerder afgeleide
	// van de gekozen kleur — anders hielden de trackkoppen bij een custom
	// P1-kleur de standaard donker-goud-inkt.
	const colorStyle = $derived(
		(opts.c1
			? ` --paper-p1: #${opts.c1}; --paper-p1-soft: color-mix(in srgb, #${opts.c1} 16%, #ffffff); --paper-p1-ink: color-mix(in srgb, #${opts.c1} 78%, #000);`
			: '') +
			(opts.c2
				? ` --paper-p2: #${opts.c2}; --paper-p2-soft: color-mix(in srgb, #${opts.c2} 16%, #ffffff);`
				: '')
	);

	// Kleurkeuze uit de native picker: '#' eraf, lowercase; de standaardkleur
	// slaan we als null op zodat de URL schoon blijft.
	function setPlayerColor(which: 'c1' | 'c2', value: string) {
		const hex = value.replace('#', '').toLowerCase();
		opts[which] = hex === (which === 'c1' ? DEFAULT_P1 : DEFAULT_P2) ? null : hex;
	}

	// Opties → URL (replaceState: geen navigatie, geen history-vervuiling).
	// replaceState mag pas ná router-init; onMount is daarvoor nog te vroeg
	// (hydration), dus afterNavigate — die vuurt pas als de initiële navigatie
	// rond is. Een gooiende effect zou bovendien de hele reactiviteit van de
	// pagina slopen. `qs` wordt vóór de guard berekend zodat de
	// afhankelijkheden ook op de eerste run geregistreerd staan.
	let routerReady = $state(false);
	afterNavigate(() => {
		routerReady = true;
		// Elke echte navigatie (in-app link naar /scorepad?…, of de kale
		// navlink terwijl je al hier staat) parseert de URL opnieuw in de
		// load — dát resultaat is dan de waarheid. Selectie en lightbox
		// verwijzen naar de oude lijst en gaan mee dicht. (afterNavigate
		// vuurt niet op onze eigen replaceState, dus dit lust niet.)
		opts = structuredClone(data.options);
		// De ingest-bronnen horen bij de URL en volgen de navigatie mee; de
		// geplakte bronnen en personalia staan bewust NIET in de URL en blijven
		// dus gewoon staan — de navigatie heeft daar niets over gezegd. lastRef
		// wel wissen: die hoorde bij de samenstelling van vóór de navigatie.
		ingestState = ingestFromData(data.decks);
		lastRef = null;
		sel.clear();
		zoom = null;
		// Een navigatie vervangt de samenstelling wisselend van buitenaf; een
		// herstelregel van vóór die navigatie zou dan iets anders terugzetten
		// dan de bezoeker net zag wegvallen.
		undo = null;
	});
	$effect(() => {
		// urlOptions: lokale (negatieve) refs serialiseren als null — zie het
		// deck-bronnen-blok bovenaan.
		const qs = serializeOptions(urlOptions(opts));
		if (!browser || !routerReady) return;
		replaceState(qs ? `?${qs}` : '/scorepad', {});
	});

	// Selectie in het volgorde-paneel (multiselect): indexposities. Elke
	// structurele mutatie maakt de indexen stale — dus wissen, behalve bij een
	// drop, die selecteert het verplaatste blok op zijn nieuwe plek terug.
	const sel = new SvelteSet<number>();

	function add(k: SheetKind) {
		// Nieuwe deck-vellen krijgen standaard de laatst toegevoegde bron;
		// andere veltypen hébben geen bron (deckRef null, zie SheetEntry).
		const deckRef = k === 'deck' || k === 'registration' ? lastRef : null;
		if (opts.list.length < MAX_SHEETS) opts.list.push({ kind: k, deckRef });
		sel.clear();
	}
	function removeLast(k: SheetKind) {
		const i = opts.list.findLastIndex((e) => e.kind === k);
		if (i >= 0) opts.list.splice(i, 1);
		sel.clear();
	}
	function move(i: number, delta: -1 | 1) {
		const j = i + delta;
		if (j < 0 || j >= opts.list.length) return;
		const [item] = opts.list.splice(i, 1);
		opts.list.splice(j, 0, item);
		sel.clear();
	}
	function removeAt(i: number) {
		opts.list.splice(i, 1);
		sel.clear();
	}
	function toggleSel(i: number) {
		if (sel.has(i)) sel.delete(i);
		else sel.add(i);
	}
	function removeSelected() {
		for (const i of [...sel].sort((a, b) => b - a)) opts.list.splice(i, 1);
		sel.clear();
	}
	// Grenzen van de selectie, voor de Omhoog/Omlaag-knoppen op de selectiebalk.
	const selMin = $derived(sel.size > 0 ? Math.min(...sel) : -1);
	const selMax = $derived(sel.size > 0 ? Math.max(...sel) : -1);
	// Omhoog/Omlaag verplaatst de hele selectie één positie, met hetzelfde
	// gedrag als een drop: een niet-aaneengesloten selectie schuift als ÉÉN
	// compact blok naar de doelplek — precies wat slepen ook doet. Per item
	// één stap zou een tweede, afwijkend verplaatsmodel introduceren (en bij
	// botsende items alsnog compacteren). "Eén positie" = de blokrand
	// verschuift één plek: omhoog t.o.v. het bovenste item, omlaag t.o.v. het
	// onderste.
	function moveSelection(delta: -1 | 1) {
		const src = [...sel].sort((a, b) => a - b);
		if (src.length === 0) return;
		if (delta === -1 && src[0] === 0) return;
		if (delta === 1 && src[src.length - 1] === opts.list.length - 1) return;
		moveBlock(src, delta === -1 ? src[0] - 1 : src[src.length - 1] + 2);
	}

	// ── Reset met herstel (geen confirm()-dialoog): de vorige opties blijven
	// als snapshot staan tot er hersteld wordt of een nieuwe mutatie komt. ──
	let undo = $state<ScorepadOptions | null>(null);
	function resetAll() {
		// Alleen een snapshot als er iets te verliezen valt; nogmaals Reset op
		// een al-gereset pad mag de eerdere snapshot niet overschrijven met de
		// standaardopties.
		if (serializeOptions(urlOptions(opts)) !== '') undo = $state.snapshot(opts) as ScorepadOptions;
		// Reset vervangt de hele lijst — de selectie-indexen wijzen anders naar
		// een lijst die niet meer bestaat (phantom-selectie, review #343). Ook
		// lastRef gaat mee: een standaardbron die naar een gewiste ingest-bron
		// wijst zou nieuwe deck-vellen een verweesde ref geven.
		opts = defaultOptions();
		lastRef = null;
		sel.clear();
	}
	function restoreReset() {
		if (undo === null) return;
		opts = undo;
		undo = null;
		sel.clear();
	}
	// Elke mutatie ná de reset maakt de herstelregel achterhaald: zodra de
	// serialisatie afwijkt van de net-gereset standaard (die naar '' schrijft),
	// verdwijnt hij. Dit dekt álle mutatiepaden (chips, pickers, DnD) zonder
	// elke handler afzonderlijk te hoeven raken.
	$effect(() => {
		if (undo !== null && serializeOptions(urlOptions(opts)) !== '') undo = null;
	});

	// Drag & drop: een niet-geselecteerde rij verslepen pakt alleen die rij;
	// een geselecteerde rij verslepen neemt de hele selectie mee. De pijltjes
	// blijven bestaan als toetsenbord-pad (native DnD is muis/trackpad-only).
	let dragIdxs: number[] | null = null;
	let insertAt = $state<number | null>(null);

	function onDragStart(e: DragEvent, i: number) {
		if (!sel.has(i)) {
			sel.clear();
			sel.add(i);
		}
		dragIdxs = [...sel].sort((a, b) => a - b);
		// Firefox start zonder data geen drag.
		e.dataTransfer?.setData('text/plain', String(i));
		if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
	}
	function onDragOver(e: DragEvent, i: number) {
		if (dragIdxs === null) return;
		e.preventDefault();
		const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
		insertAt =
			i >= opts.list.length
				? opts.list.length
				: e.clientY < r.top + r.height / 2
					? i
					: Math.min(i + 1, opts.list.length);
		if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
	}
	// Verplaatst de entries op `src` (oplopend gesorteerd) als aaneengesloten
	// blok naar `at` (invoegindex in de ÓÓRSPRONKELIJKE lijst) en herselecteert
	// het blok op zijn nieuwe plek — gedeeld door drop én de knoppen op de
	// selectiebalk, zodat beide paden exact hetzelfde doen.
	function moveBlock(src: number[], at: number) {
		const items = src.map((i) => opts.list[i]);
		let target = at;
		for (let x = src.length - 1; x >= 0; x--) {
			opts.list.splice(src[x], 1);
			if (src[x] < target) target--;
		}
		opts.list.splice(target, 0, ...items);
		sel.clear();
		for (let x = 0; x < items.length; x++) sel.add(target + x);
	}
	function onDrop(e: DragEvent) {
		e.preventDefault();
		if (dragIdxs === null || insertAt === null) return resetDrag();
		moveBlock(dragIdxs, insertAt);
		resetDrag();
	}
	function resetDrag() {
		dragIdxs = null;
		insertAt = null;
	}

	// Afgeleid uit SHEET_INFO: een nieuw veltype met een bestaande groep
	// verschijnt vanzelf in de juiste kolom, een nieuwe groep dwingt hier een
	// titel af via het Record-type.
	const GROUP_TITLES: Record<(typeof SHEET_INFO)[SheetKind]['group'], string> = {
		spel: 'Tijdens het spel',
		na: 'Na het spel',
		deck: 'Deck'
	};
	const GROUPS: { title: string; kinds: SheetKind[] }[] = (
		Object.keys(GROUP_TITLES) as (keyof typeof GROUP_TITLES)[]
	).map((g) => ({
		title: GROUP_TITLES[g],
		kinds: SHEET_ORDER.filter((k) => SHEET_INFO[k].group === g)
	}));

	// Preview-weergave: 'grid' zet de pagina's als miniaturen naast elkaar
	// (overzicht), 'full' toont ze groot onder elkaar. Puur een kijkstand —
	// bewust niet in de URL en zonder invloed op het printresultaat.
	let view = $state<'grid' | 'full'>('grid');

	// Schaal van de preview: past de pagina in de beschikbare breedte. 1mm =
	// 96/25.4 px (CSS-definitie), dus A5 = 559px en A4-liggend = 1123px breed.
	// In gridstand bepaalt een richtbreedte per cel het kolomaantal; de cellen
	// verdelen daarna de volle breedte.
	const MM = 96 / 25.4;
	const GAP = 14;
	let pvw = $state(0);
	const pageWmm = $derived(opts.paper === 'a4' ? 297 : 148);
	const targetCell = $derived(opts.paper === 'a4' ? 470 : 300);
	const cols = $derived(
		view === 'grid' ? Math.max(1, Math.floor((pvw + GAP) / (targetCell + GAP))) : 1
	);
	const cellW = $derived(cols > 1 ? (pvw - (cols - 1) * GAP) / cols : pvw);
	const scale = $derived(pvw > 0 ? Math.min(1, cellW / (pageWmm * MM)) : 1);
	const slotH = $derived(Math.ceil(210 * MM * scale));

	function pageLabel(p: SheetPageEntry | null): string {
		if (p === null) return 'leeg';
		if (p.page === 'milestone2') return `${SHEET_INFO.milestone.label} — 2/2`;
		return SHEET_INFO[p.page].label;
	}

	// ── Slepen in de preview ──
	// Per printpagina de bijbehorende lijst-index (milestone2 hoort bij
	// dezelfde entry als zijn eerste pagina — een paar verhuist als geheel).
	// In A4-op-volgorde bundelt één pagina twee verschillende entries; slepen
	// zou daar dubbelzinnig zijn, dus daar staat het uit.
	const pageEntry = $derived.by(() => {
		const map: number[] = [];
		opts.list.forEach((e, li) => {
			map.push(li);
			if (e.kind === 'milestone') map.push(li);
		});
		return map;
	});
	const pvDraggable = $derived(view === 'grid' && (opts.paper === 'a5' || opts.duplicate));

	let pvDrag: number | null = null;
	let pvMark = $state<{ page: number; side: 'l' | 'r' } | null>(null);

	function pvDragStart(e: DragEvent, page: number) {
		pvDrag = pageEntry[page];
		e.dataTransfer?.setData('text/plain', String(page));
		if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move';
	}
	function pvDragOver(e: DragEvent, page: number) {
		if (pvDrag === null) return;
		e.preventDefault();
		const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
		pvMark = { page, side: e.clientX < r.left + r.width / 2 ? 'l' : 'r' };
		if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
	}
	function pvDrop(e: DragEvent) {
		e.preventDefault();
		if (pvDrag === null || pvMark === null) return pvReset();
		const li = pageEntry[pvMark.page];
		let to = pvMark.side === 'l' ? li : li + 1;
		const [item] = opts.list.splice(pvDrag, 1);
		if (pvDrag < to) to--;
		opts.list.splice(to, 0, item);
		sel.clear();
		pvReset();
	}
	function pvReset() {
		pvDrag = null;
		pvMark = null;
	}

	// ── Uitvergroting (klik op een miniatuur) ──
	let zoom = $state<number | null>(null);
	let winW = $state(0);
	let winH = $state(0);
	let lbClose = $state<HTMLButtonElement | null>(null);
	// De opener onthouden zodat de focus bij sluiten terugkeert waar hij
	// vandaan kwam (WCAG 2.4.3 — review #343).
	let zoomOpener: HTMLElement | null = null;
	// Smal scherm (≤760px): fit-to-width op de vólle vensterbreedte; de hoogte
	// mag dan overlopen — de stage scrollt verticaal (mediaquery met dezelfde
	// grens in de CSS onderaan, samen houden). Past schaal 1.0 binnen de
	// breedte, dan komt de formule daar vanzelf op of boven uit. Desktop houdt
	// de bestaande beide-richtingen-fit met 1.4-plafond.
	const lbNarrow = $derived(winW > 0 && winW <= 760);
	const zoomScale = $derived(
		winW > 0 && winH > 0
			? lbNarrow
				? winW / (pageWmm * MM)
				: Math.min((winW * 0.92) / (pageWmm * MM), (winH * 0.86) / (210 * MM), 1.4)
			: 1
	);
	// Het plan kan onder de lightbox uit veranderen — renderen op een veilige
	// greep, en zoom zelf mag niet buiten bereik blijven staan.
	const zoomPage = $derived(zoom === null ? null : (plan[zoom] ?? null));
	$effect(() => {
		if (zoom !== null && zoom >= plan.length) zoom = plan.length > 0 ? plan.length - 1 : null;
	});
	// Focus de sluitknop alleen bij het ÓPENEN: dit effect hangt aan de
	// open-boolean (derived verandert niet bij bladeren), niet aan de
	// zoom-wáárde — anders steelt elke ‹/›-klik de focus terug naar ✕ en
	// sluit een tweede Enter de dialog (review #343).
	const zoomOpen = $derived(zoom !== null);
	$effect(() => {
		if (zoomOpen) lbClose?.focus();
	});
	function openZoom(e: MouseEvent, i: number) {
		zoomOpener = e.currentTarget as HTMLElement;
		zoom = i;
	}
	function closeZoom() {
		zoom = null;
		zoomOpener?.focus();
		zoomOpener = null;
	}
	function zoomKey(e: KeyboardEvent) {
		if (zoom === null) return;
		if (e.key === 'Escape') closeZoom();
		else if (e.key === 'ArrowRight') zoom = Math.min(plan.length - 1, zoom + 1);
		else if (e.key === 'ArrowLeft') zoom = Math.max(0, zoom - 1);
		else if (e.key === 'Tab') {
			// Simpele focus-trap: aria-modal verbergt de achtergrond al voor
			// screenreaders, dus Tab hoort binnen de balk te cirkelen — anders
			// bereik je onzichtbare controls achter de scrim (review #343).
			const btns = [...document.querySelectorAll<HTMLButtonElement>('.lb-bar button')].filter(
				(b) => !b.disabled
			);
			if (btns.length === 0) return;
			e.preventDefault();
			const cur = btns.indexOf(document.activeElement as HTMLButtonElement);
			const next = e.shiftKey
				? cur <= 0
					? btns.length - 1
					: cur - 1
				: cur === -1 || cur === btns.length - 1
					? 0
					: cur + 1;
			btns[next].focus();
		}
	}

	// De @page-maat kan niet via een CSS-klasse wisselen; dit is een vaste
	// keuze uit twee letterlijke stylesheets (geen gebruikersinvoer — de enige
	// variabele is de a5/a4-ternary), dus veilig voor {@html}.
	const pageStyle = $derived(
		`<style>@page{size:${opts.paper === 'a4' ? 'A4 landscape' : 'A5 portrait'};margin:0}</style>`
	);
</script>

<svelte:head>
	<title>Score pad — Poracle</title>
	{@html pageStyle}
</svelte:head>

<svelte:window bind:innerWidth={winW} bind:innerHeight={winH} onkeydown={zoomKey} />

<main>
	<div class="no-print">
		<h1>Score <span>pad</span></h1>
		<p class="subtitle">
			Stel je eigen Riftbound-scorepad samen: kies vellen en volgorde, bekijk de preview en print
			als PDF — voor papier of voor tablet + stylus.
		</p>

		<section class="opts panel" aria-label="Samenstelling">
			<div class="ogroups">
				{#each GROUPS as grp (grp.title)}
					<div class="ogroup">
						<p class="fglabel">{grp.title}</p>
						{#each grp.kinds as k (k)}
							<div class="step">
								<div class="stx">
									<span class="slabel">{SHEET_INFO[k].label}</span>
									<span class="shint">{SHEET_INFO[k].hint}</span>
								</div>
								<div class="sctl">
									<button
										type="button"
										aria-label="Minder — {SHEET_INFO[k].label}"
										disabled={counts[k] === 0}
										onclick={() => removeLast(k)}>−</button
									>
									<span class="scount tnum">{counts[k]}</span>
									<button
										type="button"
										aria-label="Meer — {SHEET_INFO[k].label}"
										disabled={opts.list.length >= MAX_SHEETS}
										onclick={() => add(k)}>+</button
									>
								</div>
							</div>
						{/each}
					</div>
				{/each}

				<div class="ogroup">
					<p class="fglabel">Volgorde</p>
					{#if opts.list.length === 0}
						<p class="onone">Nog niets gekozen — voeg hiernaast vellen toe.</p>
					{:else}
						<ol class="order">
							{#each opts.list as entry, i (i)}
								<!-- Drag & drop is een muis-extra; het toegankelijke pad zijn de
								     checkbox en de knoppen. Vandaar bewust géén interactieve rol
								     op de rij zelf. -->
								<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
								<li
									class:sel={sel.has(i)}
									class:drop-before={insertAt === i}
									draggable="true"
									ondragstart={(e) => onDragStart(e, i)}
									ondragover={(e) => onDragOver(e, i)}
									ondrop={onDrop}
									ondragend={resetDrag}
								>
									<input
										type="checkbox"
										class="osel"
										aria-label="Selecteer positie {i + 1} — {SHEET_INFO[entry.kind].label}"
										checked={sel.has(i)}
										onchange={() => toggleSel(i)}
									/>
									<span class="onum tnum">{i + 1}</span>
									<!-- shortLabel: onderscheid vooraan, zodat afkappen in deze smalle
									     kolom nooit twee gelijk ogende regels geeft. Het paginatal staat
									     als apart badge-element buiten de ellipsis. -->
									<span class="olabel">{SHEET_INFO[entry.kind].shortLabel}</span>
									{#if SHEET_INFO[entry.kind].pages > 1}
										<span class="opag tnum">{SHEET_INFO[entry.kind].pages} pag.</span>
									{/if}
									{#if (entry.kind === 'deck' || entry.kind === 'registration') && sourceRows.length > 0}
										<!-- Bron per vel: 'value' als string zodat één select zowel
										     'Blanco' (null) als positieve/negatieve refs kan dragen. -->
										<select
											class="osrc"
											aria-label="Deck-bron — positie {i + 1}"
											value={entry.deckRef === null ? '' : String(entry.deckRef)}
											onchange={(e) => {
												const v = e.currentTarget.value;
												entry.deckRef = v === '' ? null : Number(v);
											}}
										>
											<option value="">Blanco</option>
											{#each sourceRows as s (s.ref)}
												<option value={String(s.ref)}>{s.num} · {s.name}</option>
											{/each}
										</select>
									{/if}
									<span class="obtns">
										<button
											type="button"
											aria-label="Omhoog — positie {i + 1}"
											disabled={i === 0}
											onclick={() => move(i, -1)}>↑</button
										>
										<button
											type="button"
											aria-label="Omlaag — positie {i + 1}"
											disabled={i === opts.list.length - 1}
											onclick={() => move(i, 1)}>↓</button
										>
										<button
											type="button"
											aria-label="Verwijderen — positie {i + 1}"
											onclick={() => removeAt(i)}>✕</button
										>
									</span>
								</li>
							{/each}
							<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
							<li
								class="drop-end"
								class:drop-before={insertAt === opts.list.length}
								aria-hidden="true"
								ondragover={(e) => onDragOver(e, opts.list.length)}
								ondrop={onDrop}
							></li>
						</ol>
						{#if sel.size > 0}
							<div class="selrow">
								<span class="tnum">{sel.size} geselecteerd — verplaats of verwijder:</span>
								<button
									type="button"
									class="link-btn"
									disabled={selMin === 0}
									onclick={() => moveSelection(-1)}>Omhoog</button
								>
								<button
									type="button"
									class="link-btn"
									disabled={selMax === opts.list.length - 1}
									onclick={() => moveSelection(1)}>Omlaag</button
								>
								<button type="button" class="link-btn" onclick={removeSelected}>Verwijder</button>
								<button type="button" class="link-btn" onclick={() => sel.clear()}
									>Wis selectie</button
								>
							</div>
						{/if}
					{/if}
				</div>

				<div class="ogroup">
					<p class="fglabel">Papier</p>
					<div class="chips">
						<button
							type="button"
							class="chip"
							class:on={opts.paper === 'a5'}
							onclick={() => (opts.paper = 'a5')}>A5 — los / digitaal</button
						>
						<button
							type="button"
							class="chip"
							class:on={opts.paper === 'a4'}
							onclick={() => (opts.paper = 'a4')}>A4 — 2 vellen per pagina</button
						>
					</div>
					{#if opts.paper === 'a4'}
						<div class="chips">
							<button
								type="button"
								class="chip"
								class:on={opts.duplicate}
								onclick={() => (opts.duplicate = true)}>Snijstapel — elk vel dubbel</button
							>
							<button
								type="button"
								class="chip"
								class:on={!opts.duplicate}
								onclick={() => (opts.duplicate = false)}>Vellen op volgorde</button
							>
						</div>
					{/if}

					<p class="fglabel">Ringband-marge</p>
					<div class="chips">
						<button
							type="button"
							class="chip"
							class:on={opts.binding === 'none'}
							onclick={() => (opts.binding = 'none')}>Geen</button
						>
						<button
							type="button"
							class="chip"
							class:on={opts.binding === 'top'}
							onclick={() => (opts.binding = 'top')}>Boven</button
						>
						<button
							type="button"
							class="chip"
							class:on={opts.binding === 'side'}
							onclick={() => (opts.binding = 'side')}>Zijkant</button
						>
					</div>

					<p class="fglabel">Inkt</p>
					<div class="chips">
						<button
							type="button"
							class="chip"
							class:on={opts.ink === 'color'}
							onclick={() => (opts.ink = 'color')}>Kleur</button
						>
						<button
							type="button"
							class="chip"
							class:on={opts.ink === 'bw'}
							onclick={() => (opts.ink = 'bw')}>Zwart-wit</button
						>
					</div>

					{#if opts.ink === 'color'}
						<p class="fglabel">Spelerkleuren</p>
						<div class="pcolors">
							<label class="pcolor">
								<input
									type="color"
									value={'#' + (opts.c1 ?? DEFAULT_P1)}
									oninput={(e) => setPlayerColor('c1', e.currentTarget.value)}
								/>
								P1
							</label>
							<label class="pcolor">
								<input
									type="color"
									value={'#' + (opts.c2 ?? DEFAULT_P2)}
									oninput={(e) => setPlayerColor('c2', e.currentTarget.value)}
								/>
								P2
							</label>
							<button
								type="button"
								class="link-btn"
								disabled={opts.c1 === null && opts.c2 === null}
								onclick={() => {
									opts.c1 = null;
									opts.c2 = null;
								}}>Standaard</button
							>
						</div>
					{/if}

					{#if hasNotes}
						<p class="fglabel">Notes-stijl</p>
						<div class="chips">
							<button
								type="button"
								class="chip"
								class:on={opts.notesStyle === 'dots'}
								onclick={() => (opts.notesStyle = 'dots')}>Puntenraster</button
							>
							<button
								type="button"
								class="chip"
								class:on={opts.notesStyle === 'lines'}
								onclick={() => (opts.notesStyle = 'lines')}>Lijntjes</button
							>
						</div>
					{/if}
				</div>
			</div>

			{#if hasDeckSheet}
				<div class="deckbron">
					<p class="fglabel">Deck-bronnen</p>
					<p class="dbintro">
						Vul de deck-vellen vooraf in met één of meer decklijsten — wijs per vel een bron aan
						in het Volgorde-paneel, of laat alles leeg voor blanco invulvellen.
					</p>

					{#if sourceRows.length === 0}
						<p class="dbstatus">Geen deck-bron gekozen — de deck-vellen blijven blanco.</p>
					{:else}
						<ul class="dbsources">
							{#each sourceRows as s (s.ref)}
								<li>
									<span class="dbsnum tnum">{s.num}</span>
									<span class="dbsmain">
										<span class="dbsname">{s.name}</span>
										<span class="dbsorigin">{s.origin}</span>
										{#if s.loading}<span class="dbsorigin">laden…</span>{/if}
										{#if s.error}<span class="dberr">{s.error}</span>{/if}
										{#if s.note}<span class="dbnote">{s.note}</span>{/if}
									</span>
									<button
										type="button"
										class="link-btn"
										aria-label="Verwijder bron {s.num} — {s.name}"
										onclick={() => removeSource(s.ref)}>Verwijder</button
									>
								</li>
							{/each}
						</ul>
					{/if}

					<div class="dbgrid">
						<div class="dbway">
							<label class="dblabel" for="deck-search">Zoek in de ingest</label>
							<input
								id="deck-search"
								type="search"
								placeholder="Decknaam…"
								autocomplete="off"
								value={deckQuery}
								oninput={(e) => onDeckQuery(e.currentTarget.value)}
							/>
							{#if deckSearchError}<p class="dberr">{deckSearchError}</p>{/if}
							{#if deckResults !== null && !deckSearchError}
								{#if deckResults.length === 0}
									<p class="dbnote">Geen decks gevonden.</p>
								{:else}
									<ul class="dbhits">
										{#each deckResults as d (d.id)}
											<li>
												<button type="button" onclick={() => chooseDeck(d.id)}>
													<span class="dbname">{d.name ?? d.id}</span>
													<span class="dbcount tnum">{d.cardCount} kaarten</span>
												</button>
											</li>
										{/each}
									</ul>
								{/if}
							{/if}
						</div>
						<div class="dbway">
							<label class="dblabel" for="deck-link">Piltover-link of deck-id</label>
							<div class="dbrow">
								<input
									id="deck-link"
									type="text"
									placeholder="…/decks/view/…"
									autocomplete="off"
									bind:value={linkInput}
									onkeydown={(e) => {
										if (e.key === 'Enter') void linkFromInput();
									}}
								/>
								<!-- Bezig-status: de auto-fetch doet zo nodig een echte
								     Piltover-rondgang en kan even duren. -->
								<button
									type="button"
									class="dbbtn"
									disabled={linkBusy}
									onclick={() => void linkFromInput()}>{linkBusy ? 'Ophalen…' : 'Koppel'}</button
								>
							</div>
							{#if linkError}<p class="dberr">{linkError}</p>{/if}
						</div>
						<div class="dbway">
							<label class="dblabel" for="deck-code">Deck-code</label>
							<div class="dbrow">
								<input
									id="deck-code"
									type="text"
									placeholder="Deck-code…"
									autocomplete="off"
									bind:value={codeInput}
									onkeydown={(e) => {
										if (e.key === 'Enter') void decodeCode();
									}}
								/>
								<button type="button" class="dbbtn" disabled={codeBusy} onclick={() => void decodeCode()}
									>Lees</button
								>
							</div>
							{#if codeError}<p class="dberr">{codeError}</p>{/if}
						</div>
						<div class="dbway">
							<label class="dblabel" for="deck-text">Decklijst plakken</label>
							<textarea
								id="deck-text"
								rows="4"
								placeholder={'3x Cardname\nSideboard:\n1 Cardname'}
								bind:value={textInput}
							></textarea>
							<div class="dbrow">
								<button type="button" class="dbbtn" onclick={useDeckText}>Gebruik lijst</button>
							</div>
							{#if textError}<p class="dberr">{textError}</p>{/if}
						</div>
					</div>
				</div>
			{/if}

			{#if hasRegistration}
				<div class="deckbron">
					<p class="fglabel">Registration — personalia</p>
					<p class="dbintro">
						Deze velden blijven op dit apparaat en komen nooit in de URL of op de server —
						persoonsgegevens horen niet in deelbare links. Leeg laten kan altijd: dan vul je ze
						met pen in.
					</p>
					<div class="persongrid">
						<label class="pfield"
							>First name <input type="text" bind:value={person.firstName} /></label
						>
						<label class="pfield">Last name <input type="text" bind:value={person.lastName} /></label>
						<label class="pfield">Riot ID <input type="text" bind:value={person.riotId} /></label>
						<label class="pfield">Event <input type="text" bind:value={person.event} /></label>
						<label class="pfield">Location <input type="text" bind:value={person.location} /></label>
						<label class="pfield">Date <input type="text" bind:value={person.date} /></label>
						<label class="pfield">Designer <input type="text" bind:value={person.designer} /></label>
					</div>
				</div>
			{/if}

			<div class="actions">
				<button type="button" class="print" disabled={plan.length === 0} onclick={() => window.print()}
					>Print / bewaar als PDF</button
				>
				{#if undo !== null}
					<!-- Herstelregel op de plek van de samenvatting: reset is direct
					     ongedaan te maken, zonder confirm()-dialoog vooraf. -->
					<span class="summary">
						Samenstelling gewist —
						<button type="button" class="link-btn" onclick={restoreReset}>Herstel</button>
					</span>
				{:else}
					<span class="summary tnum">
						{#if plan.length === 0}
							Nog geen vellen gekozen
						{:else}
							{plan.length} {opts.paper === 'a4' ? 'A4' : 'A5'}-pagina{plan.length === 1
								? ''
								: "'s"}
							→ {total} vel{total === 1 ? '' : 'len'}
						{/if}
					</span>
				{/if}
				<button type="button" class="link-btn" onclick={resetAll}>Reset</button>
			</div>
			<p class="hint">
				Kies in het printdialoog "Opslaan als PDF" voor de digitale editie (tablet + stylus). De
				marges staan al op nul; drukt je printer de rasters niet af, zet dan "Achtergronden" aan.
			</p>
			<details class="uitleg">
				<summary>Hoe werkt dit?</summary>
				<ol>
					<li>
						Kies links de vellen en aantallen; herorden via het Volgorde-paneel of door miniaturen
						te slepen. Klik een miniatuur voor een uitvergroting (pijltjestoetsen bladeren, Esc
						sluit).
					</li>
					<li>
						A5 = losse vellen en de digitale editie; A4 legt twee A5's naast elkaar — snijstapel
						betekent elk vel dubbel, na het snijden twee gelijke stapels.
					</li>
					<li>
						Ringband-marge geeft extra witruimte aan de bindkant; spelerkleuren zijn aanpasbaar,
						of kies zwart-wit.
					</li>
					<li>
						"Print / bewaar als PDF": kies je printer, of bestemming "Opslaan als PDF" voor
						tablet + stylus (GoodNotes en dergelijke). Marges staan al op nul; zet
						"Achtergronden" aan als rasters ontbreken.
					</li>
					<li>
						Op de vellen is C een punt door Conquer en H een punt door Hold; de omcirkelde 8 (11
						bij 2v2) is de Victory Score — bij overshoot turf je gewoon door.
					</li>
					<li>
						De deck-vellen (Deckoverzicht en Registration) kun je vooraf laten invullen — met
						meerdere decks tegelijk: zoek decks in de ingest, of plak Piltover-links, deck-codes
						of decklijst-tekst. Kent de ingest een geplakte link nog niet, dan wordt het deck
						automatisch éénmalig van Piltover Archive opgehaald. Wijs per vel de bron aan in het
						Volgorde-paneel. Ingest-decks reizen mee in de URL; geplakte codes/lijsten en de
						personalia blijven alleen op dit apparaat.
					</li>
					<li>Je samenstelling zit in de URL, dus bookmarken of delen kan.</li>
				</ol>
			</details>
		</section>

		<div class="pvbar">
			<h2 class="pvhead">Voorbeeld</h2>
			<div class="chips pvchips">
				<button type="button" class="chip" class:on={view === 'grid'} onclick={() => (view = 'grid')}
					>Naast elkaar</button
				>
				<button type="button" class="chip" class:on={view === 'full'} onclick={() => (view = 'full')}
					>Groot</button
				>
			</div>
		</div>
	</div>

	<section
		class="preview"
		class:grid={view === 'grid'}
		bind:clientWidth={pvw}
		aria-label="Voorbeeld van de vellen"
	>
		{#each plan as pageSheets, i (i)}
			<div
				class="pslot"
				style="height: {slotH + (view === 'grid' ? 22 : 0)}px; {view === 'grid'
					? `width: ${cellW}px`
					: ''}"
			>
				{#if view === 'grid'}
					<p class="pcap tnum">
						{i + 1} · {pageSheets.map((p) => pageLabel(p)).join(' + ')}
					</p>
				{/if}
				<button
					type="button"
					class="pthumb"
					class:mark-l={pvMark?.page === i && pvMark.side === 'l'}
					class:mark-r={pvMark?.page === i && pvMark.side === 'r'}
					draggable={pvDraggable}
					aria-label="Vergroot pagina {i + 1} — {pageSheets.map((p) => pageLabel(p)).join(' + ')}"
					onclick={(e) => openZoom(e, i)}
					ondragstart={(e) => pvDragStart(e, i)}
					ondragover={(e) => pvDragOver(e, i)}
					ondrop={pvDrop}
					ondragend={pvReset}
				>
					<div
						class="ppage"
						class:a4={opts.paper === 'a4'}
						class:bind-top={opts.binding === 'top'}
						class:bind-side={opts.binding === 'side'}
						style="transform: scale({scale});{colorStyle}"
					>
						{#each pageSheets as p, j (j)}
							{#if j > 0}<div class="cut"></div>{/if}
							{@render sheetOf(p)}
						{/each}
					</div>
				</button>
			</div>
		{/each}
	</section>

	{#if zoom !== null && zoomPage}
		<div class="lightbox" role="dialog" aria-modal="true" aria-label="Pagina {zoom + 1} van {plan.length}">
			<button class="lb-scrim" aria-label="Sluiten" tabindex="-1" onclick={closeZoom}></button>
			<!-- De klemdoos (.lb-clip) draagt de geschaalde maat en knipt de
			     ongeschaalde ppage-layoutdoos af; de stage eromheen kan zo op
			     smalle schermen zélf verticaal scrollen (mediaquery onderaan)
			     zonder dat de layout-overflow van de transform meescrollt. -->
			<div class="lb-stage">
				<div
					class="lb-clip"
					style="width: {Math.round(pageWmm * MM * zoomScale)}px; height: {Math.round(
						210 * MM * zoomScale
					)}px"
				>
					<div
						class="ppage"
						class:a4={opts.paper === 'a4'}
						class:bind-top={opts.binding === 'top'}
						class:bind-side={opts.binding === 'side'}
						style="transform: scale({zoomScale});{colorStyle}"
					>
						{#each zoomPage as p, j (j)}
							{#if j > 0}<div class="cut"></div>{/if}
							{@render sheetOf(p)}
						{/each}
					</div>
				</div>
			</div>
			<div class="lb-bar">
				<button
					type="button"
					onclick={() => (zoom = Math.max(0, (zoom ?? 0) - 1))}
					disabled={zoom === 0}
					aria-label="Vorige pagina">‹</button
				>
				<span class="tnum">{zoom + 1} / {plan.length}</span>
				<button
					type="button"
					onclick={() => (zoom = Math.min(plan.length - 1, (zoom ?? 0) + 1))}
					disabled={zoom === plan.length - 1}
					aria-label="Volgende pagina">›</button
				>
				<button type="button" bind:this={lbClose} onclick={closeZoom} aria-label="Sluiten"
					>✕</button
				>
			</div>
		</div>
	{/if}
</main>

{#snippet sheetOf(p: SheetPageEntry | null)}
	{#if p === null}
		<div class="empty" aria-hidden="true"></div>
	{:else if p.page === 'match'}
		<MatchSheet {bw} />
	{:else if p.page === 'matchalt'}
		<MatchAltSheet {bw} />
	{:else if p.page === 'solo'}
		<SoloSheet {bw} />
	{:else if p.page === 'ffa'}
		<FfaSheet {bw} />
	{:else if p.page === 'duo'}
		<DuoSheet {bw} />
	{:else if p.page === 'tournament'}
		<TournamentSheet {bw} />
	{:else if p.page === 'reflection'}
		<ReflectionSheet {bw} />
	{:else if p.page === 'milestone'}
		<MilestoneSheet {bw} part={1} />
	{:else if p.page === 'milestone2'}
		<MilestoneSheet {bw} part={2} />
	{:else if p.page === 'notes'}
		<NotesSheet {bw} style={opts.notesStyle} />
	{:else if p.page === 'deck'}
		<DeckSheet {bw} deck={prefillFor(p.deckRef)} />
	{:else if p.page === 'registration'}
		<RegistrationSheet {bw} deck={prefillFor(p.deckRef)} {person} />
	{/if}
{/snippet}

<style>
	main {
		max-width: 1180px;
		margin: 0 auto;
		padding: 24px 20px;
	}
	h1 span {
		color: var(--accent);
	}
	.subtitle {
		color: var(--muted);
	}

	.opts {
		padding: 16px 18px;
		margin: 14px 0 18px;
	}
	.ogroups {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
		gap: 18px 24px;
	}
	.fglabel {
		margin: 0 0 8px;
		font-size: 0.72rem;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.05em;
		color: var(--muted);
	}
	.fglabel:not(:first-child) {
		margin-top: 14px;
	}

	.step {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: 12px;
		padding: 6px 0;
		border-bottom: 1px solid var(--border);
	}
	.step:last-child {
		border-bottom: 0;
	}
	.stx {
		display: flex;
		flex-direction: column;
		min-width: 0;
	}
	.slabel {
		font-size: 0.9rem;
		font-weight: 600;
	}
	.shint {
		font-size: 0.76rem;
		color: var(--muted);
	}
	.sctl {
		display: inline-flex;
		align-items: center;
		gap: 2px;
		flex: none;
	}
	.sctl button {
		width: 30px;
		height: 30px;
		border: 1px solid var(--border);
		border-radius: 8px;
		background: var(--surface-deep);
		color: var(--text);
		font-size: 1rem;
		cursor: pointer;
	}
	.sctl button:disabled {
		opacity: 0.35;
		cursor: default;
	}
	.scount {
		min-width: 26px;
		text-align: center;
		font-weight: 700;
	}

	/* Volgorde-paneel: genummerde lijst met omhoog/omlaag/verwijderen. */
	.onone {
		color: var(--muted);
		font-size: 0.85rem;
	}
	/* Geen eigen scrollbalk: de lijst groeit gewoon mee met de kolom. */
	.order {
		list-style: none;
		margin: 0;
		padding: 0;
	}
	.order li {
		display: flex;
		align-items: center;
		gap: 8px;
		padding: 5px 0;
		border-bottom: 1px solid var(--border);
		cursor: grab;
	}
	.order li.sel {
		background: var(--accent-soft);
	}
	/* Invoeg-indicator als inset-schaduw: verschuift de layout niet. */
	.order li.drop-before {
		box-shadow: inset 0 2px 0 var(--accent);
	}
	.order li.drop-end {
		border-bottom: 0;
		min-height: 10px;
		padding: 0;
		cursor: default;
	}
	.order li:not(.drop-end):last-of-type {
		border-bottom: 0;
	}
	.osel {
		accent-color: var(--accent);
		width: 15px;
		height: 15px;
		flex: none;
		cursor: pointer;
	}
	.selrow {
		display: flex;
		align-items: center;
		flex-wrap: wrap;
		gap: 2px 8px;
		margin-top: 8px;
		font-size: 0.8rem;
		color: var(--muted);
	}
	/* De auto-marge van .link-btn hoort bij de actions-rij; hier staan vier
	   knoppen als compacte groep naast de tekst. */
	.selrow .link-btn {
		margin-left: 0;
	}
	.onum {
		color: var(--muted);
		font-size: 0.8rem;
		min-width: 18px;
		text-align: right;
	}
	.olabel {
		flex: 1;
		min-width: 0;
		font-size: 0.86rem;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}
	/* Paginatal-badge: buiten het truncerende label, dus altijd leesbaar. */
	.opag {
		flex: none;
		font-size: 0.68rem;
		color: var(--muted);
		border: 1px solid var(--border);
		border-radius: 999px;
		padding: 1px 6px;
		white-space: nowrap;
	}
	/* Bron-select op deck-/registration-rijen: compact, kapt lange decknamen
	   af — het volledige overzicht staat in het Deck-bronnen-paneel. */
	.osrc {
		flex: none;
		max-width: 108px;
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-radius: 7px;
		color: var(--text);
		font-size: 0.72rem;
		font-family: inherit;
		padding: 3px 4px;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}
	.obtns {
		display: inline-flex;
		gap: 2px;
	}
	.obtns button {
		width: 26px;
		height: 26px;
		border: 1px solid var(--border);
		border-radius: 7px;
		background: var(--surface-deep);
		color: var(--text);
		font-size: 0.8rem;
		cursor: pointer;
	}
	.obtns button:disabled {
		opacity: 0.3;
		cursor: default;
	}

	.chips {
		display: flex;
		flex-wrap: wrap;
		gap: 6px;
		margin-bottom: 8px;
	}
	.chip {
		background: var(--surface);
		color: var(--muted);
		border: 1px solid var(--border);
		border-radius: 999px;
		padding: 5px 12px;
		font-size: 0.8rem;
		cursor: pointer;
	}
	.chip:hover {
		border-color: var(--border-strong);
		color: var(--text);
	}
	/* Actieve chips als tint (keuze Sjoerd, UX-review): vol geel blijft
	   exclusief voor de Print-CTA, zodat de primaire actie de enige gele
	   vlek is — conform de actieve-filterchip-conventie in STYLE-CONTRACT. */
	.chip.on {
		background: var(--accent-soft);
		color: var(--text);
		border-color: var(--accent);
		font-weight: 600;
	}

	/* Spelerkleuren: compacte native pickers met hun label ernaast. */
	.pcolors {
		display: flex;
		align-items: center;
		gap: 12px;
	}
	.pcolor {
		display: inline-flex;
		align-items: center;
		gap: 6px;
		font-size: 0.8rem;
		color: var(--muted);
		cursor: pointer;
	}
	.pcolors input[type='color'] {
		width: 30px;
		height: 30px;
		border: 1px solid var(--border);
		border-radius: 8px;
		padding: 0;
		background: none;
		cursor: pointer;
	}
	/* De auto-marge van .link-btn (uit de actions-rij) is hier niet gewenst. */
	.pcolors .link-btn {
		margin-left: 0;
	}

	/* ── Deck-bron (#344): vier invoerwegen naar een voorgevuld deck-vel, plus
	   de lokale personalia voor het registration sheet. ── */
	.deckbron {
		margin-top: 16px;
		padding-top: 14px;
		border-top: 1px solid var(--border);
	}
	.dbintro {
		margin: 0 0 10px;
		font-size: 0.78rem;
		color: var(--muted);
	}
	.dbstatus {
		margin: 0 0 10px;
		font-size: 0.85rem;
	}
	.dbnote {
		margin: 4px 0 0;
		font-size: 0.76rem;
		color: var(--muted);
	}
	.dberr {
		margin: 4px 0 0;
		font-size: 0.76rem;
		color: var(--err);
	}
	/* Bronnenlijst (#346): één rij per deck-bron — nummer (matcht het
	   bron-select in het Volgorde-paneel), naam + herkomst, verwijderknop. */
	.dbsources {
		list-style: none;
		margin: 0 0 12px;
		padding: 0;
	}
	.dbsources li {
		display: flex;
		align-items: baseline;
		gap: 8px;
		padding: 5px 0;
		border-bottom: 1px solid var(--border);
	}
	.dbsources li:last-child {
		border-bottom: 0;
	}
	.dbsnum {
		flex: none;
		color: var(--muted);
		font-size: 0.8rem;
		min-width: 18px;
		text-align: right;
	}
	.dbsmain {
		flex: 1;
		min-width: 0;
		display: flex;
		flex-direction: column;
		gap: 1px;
	}
	.dbsname {
		font-size: 0.86rem;
		font-weight: 600;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}
	.dbsorigin {
		font-size: 0.72rem;
		color: var(--muted);
	}
	.dbsmain .dberr,
	.dbsmain .dbnote {
		margin: 0;
	}
	/* De auto-marge van .link-btn hoort bij de actions-rij; in de statusregels
	   staat de knop gewoon in de lopende tekst. */
	.deckbron .link-btn {
		margin-left: 0;
		padding: 0 2px;
		text-decoration: underline;
	}
	.dbgrid {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
		gap: 12px 24px;
	}
	.dblabel {
		display: block;
		margin-bottom: 4px;
		font-size: 0.76rem;
		font-weight: 600;
		color: var(--muted);
	}
	.dbway input,
	.dbway textarea {
		width: 100%;
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-radius: 8px;
		color: var(--text);
		padding: 7px 10px;
		font-size: 0.85rem;
		font-family: inherit;
	}
	.dbway textarea {
		resize: vertical;
		margin-bottom: 6px;
	}
	.dbrow {
		display: flex;
		gap: 6px;
	}
	.dbbtn {
		flex: none;
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-radius: 8px;
		color: var(--text);
		padding: 7px 12px;
		font-size: 0.8rem;
		cursor: pointer;
	}
	.dbbtn:hover {
		border-color: var(--border-strong);
	}
	.dbbtn:disabled {
		opacity: 0.5;
		cursor: default;
	}
	/* Trefferlijst van de ingest-zoek: klikbare rijen, naam + kaarttelling. */
	.dbhits {
		list-style: none;
		margin: 6px 0 0;
		padding: 0;
		border: 1px solid var(--border);
		border-radius: 8px;
		overflow: hidden;
		max-height: 220px;
		overflow-y: auto;
	}
	.dbhits li + li {
		border-top: 1px solid var(--border);
	}
	.dbhits button {
		display: flex;
		align-items: baseline;
		justify-content: space-between;
		gap: 8px;
		width: 100%;
		background: none;
		border: 0;
		color: var(--text);
		padding: 6px 10px;
		font-size: 0.82rem;
		text-align: left;
		cursor: pointer;
	}
	.dbhits button:hover {
		background: var(--accent-soft);
	}
	.dbname {
		min-width: 0;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}
	.dbcount {
		flex: none;
		color: var(--muted);
		font-size: 0.74rem;
	}
	.persongrid {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
		gap: 8px 16px;
	}
	.pfield {
		display: flex;
		flex-direction: column;
		gap: 3px;
		font-size: 0.76rem;
		font-weight: 600;
		color: var(--muted);
	}
	.pfield input {
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-radius: 8px;
		color: var(--text);
		padding: 7px 10px;
		font-size: 0.85rem;
		font-weight: 400;
		font-family: inherit;
	}

	.actions {
		display: flex;
		align-items: center;
		gap: 14px;
		flex-wrap: wrap;
		margin-top: 16px;
		padding-top: 14px;
		border-top: 1px solid var(--border);
	}
	.print {
		background: var(--accent);
		color: var(--accent-ink);
		border: 0;
		border-radius: 10px;
		padding: 10px 18px;
		font-weight: 700;
		font-size: 0.95rem;
		cursor: pointer;
	}
	.print:disabled {
		opacity: 0.5;
		cursor: default;
	}
	.summary {
		color: var(--muted);
		font-size: 0.85rem;
	}
	/* De inline herstelknop erft de .link-btn-stijl, maar niet de auto-marge
	   (die hoort bij de losse Reset-knop in de actions-rij). */
	.summary .link-btn {
		margin-left: 0;
		padding: 0 2px;
		text-decoration: underline;
	}
	.link-btn {
		background: none;
		border: 0;
		color: var(--muted);
		cursor: pointer;
		font-size: 0.85rem;
		padding: 6px 4px;
		margin-left: auto;
	}
	.link-btn:hover {
		color: var(--text);
	}
	.link-btn:disabled {
		opacity: 0.4;
		cursor: default;
	}
	.hint {
		margin: 10px 0 0;
		font-size: 0.78rem;
		color: var(--muted);
	}

	/* Uitleg-blok: standaard ingeklapt — de volledige werkwijze voor wie wil. */
	.uitleg {
		margin-top: 10px;
		font-size: 0.82rem;
		color: var(--muted);
	}
	.uitleg summary {
		cursor: pointer;
		font-weight: 600;
	}
	.uitleg ol {
		margin: 6px 0 0;
		padding-left: 20px;
	}
	.uitleg li {
		margin: 4px 0;
	}

	.pvbar {
		display: flex;
		align-items: baseline;
		justify-content: space-between;
		gap: 12px;
	}
	.pvhead {
		font-size: 0.72rem;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.05em;
		color: var(--muted);
		margin: 0 0 10px;
	}
	.pvchips {
		margin-bottom: 6px;
	}
	.pcap {
		margin: 0 0 4px;
		font-size: 0.72rem;
		color: var(--muted);
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}

	/* ── Preview: pagina's op schaal — grid (naast elkaar) of groot ── */
	.preview {
		display: flex;
		flex-direction: column;
		gap: 18px;
	}
	.preview.grid {
		flex-direction: row;
		flex-wrap: wrap;
		gap: 14px;
		align-items: flex-start;
	}
	.pslot {
		overflow: hidden;
	}
	/* De miniatuur is een knop (klik = uitvergroten, slepen = herordenen). */
	.pthumb {
		display: block;
		width: 100%;
		padding: 0;
		border: 0;
		background: none;
		cursor: zoom-in;
		text-align: left;
	}
	.pthumb.mark-l {
		box-shadow: inset 3px 0 0 var(--accent);
	}
	.pthumb.mark-r {
		box-shadow: inset -3px 0 0 var(--accent);
	}
	.ppage {
		width: 148mm;
		height: 210mm;
		display: flex;
		background: var(--paper);
		box-shadow: var(--shadow-panel-lg);
		border: 1px solid var(--border);
		transform-origin: top left;
	}
	.ppage.a4 {
		width: 297mm;
	}
	/* Ringband-marge: de bind-rand krijgt 16mm i.p.v. 8mm binnenmarge. */
	.ppage.bind-top {
		--bind-top: 16mm;
	}
	.ppage.bind-side {
		--bind-left: 16mm;
	}
	.cut {
		width: 1mm;
		flex: none;
		border-left: 0.25mm dashed var(--paper-line);
		margin: 3mm 0;
	}
	.empty {
		width: 148mm;
		height: 210mm;
	}

	/* ── Lightbox: uitvergrote pagina met navigatie ── */
	.lightbox {
		position: fixed;
		inset: 0;
		z-index: 40;
		display: flex;
		flex-direction: column;
		align-items: center;
		justify-content: center;
		gap: 12px;
	}
	.lb-scrim {
		position: absolute;
		inset: 0;
		background: rgba(10, 12, 18, 0.72);
		border: 0;
		cursor: pointer;
	}
	.lb-stage {
		position: relative;
		z-index: 1;
		box-shadow: 0 24px 80px -24px rgba(0, 0, 0, 0.7);
	}
	/* Het knippen zit op de klemdoos: de ppage-layoutdoos is groter (schaal
	   < 1) of kleiner (schaal > 1) dan de weergave, en de stage moet op smalle
	   schermen vrij zijn om zelf te scrollen. */
	.lb-clip {
		overflow: hidden;
	}
	.lb-stage .ppage {
		box-shadow: none;
		border: 0;
	}
	/* Smal scherm: de lightbox-pagina rendert fit-to-width op de volle
	   vensterbreedte (zie lbNarrow in de script — zelfde 760px-grens) en de
	   stage pant/scrollt verticaal binnen het venster; 84px laat ruimte voor
	   de navigatiebalk eronder. Desktop blijft ongewijzigd. */
	@media (max-width: 760px) {
		.lb-stage {
			max-height: calc(100dvh - 84px);
			overflow-y: auto;
			overscroll-behavior: contain;
		}
	}
	.lb-bar {
		position: relative;
		z-index: 1;
		display: flex;
		align-items: center;
		gap: 10px;
		background: var(--surface);
		border: 1px solid var(--border);
		border-radius: 999px;
		padding: 6px 12px;
		color: var(--muted);
		font-size: 0.85rem;
	}
	.lb-bar button {
		width: 32px;
		height: 32px;
		border: 1px solid var(--border);
		border-radius: 50%;
		background: var(--surface-deep);
		color: var(--text);
		cursor: pointer;
	}
	.lb-bar button:disabled {
		opacity: 0.35;
		cursor: default;
	}

	/* ── Print: alleen de pagina's, op ware grootte, één per @page ── */
	@media print {
		/* App-schil weg — ook op andere routes onschadelijk, maar hier nodig. */
		:global(.topbar),
		:global(.sidebar),
		:global(.site-footer),
		:global(.filter-fab),
		:global(.scrim),
		:global(.rail) {
			display: none !important;
		}
		:global(.shell),
		:global(.workarea),
		:global(.content) {
			display: block !important;
			min-height: 0 !important;
		}
		/* De .ppage is bewust 0.4mm korter dan de @page (spookpagina-marge);
		   in die kier schemert de body-achtergrond door — in het donkere
		   thema een zwarte streep onderaan élke pagina zodra "Achtergronden"
		   aan staat (review #343). Papier blijft papier, ook bij donker
		   browsen. */
		:global(html),
		:global(body) {
			background: var(--paper) !important;
		}
		main {
			max-width: none;
			margin: 0;
			padding: 0;
		}
		.no-print {
			display: none !important;
		}
		.preview {
			display: block;
		}
		.pslot {
			height: auto !important;
			width: auto !important;
			overflow: visible;
		}
		.pcap {
			display: none;
		}
		.lightbox {
			display: none !important;
		}
		.pthumb {
			box-shadow: none !important;
		}
		.ppage {
			transform: none !important;
			box-shadow: none;
			border: 0;
			/* Fractioneel korter dan de @page voorkomt spookpagina's door
			   afronding; de vellen hebben onderaan marge genoeg. */
			height: 209.6mm;
			overflow: hidden;
			break-after: page;
		}
		.ppage:last-child {
			break-after: auto;
		}
	}

	/* ── Touch: de globale 44px-min-height (app.css) rekt smalle knopjes tot
	   ovalen en laat de breedte onder de raakvlak-eis — dus hier ook de
	   breedte mee laten groeien en de vormen herstellen (review #343). Juist
	   op touch werkt HTML5-DnD niet, dus deze controls zijn daar het enige
	   herorden-pad. ── */
	@media (pointer: coarse) {
		.sctl button,
		.obtns button {
			min-width: 44px;
			width: auto;
			border-radius: 9px;
		}
		.lb-bar button {
			min-width: 44px;
			width: auto;
			border-radius: 999px;
			padding: 0 14px;
		}
		.osel {
			width: 24px;
			height: 24px;
		}
		.pcolor input {
			width: 44px;
			height: 44px;
		}
	}
</style>
