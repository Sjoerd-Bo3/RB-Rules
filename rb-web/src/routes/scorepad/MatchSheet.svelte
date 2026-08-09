<script lang="ts">
	// Bo3 match sheet (#342): drie games met Conquer/Hold-punttracks — je turft
	// niet alleen de score maar ook hóé elk punt gescoord is (het RiftHub-idee,
	// in eigen ontwerp). Per game: first player + battlefield-keuze per speler
	// (§486.5: ieder kiest er één van zijn drie).
	import SheetFrame from './SheetFrame.svelte';

	let { bw = false }: { bw?: boolean } = $props();

	const GAMES = [1, 2, 3];
	const POINTS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
</script>

<SheetFrame {bw} title="Match Sheet" sub="1v1 · Best of 3 · First to 8 — Core Rules §486">
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

	<div class="games">
		{#each GAMES as g (g)}
			<section class="game">
				<header class="ghead">
					<span class="gnum">Game {g}</span>
					<span class="gfirst"
						><span class="micro">First</span><span class="cb small p1"></span><span class="cb small p2"
						></span></span
					>
				</header>
				<div class="gbf">
					<span class="bfl"><span class="micro">BF P1</span><span class="fill"></span></span>
					<span class="bfl"><span class="micro">BF P2</span><span class="fill"></span></span>
				</div>
				<div class="trk">
					<div class="trow th">
						<span class="tc trk-h c1">C</span><span class="tc trk-h c1">H</span><span class="tn"
						></span>
						<span class="tc trk-h c2">C</span><span class="tc trk-h c2">H</span>
					</div>
					{#each POINTS as p (p)}
						<div class="trow">
							<span class="tc"><span class="cb small p1"></span></span>
							<span class="tc"><span class="cb small p1"></span></span>
							<span class="tn"><span class="trk-num" class:vs={p === 8}>{p}</span></span>
							<span class="tc"><span class="cb small p2"></span></span>
							<span class="tc"><span class="cb small p2"></span></span>
						</div>
					{/each}
				</div>
				<footer class="gwin">
					<span class="micro">Winner</span><span class="cb small p1"></span><span
						class="cb small p2"
					></span>
				</footer>
			</section>
		{/each}
	</div>
	<p class="legend">
		C = point by Conquer · H = point by Hold — the track runs past 8 on purpose (§194.2.a)
	</p>

	<div class="matchrow">
		<span class="mw"
			><span class="mwl">Match winner</span><span class="opt"><span class="cb p1"></span> P1</span
			><span class="opt"><span class="cb p2"></span> P2</span></span
		>
		<span class="sh-field" style="flex: 0 1 34mm"
			><span class="micro">Games</span><span class="fill"></span><span class="dash">–</span><span
				class="fill"
			></span></span
		>
	</div>

	<div class="sec"><span>Notes</span></div>
	<div class="notes dots"></div>
</SheetFrame>

<style>
	.games {
		display: grid;
		grid-template-columns: repeat(3, 1fr);
		gap: 2.6mm;
	}
	.game {
		border: 0.3mm solid var(--paper-line);
		border-radius: 1.2mm;
		padding: 1.6mm 1.8mm;
		display: flex;
		flex-direction: column;
	}
	.ghead {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: 1mm;
		margin-bottom: 1.2mm;
	}
	.gnum {
		font-size: 6.6pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.08em;
	}
	.gfirst {
		display: inline-flex;
		align-items: center;
		gap: 1mm;
	}
	.gbf {
		display: flex;
		flex-direction: column;
		gap: 0.8mm;
		margin-bottom: 1.4mm;
	}
	.bfl {
		display: flex;
		align-items: baseline;
		gap: 1.2mm;
	}
	.bfl .fill {
		flex: 1;
		border-bottom: 0.28mm solid var(--paper-line);
		height: 3.4mm;
	}

	/* Rijen als échte rijen (flex): één doorlopende scheidingslijn per rij —
	   losse cel-randjes lopen door hoogteverschillen nooit strak (#342). */
	.trk {
		display: flex;
		flex-direction: column;
	}
	.trow {
		display: flex;
		align-items: center;
		padding: 0.55mm 0;
		border-bottom: 0.2mm solid var(--paper-line-soft);
	}
	.trow.th {
		padding: 0 0 0.6mm;
		border-bottom: 0.28mm solid var(--paper-line);
	}
	.tc {
		flex: 1;
		display: flex;
		align-items: center;
		justify-content: center;
	}
	.tn {
		flex: none;
		width: 4.6mm;
		display: flex;
		align-items: center;
		justify-content: center;
	}

	.gwin {
		display: flex;
		align-items: center;
		justify-content: center;
		gap: 1.4mm;
		padding-top: 1.4mm;
	}

	.legend {
		margin: 1.4mm 0 0;
		font-size: 5pt;
		color: var(--paper-muted);
		letter-spacing: 0.03em;
	}

	.matchrow {
		display: flex;
		align-items: flex-end;
		justify-content: space-between;
		gap: 4mm;
		margin-top: 2mm;
		border: 0.35mm solid var(--paper-ink);
		border-radius: 1.2mm;
		padding: 1.8mm 2.4mm;
	}
	.mw {
		display: inline-flex;
		align-items: center;
		gap: 2.6mm;
	}
	.mwl {
		font-size: 6.6pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.1em;
	}
	.dash {
		color: var(--paper-muted);
	}

	.notes {
		flex: 1;
		min-height: 18mm;
		margin-bottom: 1mm;
	}
</style>
