<script lang="ts">
	// Deck overview (#344): compacte decklijst in twee kolommen plus drie
	// matchup-plannen — het vel dat naast de speelmat ligt. Met een DeckPrefill
	// wordt de lijst voorgedrukt; zonder deck een blanco vel met een vast
	// aantal invulregels per sectie. De kolomverdeling rekent in mm (dezelfde
	// maten als de CSS hieronder) en kapt extreme lijsten af met een
	// microregel — nooit van het vel lopen, nooit clippen.
	import SheetFrame from './SheetFrame.svelte';
	import type { DeckPrefill } from '$lib/deckPrefill';

	let { deck = null, bw = false }: { deck?: DeckPrefill | null; bw?: boolean } = $props();

	type Item =
		| { t: 'head'; label: string }
		| { t: 'row'; qty: string; name: string }
		| { t: 'more'; n: number };

	// mm-hoogtes voor de kolomverdeling — spiegelen de CSS (.dsec/.drow/.more);
	// wijzig je daar iets, houd deze synchroon. Het kolombudget (CAP_MM) volgt
	// uit het maatbudget in de PR: lichaam ~176mm minus kop, matchups en notes.
	const HEAD_MM = 5.8;
	const ROW_MM = 3.6;
	const MORE_MM = 3.2;
	const CAP_MM = 106;

	/** Sectievolgorde + aantal blanco invulregels. Bench heeft blanco 0: het
	 *  blanco vel kent geen bench-vak, prefill met bench-rijen toont ze wél. */
	const SECTIONS: { key: string; label: string; blank: number }[] = [
		{ key: 'legend', label: 'Legend', blank: 1 },
		{ key: 'champions', label: 'Champions', blank: 2 },
		{ key: 'battlefields', label: 'Battlefields', blank: 3 },
		{ key: 'runes', label: 'Runes', blank: 2 },
		{ key: 'maindeck', label: 'Main deck', blank: 18 },
		{ key: 'sideboard', label: 'Sideboard', blank: 8 },
		{ key: 'bench', label: 'Bench', blank: 0 }
	];

	const items = $derived.by(() => {
		const out: Item[] = [];
		for (const s of SECTIONS) {
			const rows = deck?.sections.find((x) => x.section === s.key)?.rows ?? [];
			// Prefill: alleen gevulde secties; blanco: het vaste aantal regels.
			const n = deck === null ? s.blank : rows.length;
			if (n === 0) continue;
			out.push({ t: 'head', label: s.label });
			for (let i = 0; i < n; i++) {
				const r = rows[i];
				out.push({
					t: 'row',
					qty: r && r.qty > 0 ? String(r.qty) : '',
					name: r?.name ?? ''
				});
			}
		}
		return out;
	});

	// Flow: vul links tot het mm-budget, dan rechts; een sectiekop telt mee
	// met minstens één regel eronder (nooit een wees onderaan de kolom). Past
	// het daarna nog niet, dan wijken de laatste regels voor de "+N more"-lijn.
	const cols = $derived.by(() => {
		const c: [Item[], Item[]] = [[], []];
		let ci = 0;
		let used = 0;
		let curLabel = '';
		for (let i = 0; i < items.length; i++) {
			const it = items[i];
			if (it.t === 'head') curLabel = it.label;
			const h = it.t === 'head' ? HEAD_MM : ROW_MM;
			const need = it.t === 'head' ? HEAD_MM + ROW_MM : h;
			if (used + need > CAP_MM) {
				if (ci === 0) {
					ci = 1;
					used = 0;
					// Stromen rijen mid-sectie door naar kolom 2, dan een
					// vervolg-kopje erboven — zonder kop lezen ze als zwevende
					// regels (PDF-check #344, Ambessa-lijst).
					if (it.t === 'row' && curLabel) {
						c[1].push({ t: 'head', label: `${curLabel} — cont.` });
						used += HEAD_MM;
					}
				} else {
					let n = items.slice(i).filter((x) => x.t === 'row').length;
					while (used + MORE_MM > CAP_MM) {
						const last = c[1].pop();
						if (!last) break;
						used -= last.t === 'head' ? HEAD_MM : ROW_MM;
						if (last.t === 'row') n++;
					}
					c[1].push({ t: 'more', n });
					return c;
				}
			}
			c[ci].push(it);
			used += h;
		}
		return c;
	});
