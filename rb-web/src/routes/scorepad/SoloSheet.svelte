<script lang="ts">
	// Losse game (#342): één game met ruime rijen — per gescoord punt een
	// schrijfregel voor hoe het punt viel (welk battlefield, wat er gebeurde).
	// Voor casual spellen, leren en het navertellen achteraf.
	import SheetFrame from './SheetFrame.svelte';

	let { bw = false }: { bw?: boolean } = $props();

	const POINTS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
</script>

<SheetFrame {bw} title="Game Sheet" sub="1v1 · Single game · First to 8 — Core Rules §485">
	<div class="sh-row">
		<span class="sh-field"><span class="micro">Date</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 2"
			><span class="micro">Event</span><span class="fill"></span></span
		>
	</div>

	<div class="sh-row">
		<span class="pl p1"><span class="micro">P1 · You</span><span class="micro">Legend</span><span class="fill"></span></span>
		<span class="pl p2"><span class="micro">P2 · Opponent</span><span class="micro">Legend</span><span class="fill"></span></span>
	</div>

	<div class="sh-row">
		<span class="first"
			><span class="micro">First player</span><span class="opt"><span class="cb p1"></span> P1</span
			><span class="opt"><span class="cb p2"></span> P2</span></span
		>
		<span class="sh-field"><span class="micro">BF P1</span><span class="fill"></span></span>
		<span class="sh-field"><span class="micro">BF P2</span><span class="fill"></span></span>
	</div>

	<div class="trk">
		<span class="trk-h c1">C</span><span class="trk-h c1">H</span><span></span>
		<span class="trk-h c2">C</span><span class="trk-h c2">H</span>
		<span class="trk-h how">How the point was scored — battlefield, play, misplay</span>
		{#each POINTS as p (p)}
			<span class="cell"><span class="cb p1"></span></span>
			<span class="cell"><span class="cb p1"></span></span>
			<span class="trk-num" class:vs={p === 8}>{p}</span>
			<span class="cell"><span class="cb p2"></span></span>
			<span class="cell"><span class="cb p2"></span></span>
			<span class="how"></span>
		{/each}
	</div>
	<p class="legend">C = point by Conquer · H = point by Hold — the track runs past 8 (§194.2.a)</p>

	<div class="winrow">
		<span class="mwl">Winner</span>
		<span class="opt"><span class="cb p1"></span> P1</span>
		<span class="opt"><span class="cb p2"></span> P2</span>
		<span class="sh-field" style="flex: 0 1 40mm"
			><span class="micro">Final</span><span class="fill"></span><span class="dash">–</span><span
				class="fill"
			></span></span
		>
	</div>

	<div class="sec"><span>Notes</span></div>
	<div class="notes dots"></div>
</SheetFrame>

<style>
	.first {
		display: inline-flex;
		align-items: center;
		gap: 2mm;
		flex: none;
	}

	.trk {
		display: grid;
		grid-template-columns: 5.4mm 5.4mm 5.4mm 5.4mm 5.4mm 1fr;
		align-items: center;
		margin-top: 1mm;
	}
	.cell {
		display: flex;
		align-items: center;
		justify-content: center;
		padding: 1.5mm 0;
	}
	.trk > :global(*) {
		border-bottom: 0.2mm solid var(--paper-line-soft);
	}
	/* Eén doorlopende lijn onder de complete kopregel (alle zes cellen). */
	.trk > :global(:nth-child(-n + 6)) {
		border-bottom: 0.3mm solid var(--paper-line);
	}
	.trk-h.how {
		text-align: left;
		padding-left: 2.4mm;
		letter-spacing: 0.05em;
	}
	.how {
		align-self: stretch;
		margin: 0 0 1mm 2.4mm;
		border-bottom: 0.28mm solid var(--paper-line) !important;
	}

	.legend {
		margin: 1.2mm 0 0;
		font-size: 5pt;
		color: var(--paper-muted);
		letter-spacing: 0.03em;
	}

	.winrow {
		display: flex;
		align-items: flex-end;
		gap: 3mm;
		margin-top: 2mm;
		border: 0.35mm solid var(--paper-ink);
		border-radius: 1.2mm;
		padding: 1.8mm 2.4mm;
	}
	.mwl {
		font-size: 6.6pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.1em;
	}
	.winrow .sh-field {
		margin-left: auto;
	}
	.dash {
		color: var(--paper-muted);
	}

	.notes {
		flex: 1;
		min-height: 14mm;
		margin-bottom: 1mm;
	}
</style>
