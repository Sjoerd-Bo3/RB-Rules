<script lang="ts" module>
	// Persoonsgegevens voor de kop — allemaal optioneel: een leeg veld wordt
	// gewoon een invullijn, prefill drukt de waarde óp die lijn.
	export interface RegistrationPerson {
		firstName?: string;
		lastName?: string;
		riotId?: string;
		event?: string;
		location?: string;
		date?: string;
		deckName?: string;
		designer?: string;
	}
</script>

<script lang="ts">
	// Deck registration (#344): de opzet van het Piltover Archive-registratievel
	// in Poracle-vormgeving — zelfde structuur (kop, decklijst in twee kolommen,
	// official use-blok), eigen ontwerp; onofficieel, de SheetFrame-voet dekt de
	// disclaimer. Met een DeckPrefill worden de kaartregels voorgedrukt; zonder
	// deck is dit een volledig blanco vel met vaste aantallen invullijnen, zodat
	// prefill en blanco exact dezelfde geometrie delen.
	import SheetFrame from './SheetFrame.svelte';
	import type { DeckPrefill } from '$lib/deckPrefill';

	let {
		deck = null,
		person = null,
		bw = false
	}: { deck?: DeckPrefill | null; person?: RegistrationPerson | null; bw?: boolean } = $props();

	// Vaste regelaantallen (blanco én als vaste maat bij prefill) — het
	// maatbudget in de PR rekent hiermee; wijzig je een aantal, reken het na.
	const LEGEND_ROWS = 2;
	const BF_ROWS = 3;
	const RUNE_ROWS = 3;
	const SB_ROWS = 8;
	/** Main-deckregels links; de rest stroomt naar CONTINUED rechts. */
	const LEFT_MAIN_ROWS = 22;
	const CONT_ROWS = 14;

	interface Row {
		qty: string;
		name: string;
		/** Chosen Champion-rij (champions-sectie) — krijgt de CC-badge. */
		cc: boolean;
	}

	const EMPTY: Row = { qty: '', name: '', cc: false };

	function rowsOf(section: string): Row[] {
		const rows = deck?.sections.find((s) => s.section === section)?.rows ?? [];
		return rows.map((r) => ({ qty: r.qty > 0 ? String(r.qty) : '', name: r.name, cc: false }));
	}

	/** Kap op `max` én vul aan tot `max`: het vel houdt altijd dezelfde vorm. */
	function fit(rows: Row[], max: number): Row[] {
		const out = rows.slice(0, max);
		while (out.length < max) out.push(EMPTY);
		return out;
	}

	const legendRows = $derived(fit(rowsOf('legend'), LEGEND_ROWS));
	const bfRows = $derived(fit(rowsOf('battlefields'), BF_ROWS));
	const runeRows = $derived(fit(rowsOf('runes'), RUNE_ROWS));
	const sbRows = $derived(fit(rowsOf('sideboard'), SB_ROWS));

	// Chosen Champions bovenaan het main deck (dit vel kent geen apart
	// champions-vak — de champions-sectie ís de Chosen Champion-informatie);
	// blanco vel: één lege regel met CC-vakje zodat de speler zelf markeert.
	const mainAll = $derived(
		deck === null
			? [{ ...EMPTY, cc: true }]
			: [...rowsOf('champions').map((r) => ({ ...r, cc: true })), ...rowsOf('maindeck')]
	);
	const leftMain = $derived(fit(mainAll, LEFT_MAIN_ROWS));
	// CONTINUED: past de rest niet (extreem groot deck), dan kappen we af met
	// een microregel — nooit van het vel lopen, nooit clippen.
	const contAll = $derived(mainAll.slice(LEFT_MAIN_ROWS));
	const contOverflow = $derived(Math.max(0, contAll.length - CONT_ROWS));
	const contRows = $derived(
		contOverflow > 0 ? contAll.slice(0, CONT_ROWS - 1) : fit(contAll, CONT_ROWS)
	);
	const moreCount = $derived(contOverflow > 0 ? contOverflow + 1 : 0);

	const initial = $derived((person?.lastName ?? '').trim().charAt(0).toUpperCase());
	const deckName = $derived(person?.deckName ?? deck?.name ?? '');
</script>