</script>

<SheetFrame {bw} title="Deck Overview" sub="Decklist & matchup plans">
	<div class="sh-row">
		<span class="sh-field" style="flex: 1.6"
			><span class="micro">Deck</span><span class="fill val">{deck?.name ?? ''}</span></span
		>
		<span class="sh-field"><span class="micro">Player</span><span class="fill"></span></span>
	</div>

	<div class="cols">
		{#each cols as col, c (c)}
			<div class="col">
				{#each col as it, i (i)}
					{#if it.t === 'head'}
						<div class="dsec">{it.label}</div>
					{:else if it.t === 'row'}
						<div class="drow"><span class="dq">{it.qty}</span><span class="nm">{it.name}</span></div>
					{:else}
						<div class="more">+{it.n} more — see full list online</div>
					{/if}
				{/each}
			</div>
		{/each}
	</div>

	<div class="sec">
		<span>Matchups</span><span class="sec-note">read: F favored · E even · U unfavored</span>
	</div>
	<div class="mgrid">
		{#each [1, 2, 3] as m (m)}
			<div class="mblock">
				<span class="sh-field"><span class="micro">Vs</span><span class="fill"></span></span>
				<div class="mread">
					<span class="micro">Read</span>
					<span class="opt"><span class="cb small"></span> F</span>
					<span class="opt"><span class="cb small"></span> E</span>
					<span class="opt"><span class="cb small"></span> U</span>
				</div>
				<div class="mplan">
					<span class="micro">Plan</span>
					<span class="wl"></span>
					<span class="wl"></span>
				</div>
			</div>
		{/each}
	</div>

	<div class="sec"><span>Notes</span></div>
	<div class="notes dots"></div>
</SheetFrame>

<style>
	/* Prefill-waarde óp de invullijn — zelfde truc als op het
	   registratievel: overflow: hidden houdt de vak-baseline gelijk aan die
	   van een lege lijn. */
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

	/* Kleine sectiekop — compacter dan de frame-.sec: decklijst-dichtheid.
	   Hoogte ~5.8mm incl. marges (HEAD_MM in het script). */
	.dsec {
		font-size: 5.6pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.1em;
		border-bottom: 0.28mm solid var(--paper-ink);
		padding-bottom: 0.5mm;
		margin: 1.6mm 0 0.8mm;
	}
	.col > .dsec:first-child {
		margin-top: 0;
	}

	/* Kaartregel 3.6mm/6pt (ROW_MM); zachte lijnen — de lijst is hier
	   naslagwerk, geen invulformulier. */
	.drow {
		display: flex;
		gap: 1.4mm;
		height: 3.6mm;
		font-size: 6pt;
	}
	.dq {
		flex: none;
		width: 4.2mm;
		text-align: center;
		line-height: 3.55mm;
		border-bottom: 0.22mm solid var(--paper-line-soft);
		font-variant-numeric: tabular-nums;
	}
	.nm {
		flex: 1;
		min-width: 0;
		line-height: 3.55mm;
		white-space: nowrap;
		overflow: hidden;
		text-overflow: ellipsis;
		border-bottom: 0.22mm solid var(--paper-line-soft);
	}
	.more {
		font-size: 4.8pt;
		color: var(--paper-muted);
		letter-spacing: 0.04em;
		padding-top: 0.6mm;
	}

	/* Drie matchup-blokken naast elkaar: Vs + read (F/E/U) + twee planregels. */
	.mgrid {
		display: grid;
		grid-template-columns: repeat(3, 1fr);
		gap: 2.6mm;
	}
	.mblock {
		border: 0.3mm solid var(--paper-line);
		border-radius: 1.2mm;
		padding: 1.4mm 1.6mm;
		display: flex;
		flex-direction: column;
		gap: 1mm;
		min-width: 0;
	}
	.mread {
		display: flex;
		align-items: center;
		gap: 2mm;
	}
	.mplan {
		display: flex;
		flex-direction: column;
	}
	.wl {
		display: block;
		height: 5mm;
		border-bottom: 0.28mm solid var(--paper-line);
	}

	.notes {
		flex: 1;
		min-height: 16mm;
		margin-bottom: 1mm;
	}
</style>
