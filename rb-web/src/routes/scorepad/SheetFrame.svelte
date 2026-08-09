<script lang="ts">
	// Gedeeld kader voor elk A5-scorevel (#342): merk-kop, voettekst en het
	// complete vel-ontwerpsysteem als :global-stijlen onder .sheet. De losse
	// velcomponenten leveren alleen hun eigen indeling; alle primitieven
	// (checkboxen, schrijflijnen, tracks, sectiekoppen) leven hier zodat de
	// vellen één familie vormen. Maten in mm en pt: dit is drukwerk.
	import type { Snippet } from 'svelte';
	import PoroMark from '$lib/PoroMark.svelte';

	let {
		title,
		sub = '',
		bw = false,
		children
	}: {
		/** Veltitel rechtsboven (Engels — speltaal), bv. "Match Sheet". */
		title: string;
		/** Modusregel onder de titel, bv. "1v1 · Best of 3 · First to 8". */
		sub?: string;
		/** Zwart-wit: spelerkleuren terug naar inkt (tokenremap, geen hex). */
		bw?: boolean;
		children: Snippet;
	} = $props();
</script>

<article class="sheet" class:bw>
	<header class="sh-brand">
		<span class="sh-mark">
			<PoroMark size={22} />
			<span class="sh-word">Poracle</span>
		</span>
		<span class="sh-titles">
			<span class="sh-title">{title}</span>
			{#if sub}<span class="sh-mode">{sub}</span>{/if}
		</span>
	</header>

	{@render children()}

	<footer class="sh-foot">
		Unofficial fan-made score sheet — Poracle · riftbound-v2.bo3.dev — not affiliated with Riot
		Games
	</footer>
</article>

<style>
	/* Het vel zelf: altijd inkt-op-wit (papier-tokens, geen thema-swap) en
	   exact A5. print-color-adjust: exact dwingt Chromium de rasters en
	   kleurbalkjes ook te printen als "achtergronden" uit staat. */
	.sheet {
		width: 148mm;
		height: 210mm;
		/* Bind-randen komen als CSS-vars van de pagina (ringband-optie #342):
		   zo hoeft geen enkel velcomponent een prop door te geven. */
		padding: var(--bind-top, 8mm) 8mm 6mm var(--bind-left, 8mm);
		background: var(--paper);
		color: var(--paper-ink);
		display: flex;
		flex-direction: column;
		overflow: hidden;
		font-size: 7pt;
		line-height: 1.3;
		-webkit-print-color-adjust: exact;
		print-color-adjust: exact;
	}
	.sheet.bw {
		--paper-p1: var(--paper-ink);
		--paper-p2: var(--paper-ink);
		--paper-p1-soft: var(--paper-line-soft);
		--paper-p2-soft: var(--paper-line-soft);
	}

	/* ── Merk-kop ── */
	.sh-brand {
		display: flex;
		align-items: flex-end;
		justify-content: space-between;
		gap: 4mm;
		padding-bottom: 2mm;
		border-bottom: 0.55mm solid var(--paper-ink);
		margin-bottom: 2.6mm;
	}
	.sh-mark {
		display: inline-flex;
		align-items: center;
		gap: 1.8mm;
	}
	.sh-word {
		font-size: 11pt;
		font-weight: 800;
		letter-spacing: 0.02em;
	}
	.sh-titles {
		display: flex;
		flex-direction: column;
		align-items: flex-end;
		gap: 0.6mm;
	}
	.sh-title {
		font-size: 8.5pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.14em;
	}
	.sh-mode {
		font-size: 5.6pt;
		color: var(--paper-muted);
		letter-spacing: 0.04em;
	}

	/* ── Voettekst ── */
	.sh-foot {
		margin-top: auto;
		padding-top: 1.6mm;
		font-size: 4.8pt;
		color: var(--paper-muted);
		letter-spacing: 0.03em;
	}

	/* ═══ Gedeelde vel-primitieven (bewust :global onder .sheet zodat de
	   losse velcomponenten alleen hun indeling hoeven te leveren) ═══ */

	/* Veldrij: microlabel + invullijn(en). */
	.sheet :global(.sh-row) {
		display: flex;
		align-items: flex-end;
		gap: 3mm;
		margin-bottom: 2mm;
	}
	.sheet :global(.sh-field) {
		display: flex;
		align-items: baseline;
		gap: 1.6mm;
		flex: 1;
		min-width: 0;
	}
	/* Ook onder .pl (spelerregels): zonder deze scope was de legend-invullijn
	   daar onzichtbaar — gevonden bij de bouw van variant B (#342). */
	.sheet :global(.sh-field .fill),
	.sheet :global(.pl .fill) {
		flex: 1;
		min-width: 8mm;
		border-bottom: 0.3mm solid var(--paper-line);
		height: 4.2mm;
	}

	.sheet :global(.micro) {
		font-size: 5.2pt;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.09em;
		color: var(--paper-muted);
		white-space: nowrap;
	}

	/* Sectiekop met hairline. */
	.sheet :global(.sec) {
		display: flex;
		align-items: baseline;
		justify-content: space-between;
		gap: 3mm;
		font-size: 6.4pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.12em;
		border-bottom: 0.3mm solid var(--paper-ink);
		padding-bottom: 0.8mm;
		margin: 2.4mm 0 1.6mm;
	}
	.sheet :global(.sec .sec-note) {
		font-weight: 400;
		font-size: 5pt;
		letter-spacing: 0.04em;
		color: var(--paper-muted);
		text-transform: none;
	}

	/* Checkbox + label. */
	.sheet :global(.cb) {
		display: inline-block;
		width: 3.4mm;
		height: 3.4mm;
		flex: none;
		border: 0.32mm solid var(--paper-ink);
		border-radius: 0.7mm;
		vertical-align: -0.7mm;
	}
	.sheet :global(.cb.p1) {
		border-color: var(--paper-p1);
	}
	.sheet :global(.cb.p2) {
		border-color: var(--paper-p2);
	}
	.sheet :global(.cb.small) {
		width: 2.9mm;
		height: 2.9mm;
	}
	.sheet :global(.opt) {
		display: inline-flex;
		align-items: center;
		gap: 1.2mm;
		font-size: 6pt;
		white-space: nowrap;
	}
	.sheet :global(.optrow) {
		display: flex;
		align-items: center;
		gap: 3.4mm;
		flex-wrap: wrap;
		margin: 1.4mm 0;
	}
	.sheet :global(.optrow .micro) {
		flex: none;
	}

	/* Spelerbalk (P1 goud / P2 rood): kleurstreep + label + legend-lijn. */
	.sheet :global(.pl) {
		display: flex;
		align-items: baseline;
		gap: 1.8mm;
		flex: 1;
		min-width: 0;
		border-left: 1.4mm solid var(--paper-ink);
		padding-left: 1.8mm;
	}
	.sheet :global(.pl.p1) {
		border-left-color: var(--paper-p1);
	}
	.sheet :global(.pl.p2) {
		border-left-color: var(--paper-p2);
	}

	/* Schrijflijnen en gelinieerde boxen. */
	.sheet :global(.wline) {
		border-bottom: 0.3mm solid var(--paper-line);
		height: 5.4mm;
	}
	.sheet :global(.wbox) {
		border: 0.3mm solid var(--paper-line);
		border-radius: 1mm;
		background-image: repeating-linear-gradient(
			to bottom,
			transparent 0,
			transparent 5.7mm,
			var(--paper-line-soft) 5.7mm,
			var(--paper-line-soft) 6mm
		);
		background-origin: content-box;
		padding: 0 1.6mm;
	}

	/* Puntenraster voor notes. */
	.sheet :global(.dots) {
		background-image: radial-gradient(circle, var(--paper-line) 0.26mm, transparent 0.3mm);
		background-size: 4.8mm 4.8mm;
		background-position: 2.4mm 2.4mm;
	}
	.sheet :global(.rlines) {
		background-image: repeating-linear-gradient(
			to bottom,
			transparent 0,
			transparent 6.7mm,
			var(--paper-line) 6.7mm,
			var(--paper-line) 7mm
		);
	}

	/* Punttrack: koppen, cijfers, scheidingslijnen. De Victory Score krijgt
	   een cirkel (borders printen altijd, ook zonder achtergronden). */
	.sheet :global(.trk-h) {
		font-size: 5pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.06em;
		color: var(--paper-muted);
		text-align: center;
	}
	.sheet :global(.trk-h.c1) {
		color: var(--paper-p1);
	}
	.sheet :global(.trk-h.c2) {
		color: var(--paper-p2);
	}
	.sheet :global(.trk-num) {
		font-variant-numeric: tabular-nums;
		font-size: 6pt;
		font-weight: 700;
		text-align: center;
		align-self: center;
		justify-self: center;
		width: 3.6mm;
		line-height: 3.6mm;
	}
	.sheet :global(.trk-num.vs) {
		border: 0.35mm solid var(--paper-ink);
		border-radius: 50%;
		width: 3.6mm;
		height: 3.6mm;
	}
</style>
