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
		Unofficial fan-made score sheet — Poracle · poracle.nl — not affiliated with Riot
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
		--paper-p1-ink: var(--paper-ink);
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
		/* 5.6pt als vloer: 5.2pt bleek op papier op de grens van leesbaar. */
		font-size: 5.6pt;
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

	/* Schrijflijnen en gelinieerde boxen. De liniatuur is hier bewust GEEN
	   CSS-gradient: Chromiums print-pad rastert gradient-achtergronden naar
	   72 dpi (elke vijfde 7mm-regel viel weg, wbox-regels vervaagden, dots
	   werden ongelijke blobs). Borders en blurloze box-shadow-kopieën
	   printen wél als vector (gemeten: 0 Image XObjects in de PDF), dus
	   elke liniatuur is nu één ::before-lijn plus een schaduwstapel die de
	   volledige A5-binnenhoogte dekt; overflow: hidden op de drager knipt
	   het restant weg, zodat afnemers kale divs met vrije hoogte blijven.
	   Kleur via currentColor zodat de stapel zelf kleurloos is (tokens,
	   geen hex). Pitch wijzigen = stapel opnieuw genereren, bv.:
	   python3 -c "print(', '.join(f'0 {7*n:g}mm' for n in range(1,28)))" */
	.sheet :global(.wline) {
		border-bottom: 0.3mm solid var(--paper-line);
		/* 6mm: zelfde schrijfritme als de wbox-liniatuur. */
		height: 6mm;
	}
	.sheet :global(.wbox) {
		border: 0.3mm solid var(--paper-line);
		border-radius: 1mm;
		padding: 0 1.6mm;
		position: relative;
		overflow: hidden;
	}
	.sheet :global(.wbox::before) {
		content: '';
		position: absolute;
		/* Eerste regel op 5.7mm, daarna 6mm-pitch (n = 1..32); de 1.6mm
		   inzet spiegelt de wbox-padding (content-box, zoals voorheen). */
		top: 5.7mm;
		left: 1.6mm;
		right: 1.6mm;
		height: 0.3mm;
		color: var(--paper-line-soft);
		background: currentColor;
		box-shadow:
			0 6mm, 0 12mm, 0 18mm, 0 24mm, 0 30mm, 0 36mm, 0 42mm, 0 48mm, 0 54mm, 0 60mm,
			0 66mm, 0 72mm, 0 78mm, 0 84mm, 0 90mm, 0 96mm, 0 102mm, 0 108mm, 0 114mm, 0 120mm,
			0 126mm, 0 132mm, 0 138mm, 0 144mm, 0 150mm, 0 156mm, 0 162mm, 0 168mm, 0 174mm,
			0 180mm, 0 186mm, 0 192mm;
	}

	/* Puntenraster voor notes: één 0.55mm-dot, de rest is schaduwkopie op
	   het 4.8mm-raster (28 kolommen x 41 rijen dekt de A5-binnenmaat;
	   gegenereerd — zie de regeneratie-hint bij de liniatuur hierboven). */
	.sheet :global(.dots) {
		position: relative;
		overflow: hidden;
	}
	.sheet :global(.dots::before) {
		content: '';
		position: absolute;
		/* Dot-middelpunt op (2.4mm, 2.4mm): 2.4 - 0.55 / 2 = 2.125. */
		top: 2.125mm;
		left: 2.125mm;
		width: 0.55mm;
		height: 0.55mm;
		border-radius: 50%;
		color: var(--paper-line);
		background: currentColor;
		box-shadow:
			4.8mm 0, 9.6mm 0, 14.4mm 0, 19.2mm 0, 24mm 0, 28.8mm 0, 33.6mm 0, 38.4mm 0, 43.2mm 0,
			48mm 0, 52.8mm 0, 57.6mm 0, 62.4mm 0, 67.2mm 0, 72mm 0, 76.8mm 0, 81.6mm 0, 86.4mm 0,
			91.2mm 0, 96mm 0, 100.8mm 0, 105.6mm 0, 110.4mm 0, 115.2mm 0, 120mm 0, 124.8mm 0,
			129.6mm 0, 0 4.8mm, 4.8mm 4.8mm, 9.6mm 4.8mm, 14.4mm 4.8mm, 19.2mm 4.8mm, 24mm 4.8mm,
			28.8mm 4.8mm, 33.6mm 4.8mm, 38.4mm 4.8mm, 43.2mm 4.8mm, 48mm 4.8mm, 52.8mm 4.8mm,
			57.6mm 4.8mm, 62.4mm 4.8mm, 67.2mm 4.8mm, 72mm 4.8mm, 76.8mm 4.8mm, 81.6mm 4.8mm,
			86.4mm 4.8mm, 91.2mm 4.8mm, 96mm 4.8mm, 100.8mm 4.8mm, 105.6mm 4.8mm, 110.4mm 4.8mm,
			115.2mm 4.8mm, 120mm 4.8mm, 124.8mm 4.8mm, 129.6mm 4.8mm, 0 9.6mm, 4.8mm 9.6mm,
			9.6mm 9.6mm, 14.4mm 9.6mm, 19.2mm 9.6mm, 24mm 9.6mm, 28.8mm 9.6mm, 33.6mm 9.6mm,
			38.4mm 9.6mm, 43.2mm 9.6mm, 48mm 9.6mm, 52.8mm 9.6mm, 57.6mm 9.6mm, 62.4mm 9.6mm,
			67.2mm 9.6mm, 72mm 9.6mm, 76.8mm 9.6mm, 81.6mm 9.6mm, 86.4mm 9.6mm, 91.2mm 9.6mm,
			96mm 9.6mm, 100.8mm 9.6mm, 105.6mm 9.6mm, 110.4mm 9.6mm, 115.2mm 9.6mm, 120mm 9.6mm,
			124.8mm 9.6mm, 129.6mm 9.6mm, 0 14.4mm, 4.8mm 14.4mm, 9.6mm 14.4mm, 14.4mm 14.4mm,
			19.2mm 14.4mm, 24mm 14.4mm, 28.8mm 14.4mm, 33.6mm 14.4mm, 38.4mm 14.4mm,
			43.2mm 14.4mm, 48mm 14.4mm, 52.8mm 14.4mm, 57.6mm 14.4mm, 62.4mm 14.4mm,
			67.2mm 14.4mm, 72mm 14.4mm, 76.8mm 14.4mm, 81.6mm 14.4mm, 86.4mm 14.4mm,
			91.2mm 14.4mm, 96mm 14.4mm, 100.8mm 14.4mm, 105.6mm 14.4mm, 110.4mm 14.4mm,
			115.2mm 14.4mm, 120mm 14.4mm, 124.8mm 14.4mm, 129.6mm 14.4mm, 0 19.2mm, 4.8mm 19.2mm,
			9.6mm 19.2mm, 14.4mm 19.2mm, 19.2mm 19.2mm, 24mm 19.2mm, 28.8mm 19.2mm,
			33.6mm 19.2mm, 38.4mm 19.2mm, 43.2mm 19.2mm, 48mm 19.2mm, 52.8mm 19.2mm,
			57.6mm 19.2mm, 62.4mm 19.2mm, 67.2mm 19.2mm, 72mm 19.2mm, 76.8mm 19.2mm,
			81.6mm 19.2mm, 86.4mm 19.2mm, 91.2mm 19.2mm, 96mm 19.2mm, 100.8mm 19.2mm,
			105.6mm 19.2mm, 110.4mm 19.2mm, 115.2mm 19.2mm, 120mm 19.2mm, 124.8mm 19.2mm,
			129.6mm 19.2mm, 0 24mm, 4.8mm 24mm, 9.6mm 24mm, 14.4mm 24mm, 19.2mm 24mm, 24mm 24mm,
			28.8mm 24mm, 33.6mm 24mm, 38.4mm 24mm, 43.2mm 24mm, 48mm 24mm, 52.8mm 24mm,
			57.6mm 24mm, 62.4mm 24mm, 67.2mm 24mm, 72mm 24mm, 76.8mm 24mm, 81.6mm 24mm,
			86.4mm 24mm, 91.2mm 24mm, 96mm 24mm, 100.8mm 24mm, 105.6mm 24mm, 110.4mm 24mm,
			115.2mm 24mm, 120mm 24mm, 124.8mm 24mm, 129.6mm 24mm, 0 28.8mm, 4.8mm 28.8mm,
			9.6mm 28.8mm, 14.4mm 28.8mm, 19.2mm 28.8mm, 24mm 28.8mm, 28.8mm 28.8mm,
			33.6mm 28.8mm, 38.4mm 28.8mm, 43.2mm 28.8mm, 48mm 28.8mm, 52.8mm 28.8mm,
			57.6mm 28.8mm, 62.4mm 28.8mm, 67.2mm 28.8mm, 72mm 28.8mm, 76.8mm 28.8mm,
			81.6mm 28.8mm, 86.4mm 28.8mm, 91.2mm 28.8mm, 96mm 28.8mm, 100.8mm 28.8mm,
			105.6mm 28.8mm, 110.4mm 28.8mm, 115.2mm 28.8mm, 120mm 28.8mm, 124.8mm 28.8mm,
			129.6mm 28.8mm, 0 33.6mm, 4.8mm 33.6mm, 9.6mm 33.6mm, 14.4mm 33.6mm, 19.2mm 33.6mm,
			24mm 33.6mm, 28.8mm 33.6mm, 33.6mm 33.6mm, 38.4mm 33.6mm, 43.2mm 33.6mm, 48mm 33.6mm,
			52.8mm 33.6mm, 57.6mm 33.6mm, 62.4mm 33.6mm, 67.2mm 33.6mm, 72mm 33.6mm,
			76.8mm 33.6mm, 81.6mm 33.6mm, 86.4mm 33.6mm, 91.2mm 33.6mm, 96mm 33.6mm,
			100.8mm 33.6mm, 105.6mm 33.6mm, 110.4mm 33.6mm, 115.2mm 33.6mm, 120mm 33.6mm,
			124.8mm 33.6mm, 129.6mm 33.6mm, 0 38.4mm, 4.8mm 38.4mm, 9.6mm 38.4mm, 14.4mm 38.4mm,
			19.2mm 38.4mm, 24mm 38.4mm, 28.8mm 38.4mm, 33.6mm 38.4mm, 38.4mm 38.4mm,
			43.2mm 38.4mm, 48mm 38.4mm, 52.8mm 38.4mm, 57.6mm 38.4mm, 62.4mm 38.4mm,
			67.2mm 38.4mm, 72mm 38.4mm, 76.8mm 38.4mm, 81.6mm 38.4mm, 86.4mm 38.4mm,
			91.2mm 38.4mm, 96mm 38.4mm, 100.8mm 38.4mm, 105.6mm 38.4mm, 110.4mm 38.4mm,
			115.2mm 38.4mm, 120mm 38.4mm, 124.8mm 38.4mm, 129.6mm 38.4mm, 0 43.2mm, 4.8mm 43.2mm,
			9.6mm 43.2mm, 14.4mm 43.2mm, 19.2mm 43.2mm, 24mm 43.2mm, 28.8mm 43.2mm,
			33.6mm 43.2mm, 38.4mm 43.2mm, 43.2mm 43.2mm, 48mm 43.2mm, 52.8mm 43.2mm,
			57.6mm 43.2mm, 62.4mm 43.2mm, 67.2mm 43.2mm, 72mm 43.2mm, 76.8mm 43.2mm,
			81.6mm 43.2mm, 86.4mm 43.2mm, 91.2mm 43.2mm, 96mm 43.2mm, 100.8mm 43.2mm,
			105.6mm 43.2mm, 110.4mm 43.2mm, 115.2mm 43.2mm, 120mm 43.2mm, 124.8mm 43.2mm,
			129.6mm 43.2mm, 0 48mm, 4.8mm 48mm, 9.6mm 48mm, 14.4mm 48mm, 19.2mm 48mm, 24mm 48mm,
			28.8mm 48mm, 33.6mm 48mm, 38.4mm 48mm, 43.2mm 48mm, 48mm 48mm, 52.8mm 48mm,
			57.6mm 48mm, 62.4mm 48mm, 67.2mm 48mm, 72mm 48mm, 76.8mm 48mm, 81.6mm 48mm,
			86.4mm 48mm, 91.2mm 48mm, 96mm 48mm, 100.8mm 48mm, 105.6mm 48mm, 110.4mm 48mm,
			115.2mm 48mm, 120mm 48mm, 124.8mm 48mm, 129.6mm 48mm, 0 52.8mm, 4.8mm 52.8mm,
			9.6mm 52.8mm, 14.4mm 52.8mm, 19.2mm 52.8mm, 24mm 52.8mm, 28.8mm 52.8mm,
			33.6mm 52.8mm, 38.4mm 52.8mm, 43.2mm 52.8mm, 48mm 52.8mm, 52.8mm 52.8mm,
			57.6mm 52.8mm, 62.4mm 52.8mm, 67.2mm 52.8mm, 72mm 52.8mm, 76.8mm 52.8mm,
			81.6mm 52.8mm, 86.4mm 52.8mm, 91.2mm 52.8mm, 96mm 52.8mm, 100.8mm 52.8mm,
			105.6mm 52.8mm, 110.4mm 52.8mm, 115.2mm 52.8mm, 120mm 52.8mm, 124.8mm 52.8mm,
			129.6mm 52.8mm, 0 57.6mm, 4.8mm 57.6mm, 9.6mm 57.6mm, 14.4mm 57.6mm, 19.2mm 57.6mm,
			24mm 57.6mm, 28.8mm 57.6mm, 33.6mm 57.6mm, 38.4mm 57.6mm, 43.2mm 57.6mm, 48mm 57.6mm,
			52.8mm 57.6mm, 57.6mm 57.6mm, 62.4mm 57.6mm, 67.2mm 57.6mm, 72mm 57.6mm,
			76.8mm 57.6mm, 81.6mm 57.6mm, 86.4mm 57.6mm, 91.2mm 57.6mm, 96mm 57.6mm,
			100.8mm 57.6mm, 105.6mm 57.6mm, 110.4mm 57.6mm, 115.2mm 57.6mm, 120mm 57.6mm,
			124.8mm 57.6mm, 129.6mm 57.6mm, 0 62.4mm, 4.8mm 62.4mm, 9.6mm 62.4mm, 14.4mm 62.4mm,
			19.2mm 62.4mm, 24mm 62.4mm, 28.8mm 62.4mm, 33.6mm 62.4mm, 38.4mm 62.4mm,
			43.2mm 62.4mm, 48mm 62.4mm, 52.8mm 62.4mm, 57.6mm 62.4mm, 62.4mm 62.4mm,
			67.2mm 62.4mm, 72mm 62.4mm, 76.8mm 62.4mm, 81.6mm 62.4mm, 86.4mm 62.4mm,
			91.2mm 62.4mm, 96mm 62.4mm, 100.8mm 62.4mm, 105.6mm 62.4mm, 110.4mm 62.4mm,
			115.2mm 62.4mm, 120mm 62.4mm, 124.8mm 62.4mm, 129.6mm 62.4mm, 0 67.2mm, 4.8mm 67.2mm,
			9.6mm 67.2mm, 14.4mm 67.2mm, 19.2mm 67.2mm, 24mm 67.2mm, 28.8mm 67.2mm,
			33.6mm 67.2mm, 38.4mm 67.2mm, 43.2mm 67.2mm, 48mm 67.2mm, 52.8mm 67.2mm,
			57.6mm 67.2mm, 62.4mm 67.2mm, 67.2mm 67.2mm, 72mm 67.2mm, 76.8mm 67.2mm,
			81.6mm 67.2mm, 86.4mm 67.2mm, 91.2mm 67.2mm, 96mm 67.2mm, 100.8mm 67.2mm,
			105.6mm 67.2mm, 110.4mm 67.2mm, 115.2mm 67.2mm, 120mm 67.2mm, 124.8mm 67.2mm,
			129.6mm 67.2mm, 0 72mm, 4.8mm 72mm, 9.6mm 72mm, 14.4mm 72mm, 19.2mm 72mm, 24mm 72mm,
			28.8mm 72mm, 33.6mm 72mm, 38.4mm 72mm, 43.2mm 72mm, 48mm 72mm, 52.8mm 72mm,
			57.6mm 72mm, 62.4mm 72mm, 67.2mm 72mm, 72mm 72mm, 76.8mm 72mm, 81.6mm 72mm,
			86.4mm 72mm, 91.2mm 72mm, 96mm 72mm, 100.8mm 72mm, 105.6mm 72mm, 110.4mm 72mm,
			115.2mm 72mm, 120mm 72mm, 124.8mm 72mm, 129.6mm 72mm, 0 76.8mm, 4.8mm 76.8mm,
			9.6mm 76.8mm, 14.4mm 76.8mm, 19.2mm 76.8mm, 24mm 76.8mm, 28.8mm 76.8mm,
			33.6mm 76.8mm, 38.4mm 76.8mm, 43.2mm 76.8mm, 48mm 76.8mm, 52.8mm 76.8mm,
			57.6mm 76.8mm, 62.4mm 76.8mm, 67.2mm 76.8mm, 72mm 76.8mm, 76.8mm 76.8mm,
			81.6mm 76.8mm, 86.4mm 76.8mm, 91.2mm 76.8mm, 96mm 76.8mm, 100.8mm 76.8mm,
			105.6mm 76.8mm, 110.4mm 76.8mm, 115.2mm 76.8mm, 120mm 76.8mm, 124.8mm 76.8mm,
			129.6mm 76.8mm, 0 81.6mm, 4.8mm 81.6mm, 9.6mm 81.6mm, 14.4mm 81.6mm, 19.2mm 81.6mm,
			24mm 81.6mm, 28.8mm 81.6mm, 33.6mm 81.6mm, 38.4mm 81.6mm, 43.2mm 81.6mm, 48mm 81.6mm,
			52.8mm 81.6mm, 57.6mm 81.6mm, 62.4mm 81.6mm, 67.2mm 81.6mm, 72mm 81.6mm,
			76.8mm 81.6mm, 81.6mm 81.6mm, 86.4mm 81.6mm, 91.2mm 81.6mm, 96mm 81.6mm,
			100.8mm 81.6mm, 105.6mm 81.6mm, 110.4mm 81.6mm, 115.2mm 81.6mm, 120mm 81.6mm,
			124.8mm 81.6mm, 129.6mm 81.6mm, 0 86.4mm, 4.8mm 86.4mm, 9.6mm 86.4mm, 14.4mm 86.4mm,
			19.2mm 86.4mm, 24mm 86.4mm, 28.8mm 86.4mm, 33.6mm 86.4mm, 38.4mm 86.4mm,
			43.2mm 86.4mm, 48mm 86.4mm, 52.8mm 86.4mm, 57.6mm 86.4mm, 62.4mm 86.4mm,
			67.2mm 86.4mm, 72mm 86.4mm, 76.8mm 86.4mm, 81.6mm 86.4mm, 86.4mm 86.4mm,
			91.2mm 86.4mm, 96mm 86.4mm, 100.8mm 86.4mm, 105.6mm 86.4mm, 110.4mm 86.4mm,
			115.2mm 86.4mm, 120mm 86.4mm, 124.8mm 86.4mm, 129.6mm 86.4mm, 0 91.2mm, 4.8mm 91.2mm,
			9.6mm 91.2mm, 14.4mm 91.2mm, 19.2mm 91.2mm, 24mm 91.2mm, 28.8mm 91.2mm,
			33.6mm 91.2mm, 38.4mm 91.2mm, 43.2mm 91.2mm, 48mm 91.2mm, 52.8mm 91.2mm,
			57.6mm 91.2mm, 62.4mm 91.2mm, 67.2mm 91.2mm, 72mm 91.2mm, 76.8mm 91.2mm,
			81.6mm 91.2mm, 86.4mm 91.2mm, 91.2mm 91.2mm, 96mm 91.2mm, 100.8mm 91.2mm,
			105.6mm 91.2mm, 110.4mm 91.2mm, 115.2mm 91.2mm, 120mm 91.2mm, 124.8mm 91.2mm,
			129.6mm 91.2mm, 0 96mm, 4.8mm 96mm, 9.6mm 96mm, 14.4mm 96mm, 19.2mm 96mm, 24mm 96mm,
			28.8mm 96mm, 33.6mm 96mm, 38.4mm 96mm, 43.2mm 96mm, 48mm 96mm, 52.8mm 96mm,
			57.6mm 96mm, 62.4mm 96mm, 67.2mm 96mm, 72mm 96mm, 76.8mm 96mm, 81.6mm 96mm,
			86.4mm 96mm, 91.2mm 96mm, 96mm 96mm, 100.8mm 96mm, 105.6mm 96mm, 110.4mm 96mm,
			115.2mm 96mm, 120mm 96mm, 124.8mm 96mm, 129.6mm 96mm, 0 100.8mm, 4.8mm 100.8mm,
			9.6mm 100.8mm, 14.4mm 100.8mm, 19.2mm 100.8mm, 24mm 100.8mm, 28.8mm 100.8mm,
			33.6mm 100.8mm, 38.4mm 100.8mm, 43.2mm 100.8mm, 48mm 100.8mm, 52.8mm 100.8mm,
			57.6mm 100.8mm, 62.4mm 100.8mm, 67.2mm 100.8mm, 72mm 100.8mm, 76.8mm 100.8mm,
			81.6mm 100.8mm, 86.4mm 100.8mm, 91.2mm 100.8mm, 96mm 100.8mm, 100.8mm 100.8mm,
			105.6mm 100.8mm, 110.4mm 100.8mm, 115.2mm 100.8mm, 120mm 100.8mm, 124.8mm 100.8mm,
			129.6mm 100.8mm, 0 105.6mm, 4.8mm 105.6mm, 9.6mm 105.6mm, 14.4mm 105.6mm,
			19.2mm 105.6mm, 24mm 105.6mm, 28.8mm 105.6mm, 33.6mm 105.6mm, 38.4mm 105.6mm,
			43.2mm 105.6mm, 48mm 105.6mm, 52.8mm 105.6mm, 57.6mm 105.6mm, 62.4mm 105.6mm,
			67.2mm 105.6mm, 72mm 105.6mm, 76.8mm 105.6mm, 81.6mm 105.6mm, 86.4mm 105.6mm,
			91.2mm 105.6mm, 96mm 105.6mm, 100.8mm 105.6mm, 105.6mm 105.6mm, 110.4mm 105.6mm,
			115.2mm 105.6mm, 120mm 105.6mm, 124.8mm 105.6mm, 129.6mm 105.6mm, 0 110.4mm,
			4.8mm 110.4mm, 9.6mm 110.4mm, 14.4mm 110.4mm, 19.2mm 110.4mm, 24mm 110.4mm,
			28.8mm 110.4mm, 33.6mm 110.4mm, 38.4mm 110.4mm, 43.2mm 110.4mm, 48mm 110.4mm,
			52.8mm 110.4mm, 57.6mm 110.4mm, 62.4mm 110.4mm, 67.2mm 110.4mm, 72mm 110.4mm,
			76.8mm 110.4mm, 81.6mm 110.4mm, 86.4mm 110.4mm, 91.2mm 110.4mm, 96mm 110.4mm,
			100.8mm 110.4mm, 105.6mm 110.4mm, 110.4mm 110.4mm, 115.2mm 110.4mm, 120mm 110.4mm,
			124.8mm 110.4mm, 129.6mm 110.4mm, 0 115.2mm, 4.8mm 115.2mm, 9.6mm 115.2mm,
			14.4mm 115.2mm, 19.2mm 115.2mm, 24mm 115.2mm, 28.8mm 115.2mm, 33.6mm 115.2mm,
			38.4mm 115.2mm, 43.2mm 115.2mm, 48mm 115.2mm, 52.8mm 115.2mm, 57.6mm 115.2mm,
			62.4mm 115.2mm, 67.2mm 115.2mm, 72mm 115.2mm, 76.8mm 115.2mm, 81.6mm 115.2mm,
			86.4mm 115.2mm, 91.2mm 115.2mm, 96mm 115.2mm, 100.8mm 115.2mm, 105.6mm 115.2mm,
			110.4mm 115.2mm, 115.2mm 115.2mm, 120mm 115.2mm, 124.8mm 115.2mm, 129.6mm 115.2mm,
			0 120mm, 4.8mm 120mm, 9.6mm 120mm, 14.4mm 120mm, 19.2mm 120mm, 24mm 120mm,
			28.8mm 120mm, 33.6mm 120mm, 38.4mm 120mm, 43.2mm 120mm, 48mm 120mm, 52.8mm 120mm,
			57.6mm 120mm, 62.4mm 120mm, 67.2mm 120mm, 72mm 120mm, 76.8mm 120mm, 81.6mm 120mm,
			86.4mm 120mm, 91.2mm 120mm, 96mm 120mm, 100.8mm 120mm, 105.6mm 120mm, 110.4mm 120mm,
			115.2mm 120mm, 120mm 120mm, 124.8mm 120mm, 129.6mm 120mm, 0 124.8mm, 4.8mm 124.8mm,
			9.6mm 124.8mm, 14.4mm 124.8mm, 19.2mm 124.8mm, 24mm 124.8mm, 28.8mm 124.8mm,
			33.6mm 124.8mm, 38.4mm 124.8mm, 43.2mm 124.8mm, 48mm 124.8mm, 52.8mm 124.8mm,
			57.6mm 124.8mm, 62.4mm 124.8mm, 67.2mm 124.8mm, 72mm 124.8mm, 76.8mm 124.8mm,
			81.6mm 124.8mm, 86.4mm 124.8mm, 91.2mm 124.8mm, 96mm 124.8mm, 100.8mm 124.8mm,
			105.6mm 124.8mm, 110.4mm 124.8mm, 115.2mm 124.8mm, 120mm 124.8mm, 124.8mm 124.8mm,
			129.6mm 124.8mm, 0 129.6mm, 4.8mm 129.6mm, 9.6mm 129.6mm, 14.4mm 129.6mm,
			19.2mm 129.6mm, 24mm 129.6mm, 28.8mm 129.6mm, 33.6mm 129.6mm, 38.4mm 129.6mm,
			43.2mm 129.6mm, 48mm 129.6mm, 52.8mm 129.6mm, 57.6mm 129.6mm, 62.4mm 129.6mm,
			67.2mm 129.6mm, 72mm 129.6mm, 76.8mm 129.6mm, 81.6mm 129.6mm, 86.4mm 129.6mm,
			91.2mm 129.6mm, 96mm 129.6mm, 100.8mm 129.6mm, 105.6mm 129.6mm, 110.4mm 129.6mm,
			115.2mm 129.6mm, 120mm 129.6mm, 124.8mm 129.6mm, 129.6mm 129.6mm, 0 134.4mm,
			4.8mm 134.4mm, 9.6mm 134.4mm, 14.4mm 134.4mm, 19.2mm 134.4mm, 24mm 134.4mm,
			28.8mm 134.4mm, 33.6mm 134.4mm, 38.4mm 134.4mm, 43.2mm 134.4mm, 48mm 134.4mm,
			52.8mm 134.4mm, 57.6mm 134.4mm, 62.4mm 134.4mm, 67.2mm 134.4mm, 72mm 134.4mm,
			76.8mm 134.4mm, 81.6mm 134.4mm, 86.4mm 134.4mm, 91.2mm 134.4mm, 96mm 134.4mm,
			100.8mm 134.4mm, 105.6mm 134.4mm, 110.4mm 134.4mm, 115.2mm 134.4mm, 120mm 134.4mm,
			124.8mm 134.4mm, 129.6mm 134.4mm, 0 139.2mm, 4.8mm 139.2mm, 9.6mm 139.2mm,
			14.4mm 139.2mm, 19.2mm 139.2mm, 24mm 139.2mm, 28.8mm 139.2mm, 33.6mm 139.2mm,
			38.4mm 139.2mm, 43.2mm 139.2mm, 48mm 139.2mm, 52.8mm 139.2mm, 57.6mm 139.2mm,
			62.4mm 139.2mm, 67.2mm 139.2mm, 72mm 139.2mm, 76.8mm 139.2mm, 81.6mm 139.2mm,
			86.4mm 139.2mm, 91.2mm 139.2mm, 96mm 139.2mm, 100.8mm 139.2mm, 105.6mm 139.2mm,
			110.4mm 139.2mm, 115.2mm 139.2mm, 120mm 139.2mm, 124.8mm 139.2mm, 129.6mm 139.2mm,
			0 144mm, 4.8mm 144mm, 9.6mm 144mm, 14.4mm 144mm, 19.2mm 144mm, 24mm 144mm,
			28.8mm 144mm, 33.6mm 144mm, 38.4mm 144mm, 43.2mm 144mm, 48mm 144mm, 52.8mm 144mm,
			57.6mm 144mm, 62.4mm 144mm, 67.2mm 144mm, 72mm 144mm, 76.8mm 144mm, 81.6mm 144mm,
			86.4mm 144mm, 91.2mm 144mm, 96mm 144mm, 100.8mm 144mm, 105.6mm 144mm, 110.4mm 144mm,
			115.2mm 144mm, 120mm 144mm, 124.8mm 144mm, 129.6mm 144mm, 0 148.8mm, 4.8mm 148.8mm,
			9.6mm 148.8mm, 14.4mm 148.8mm, 19.2mm 148.8mm, 24mm 148.8mm, 28.8mm 148.8mm,
			33.6mm 148.8mm, 38.4mm 148.8mm, 43.2mm 148.8mm, 48mm 148.8mm, 52.8mm 148.8mm,
			57.6mm 148.8mm, 62.4mm 148.8mm, 67.2mm 148.8mm, 72mm 148.8mm, 76.8mm 148.8mm,
			81.6mm 148.8mm, 86.4mm 148.8mm, 91.2mm 148.8mm, 96mm 148.8mm, 100.8mm 148.8mm,
			105.6mm 148.8mm, 110.4mm 148.8mm, 115.2mm 148.8mm, 120mm 148.8mm, 124.8mm 148.8mm,
			129.6mm 148.8mm, 0 153.6mm, 4.8mm 153.6mm, 9.6mm 153.6mm, 14.4mm 153.6mm,
			19.2mm 153.6mm, 24mm 153.6mm, 28.8mm 153.6mm, 33.6mm 153.6mm, 38.4mm 153.6mm,
			43.2mm 153.6mm, 48mm 153.6mm, 52.8mm 153.6mm, 57.6mm 153.6mm, 62.4mm 153.6mm,
			67.2mm 153.6mm, 72mm 153.6mm, 76.8mm 153.6mm, 81.6mm 153.6mm, 86.4mm 153.6mm,
			91.2mm 153.6mm, 96mm 153.6mm, 100.8mm 153.6mm, 105.6mm 153.6mm, 110.4mm 153.6mm,
			115.2mm 153.6mm, 120mm 153.6mm, 124.8mm 153.6mm, 129.6mm 153.6mm, 0 158.4mm,
			4.8mm 158.4mm, 9.6mm 158.4mm, 14.4mm 158.4mm, 19.2mm 158.4mm, 24mm 158.4mm,
			28.8mm 158.4mm, 33.6mm 158.4mm, 38.4mm 158.4mm, 43.2mm 158.4mm, 48mm 158.4mm,
			52.8mm 158.4mm, 57.6mm 158.4mm, 62.4mm 158.4mm, 67.2mm 158.4mm, 72mm 158.4mm,
			76.8mm 158.4mm, 81.6mm 158.4mm, 86.4mm 158.4mm, 91.2mm 158.4mm, 96mm 158.4mm,
			100.8mm 158.4mm, 105.6mm 158.4mm, 110.4mm 158.4mm, 115.2mm 158.4mm, 120mm 158.4mm,
			124.8mm 158.4mm, 129.6mm 158.4mm, 0 163.2mm, 4.8mm 163.2mm, 9.6mm 163.2mm,
			14.4mm 163.2mm, 19.2mm 163.2mm, 24mm 163.2mm, 28.8mm 163.2mm, 33.6mm 163.2mm,
			38.4mm 163.2mm, 43.2mm 163.2mm, 48mm 163.2mm, 52.8mm 163.2mm, 57.6mm 163.2mm,
			62.4mm 163.2mm, 67.2mm 163.2mm, 72mm 163.2mm, 76.8mm 163.2mm, 81.6mm 163.2mm,
			86.4mm 163.2mm, 91.2mm 163.2mm, 96mm 163.2mm, 100.8mm 163.2mm, 105.6mm 163.2mm,
			110.4mm 163.2mm, 115.2mm 163.2mm, 120mm 163.2mm, 124.8mm 163.2mm, 129.6mm 163.2mm,
			0 168mm, 4.8mm 168mm, 9.6mm 168mm, 14.4mm 168mm, 19.2mm 168mm, 24mm 168mm,
			28.8mm 168mm, 33.6mm 168mm, 38.4mm 168mm, 43.2mm 168mm, 48mm 168mm, 52.8mm 168mm,
			57.6mm 168mm, 62.4mm 168mm, 67.2mm 168mm, 72mm 168mm, 76.8mm 168mm, 81.6mm 168mm,
			86.4mm 168mm, 91.2mm 168mm, 96mm 168mm, 100.8mm 168mm, 105.6mm 168mm, 110.4mm 168mm,
			115.2mm 168mm, 120mm 168mm, 124.8mm 168mm, 129.6mm 168mm, 0 172.8mm, 4.8mm 172.8mm,
			9.6mm 172.8mm, 14.4mm 172.8mm, 19.2mm 172.8mm, 24mm 172.8mm, 28.8mm 172.8mm,
			33.6mm 172.8mm, 38.4mm 172.8mm, 43.2mm 172.8mm, 48mm 172.8mm, 52.8mm 172.8mm,
			57.6mm 172.8mm, 62.4mm 172.8mm, 67.2mm 172.8mm, 72mm 172.8mm, 76.8mm 172.8mm,
			81.6mm 172.8mm, 86.4mm 172.8mm, 91.2mm 172.8mm, 96mm 172.8mm, 100.8mm 172.8mm,
			105.6mm 172.8mm, 110.4mm 172.8mm, 115.2mm 172.8mm, 120mm 172.8mm, 124.8mm 172.8mm,
			129.6mm 172.8mm, 0 177.6mm, 4.8mm 177.6mm, 9.6mm 177.6mm, 14.4mm 177.6mm,
			19.2mm 177.6mm, 24mm 177.6mm, 28.8mm 177.6mm, 33.6mm 177.6mm, 38.4mm 177.6mm,
			43.2mm 177.6mm, 48mm 177.6mm, 52.8mm 177.6mm, 57.6mm 177.6mm, 62.4mm 177.6mm,
			67.2mm 177.6mm, 72mm 177.6mm, 76.8mm 177.6mm, 81.6mm 177.6mm, 86.4mm 177.6mm,
			91.2mm 177.6mm, 96mm 177.6mm, 100.8mm 177.6mm, 105.6mm 177.6mm, 110.4mm 177.6mm,
			115.2mm 177.6mm, 120mm 177.6mm, 124.8mm 177.6mm, 129.6mm 177.6mm, 0 182.4mm,
			4.8mm 182.4mm, 9.6mm 182.4mm, 14.4mm 182.4mm, 19.2mm 182.4mm, 24mm 182.4mm,
			28.8mm 182.4mm, 33.6mm 182.4mm, 38.4mm 182.4mm, 43.2mm 182.4mm, 48mm 182.4mm,
			52.8mm 182.4mm, 57.6mm 182.4mm, 62.4mm 182.4mm, 67.2mm 182.4mm, 72mm 182.4mm,
			76.8mm 182.4mm, 81.6mm 182.4mm, 86.4mm 182.4mm, 91.2mm 182.4mm, 96mm 182.4mm,
			100.8mm 182.4mm, 105.6mm 182.4mm, 110.4mm 182.4mm, 115.2mm 182.4mm, 120mm 182.4mm,
			124.8mm 182.4mm, 129.6mm 182.4mm, 0 187.2mm, 4.8mm 187.2mm, 9.6mm 187.2mm,
			14.4mm 187.2mm, 19.2mm 187.2mm, 24mm 187.2mm, 28.8mm 187.2mm, 33.6mm 187.2mm,
			38.4mm 187.2mm, 43.2mm 187.2mm, 48mm 187.2mm, 52.8mm 187.2mm, 57.6mm 187.2mm,
			62.4mm 187.2mm, 67.2mm 187.2mm, 72mm 187.2mm, 76.8mm 187.2mm, 81.6mm 187.2mm,
			86.4mm 187.2mm, 91.2mm 187.2mm, 96mm 187.2mm, 100.8mm 187.2mm, 105.6mm 187.2mm,
			110.4mm 187.2mm, 115.2mm 187.2mm, 120mm 187.2mm, 124.8mm 187.2mm, 129.6mm 187.2mm,
			0 192mm, 4.8mm 192mm, 9.6mm 192mm, 14.4mm 192mm, 19.2mm 192mm, 24mm 192mm,
			28.8mm 192mm, 33.6mm 192mm, 38.4mm 192mm, 43.2mm 192mm, 48mm 192mm, 52.8mm 192mm,
			57.6mm 192mm, 62.4mm 192mm, 67.2mm 192mm, 72mm 192mm, 76.8mm 192mm, 81.6mm 192mm,
			86.4mm 192mm, 91.2mm 192mm, 96mm 192mm, 100.8mm 192mm, 105.6mm 192mm, 110.4mm 192mm,
			115.2mm 192mm, 120mm 192mm, 124.8mm 192mm, 129.6mm 192mm;
	}
	.sheet :global(.rlines) {
		position: relative;
		overflow: hidden;
	}
	.sheet :global(.rlines::before) {
		content: '';
		position: absolute;
		/* Eerste regel op 6.7mm, daarna 7mm-pitch (n = 1..27). */
		top: 6.7mm;
		left: 0;
		right: 0;
		height: 0.3mm;
		color: var(--paper-line);
		background: currentColor;
		box-shadow:
			0 7mm, 0 14mm, 0 21mm, 0 28mm, 0 35mm, 0 42mm, 0 49mm, 0 56mm, 0 63mm, 0 70mm,
			0 77mm, 0 84mm, 0 91mm, 0 98mm, 0 105mm, 0 112mm, 0 119mm, 0 126mm, 0 133mm, 0 140mm,
			0 147mm, 0 154mm, 0 161mm, 0 168mm, 0 175mm, 0 182mm, 0 189mm;
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
		/* Tekst-variant van P1-goud: --paper-p1 haalt op wit geen leesbaar
		   contrast voor 5pt-tekst; randen/balken blijven --paper-p1. */
		color: var(--paper-p1-ink);
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
