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
		<!-- Groepslabel per trackhelft: de C/H-koppen dragen alleen kleur en die
		     valt in bw weg (#342-fix). -->
		<div class="trow tsp">
			<span class="tgrp micro">P1</span><span class="tn"></span><span class="tgrp micro">P2</span>
			<span class="thow"></span>
		</div>
		<div class="trow th">
			<span class="tc trk-h c1">C</span><span class="tc trk-h c1">H</span><span class="tn"></span>
			<span class="tc trk-h c2">C</span><span class="tc trk-h c2">H</span>
			<span class="thow trk-h how">How the point was scored — battlefield, play, misplay</span>
		</div>
		{#each POINTS as p (p)}
			<div class="trow">
				<span class="tc"><span class="cb p1"></span></span>
				<span class="tc"><span class="cb p1"></span></span>
				<span class="tn"><span class="trk-num" class:vs={p === 8}>{p}</span></span>
				<span class="tc"><span class="cb p2"></span></span>
				<span class="tc"><span class="cb p2"></span></span>
				<span class="thow wl"></span>
			</div>
		{/each}
	</div>
	<p class="legend">C = point by Conquer · H = point by Hold — the track runs past 8 (§194.2.a)</p>

	<div class="winrow">
		<span class="mwl">Winner</span>
		<span class="opt"><span class="cb p1"></span> P1</span>
		<span class="opt"><span class="cb p2"></span> P2</span>
		<span class="sh-field" style="flex: 0 1 40mm"
			><span class="micro">Games</span><span class="fill"></span><span class="dash">–</span><span
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

	/* Rijen als échte rijen (flex): geen losse cel-randjes die verspringen —
	   de schrijflijn per punt is hier de enige rijlijn (#342). */
	.trk {
		display: flex;
		flex-direction: column;
		margin-top: 1mm;
	}
	.trow {
		display: flex;
		align-items: stretch;
		height: 7mm;
	}
	.trow.th {
		height: auto;
		padding-bottom: 0.6mm;
		border-bottom: 0.3mm solid var(--paper-line);
		align-items: flex-end;
	}
	.trow.tsp {
		height: auto;
		align-items: flex-end;
		padding-bottom: 0.3mm;
	}
	/* Precies twee C/H-kolommen breed (2 × 5.4mm), zodat de track niet
	   verbreedt. */
	.tgrp {
		flex: none;
		width: 10.8mm;
		text-align: center;
	}
	.tc,
	.tn {
		flex: none;
		width: 5.4mm;
		display: flex;
		align-items: center;
		justify-content: center;
	}
	.thow {
		flex: 1;
		min-width: 0;
	}
	.trk-h.how {
		text-align: left;
		padding-left: 2.4mm;
		letter-spacing: 0.05em;
	}
	.wl {
		border-bottom: 0.28mm solid var(--paper-line);
		margin: 0 0 1.2mm 2.4mm;
	}

	.legend {
		margin: 1.2mm 0 0;
		font-size: 6pt;
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