{#snippet cardRow(r: Row)}
	<div class="drow">
		<span class="dq">{r.qty}</span>
		<span class="dn">
			<span class="nm">{r.name}</span>
			{#if r.cc}<span class="ccb">CC</span>{/if}
		</span>
	</div>
{/snippet}

<SheetFrame {bw} title="Deck Registration" sub="Print clearly using English card names">
	<div class="reghead">
		<div class="regrows">
			<div class="sh-row">
				<span class="sh-field"
					><span class="micro">Date</span><span class="fill val">{person?.date ?? ''}</span></span
				>
				<span class="sh-field" style="flex: 1.6"
					><span class="micro">Event</span><span class="fill val">{person?.event ?? ''}</span></span
				>
			</div>
			<div class="sh-row">
				<span class="sh-field"
					><span class="micro">Location</span><span class="fill val">{person?.location ?? ''}</span
					></span
				>
				<span class="sh-field" style="flex: 1.6"
					><span class="micro">Deck name</span><span class="fill val">{deckName}</span></span
				>
			</div>
		</div>
		<div class="initial">
			<span class="ibox">{initial}</span>
			<span class="ilbl">First letter of last name</span>
		</div>
	</div>
	<div class="sh-row">
		<span class="sh-field" style="flex: 1.3"
			><span class="micro">Deck designer</span><span class="fill val">{person?.designer ?? ''}</span
			></span
		>
		<span class="sh-field"
			><span class="micro">First name</span><span class="fill val">{person?.firstName ?? ''}</span
			></span
		>
	</div>
	<div class="sh-row">
		<span class="sh-field"
			><span class="micro">Last name</span><span class="fill val">{person?.lastName ?? ''}</span
			></span
		>
		<span class="sh-field"
			><span class="micro">Riot ID</span><span class="fill val">{person?.riotId ?? ''}</span></span
		>
	</div>

	<div class="cols">
		<div class="col">
			<div class="sec csec first"><span>Legend</span></div>
			{#each legendRows as r, i (i)}{@render cardRow(r)}{/each}
			<div class="sec csec"><span>Battlefields</span><span class="sec-note">3 required</span></div>
			{#each bfRows as r, i (i)}{@render cardRow(r)}{/each}
			<div class="sec csec"><span>Main deck</span><span class="sec-note">40 cards min</span></div>
			<div class="dhead"><span class="hq">#</span><span class="hn">Card name</span></div>
			{#each leftMain as r, i (i)}{@render cardRow(r)}{/each}
		</div>
		<div class="col">
			<div class="sec csec first"><span>Main deck — continued</span></div>
			<div class="dhead"><span class="hq">#</span><span class="hn">Card name</span></div>
			{#each contRows as r, i (i)}{@render cardRow(r)}{/each}
			{#if moreCount > 0}<div class="more">+{moreCount} more — see full list online</div>{/if}
			<div class="sec csec"><span>Runes</span><span class="sec-note">12 runes</span></div>
			{#each runeRows as r, i (i)}{@render cardRow(r)}{/each}
			<div class="sec csec"><span>Sideboard</span><span class="sec-note">0–8 cards</span></div>
			{#each sbRows as r, i (i)}{@render cardRow(r)}{/each}

			<div class="official">
				<span class="off-title">For official use only</span>
				<div class="off-grid">
					{#each [1, 2] as c (c)}
						<div class="off-block">
							<span class="sh-field"
								><span class="micro">Deck check rd</span><span class="fill"></span></span
							>
							<span class="sh-field"><span class="micro">Status</span><span class="fill"></span></span>
							<span class="sh-field"><span class="micro">Judge</span><span class="fill"></span></span>
						</div>
					{/each}
				</div>
				<div class="off-counts">
					<span class="micro">Main / SB</span><span class="cfill"></span><span class="slash">/</span
					><span class="cfill"></span>
				</div>
			</div>
		</div>
	</div>
</SheetFrame>

<style>
	.reghead {
		display: flex;
		align-items: flex-start;
		gap: 3mm;
	}
	.regrows {
		flex: 1;
		min-width: 0;
	}
	/* Vak rechtsboven voor de eerste letter van de achternaam (sorteervak op
	   het officiële vel): groot invulvak + microlabel eronder. */
	.initial {
		flex: none;
		width: 15mm;
		display: flex;
		flex-direction: column;
		align-items: center;
		gap: 0.8mm;
	}
	.ibox {
		width: 8mm;
		height: 8mm;
		border: 0.4mm solid var(--paper-ink);
		border-radius: 1mm;
		display: flex;
		align-items: center;
		justify-content: center;
		font-size: 12pt;
		font-weight: 800;
	}
	.ilbl {
		font-size: 4.6pt;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.06em;
		color: var(--paper-muted);
		text-align: center;
		line-height: 1.3;
	}

	/* Prefill-waarde óp de invullijn: overflow: hidden houdt de baseline van
	   het vak gelijk aan die van een lége lijn (bottom edge), zodat prefill en
	   blanco dezelfde geometrie delen. */
	.val {
		font-size: 6.5pt;
		line-height: 4mm;
		white-space: nowrap;
		overflow: hidden;
		text-overflow: ellipsis;
		padding: 0 0.6mm;
	}

	.cols {
		display: grid;
		grid-template-columns: 1fr 1fr;
		column-gap: 4mm;
		align-items: start;
		margin-top: 0.6mm;
	}
	.col {
		min-width: 0;
	}

	/* Compactere sectiekoppen dan de frame-default; .sec.csec wint op
	   specificiteit van .sheet :global(.sec) zonder aan het frame te zitten. */
	.sec.csec {
		margin: 2mm 0 1mm;
	}
	.sec.csec.first {
		margin-top: 0;
	}

	.dhead {
		display: flex;
		gap: 1.6mm;
		font-size: 4.8pt;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.08em;
		color: var(--paper-muted);
		margin-bottom: 0.5mm;
	}
	.hq {
		flex: none;
		width: 4.6mm;
		text-align: center;
	}
	.hn {
		flex: 1;
	}

	/* Kaartregel: qty-vakje + naamlijn; de tekst rust óp de schrijflijn
	   (line-height net onder de rijhoogte). Regelhoogte 3.9mm — het
	   maatbudget in de PR rekent hiermee. */
	.drow {
		display: flex;
		gap: 1.6mm;
		height: 3.9mm;
		font-size: 6.2pt;
	}
	.dq {
		flex: none;
		width: 4.6mm;
		text-align: center;
		line-height: 3.85mm;
		border-bottom: 0.25mm solid var(--paper-line);
		font-variant-numeric: tabular-nums;
	}
	.dn {
		flex: 1;
		min-width: 0;
		display: flex;
		align-items: baseline;
		gap: 1mm;
		border-bottom: 0.25mm solid var(--paper-line);
	}
	.nm {
		flex: 1;
		min-width: 0;
		line-height: 3.85mm;
		white-space: nowrap;
		overflow: hidden;
		text-overflow: ellipsis;
	}
	/* CC-badge: omkaderd microlabel voor de Chosen Champion-rijen — bewust
	   inkt-kleur, zodat hij in bw net zo leest als in kleur. */
	.ccb {
		flex: none;
		font-size: 4.4pt;
		font-weight: 800;
		letter-spacing: 0.06em;
		border: 0.28mm solid var(--paper-ink);
		border-radius: 0.6mm;
		padding: 0 0.7mm;
		line-height: 2.1mm;
	}

	.more {
		font-size: 4.8pt;
		color: var(--paper-muted);
		letter-spacing: 0.04em;
		padding-top: 0.6mm;
	}

	/* Official use-blok: zwaardere rand (inkt) — dit is het judge-vak. */
	.official {
		margin-top: 2mm;
		border: 0.35mm solid var(--paper-ink);
		border-radius: 1.2mm;
		padding: 1.6mm 2mm;
	}
	.off-title {
		display: block;
		font-size: 5pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.12em;
		margin-bottom: 1mm;
	}
	.off-grid {
		display: grid;
		grid-template-columns: 1fr 1fr;
		column-gap: 3mm;
	}
	.off-block {
		display: flex;
		flex-direction: column;
		gap: 0.6mm;
		min-width: 0;
	}
	.off-counts {
		display: flex;
		align-items: baseline;
		gap: 1.6mm;
		margin-top: 1mm;
	}
	.cfill {
		flex: 1;
		border-bottom: 0.3mm solid var(--paper-line);
		height: 3.6mm;
	}
	.slash {
		color: var(--paper-muted);
		font-size: 6.5pt;
	}
</style>
