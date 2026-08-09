<script lang="ts">
	// 2v2 Magma Chamber (#342): teams delen hun punten (§489.8.d), dus één
	// gedeelde track per team — naar 11, doorlopend tot 13 (overshoot kan,
	// §194.2.a). Turn order wisselt per team (§489.5.c).
	import SheetFrame from './SheetFrame.svelte';

	let { bw = false }: { bw?: boolean } = $props();

	const POINTS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
	const TEAMS = [
		{ name: 'Team A', cls: 'p1' },
		{ name: 'Team B', cls: 'p2' }
	];
</script>

<SheetFrame {bw} title="2v2 Sheet" sub="Magma Chamber · Teams share points · First team to 11 — §489">
	<div class="sh-row">
		<span class="sh-field"><span class="micro">Date</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 2"
			><span class="micro">Event</span><span class="fill"></span></span
		>
	</div>

	<div class="sh-row">
		<span class="sh-field" style="flex: 1.2">
			<span class="micro">Turn order</span>
			{#each [1, 2, 3, 4] as n (n)}
				<span class="ordnum tnum">{n}</span><span class="fill"></span>
			{/each}
		</span>
		<span class="sh-field">
			<span class="micro">Battlefields</span>
			<span class="fill"></span><span class="fill"></span><span class="fill"></span>
		</span>
	</div>

	<div class="teams">
		{#each TEAMS as team (team.cls)}
			<section class="team {team.cls}">
				<header class="thead">{team.name}</header>
				<!-- Name boven Legend per teammate (keuze Sjoerd, UX-review) — zelfde
				     patroon als FFA. -->
				<div class="tline">
					<span class="pnum tnum">1</span><span class="micro">Name</span><span class="fill"></span>
				</div>
				<div class="tline tsub">
					<span class="micro">Legend</span><span class="fill"></span>
				</div>
				<div class="tline">
					<span class="pnum tnum">2</span><span class="micro">Name</span><span class="fill"></span>
				</div>
				<div class="tline tsub">
					<span class="micro">Legend</span><span class="fill"></span>
				</div>
				<div class="trk">
					<div class="trow tsp">
						<span class="tspan trk-h">Player 1</span><span class="tn"></span><span
							class="tspan trk-h">Player 2</span
						>
					</div>
					<div class="trow th">
						<span class="tc trk-h">C</span><span class="tc trk-h">H</span><span class="tn"></span>
						<span class="tc trk-h">C</span><span class="tc trk-h">H</span>
					</div>
					{#each POINTS as p (p)}
						<div class="trow">
							<span class="tc"><span class="cb {team.cls}"></span></span>
							<span class="tc"><span class="cb {team.cls}"></span></span>
							<span class="tn"><span class="trk-num" class:vs={p === 11}>{p}</span></span>
							<span class="tc"><span class="cb {team.cls}"></span></span>
							<span class="tc"><span class="cb {team.cls}"></span></span>
						</div>
					{/each}
				</div>
				<footer class="twin">
					<span class="micro">Team win</span><span class="cb small {team.cls}"></span>
				</footer>
			</section>
		{/each}
	</div>
	<p class="legend">
		C = by Conquer · H = by Hold — tick in the column of the teammate who scored it; the row
		number is the shared team total (§489.8.d)
	</p>

	<div class="sec"><span>Notes</span></div>
	<div class="notes dots"></div>
</SheetFrame>

<style>
	.ordnum {
		font-size: 5.6pt;
		font-weight: 700;
		color: var(--paper-muted);
	}

	.teams {
		display: grid;
		grid-template-columns: 1fr 1fr;
		gap: 3mm;
	}
	.team {
		border: 0.3mm solid var(--paper-line);
		border-radius: 1.2mm;
		padding: 1.8mm 2.2mm;
		display: flex;
		flex-direction: column;
		border-top: 1.4mm solid var(--paper-ink);
	}
	.team.p1 {
		border-top-color: var(--paper-p1);
	}
	.team.p2 {
		border-top-color: var(--paper-p2);
	}
	.thead {
		font-size: 7pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.1em;
		margin-bottom: 1.2mm;
	}
	.tline {
		display: flex;
		align-items: baseline;
		gap: 1.4mm;
		margin-bottom: 0.9mm;
	}
	/* Legend-regel springt in onder het spelernummer van de Name-regel. */
	.tline.tsub {
		padding-left: 3.2mm;
	}
	.tline .fill {
		flex: 1;
		border-bottom: 0.28mm solid var(--paper-line);
		height: 4.2mm;
	}

	.pnum {
		font-size: 6.4pt;
		font-weight: 800;
	}

	/* Rijen als échte rijen (flex): één doorlopende scheidingslijn per rij. */
	.trk {
		display: flex;
		flex-direction: column;
		margin-top: 1mm;
	}
	.trow {
		display: flex;
		align-items: center;
		padding: 0.5mm 0;
		border-bottom: 0.2mm solid var(--paper-line-soft);
	}
	.trow.tsp {
		padding: 0 0 0.3mm;
		border-bottom: 0;
	}
	.trow.th {
		padding: 0 0 0.5mm;
		border-bottom: 0.28mm solid var(--paper-line);
	}
	.tspan {
		flex: 2;
		text-align: center;
	}
	.tn {
		flex: none;
		width: 5mm;
		display: flex;
		align-items: center;
		justify-content: center;
	}
	.tc {
		flex: 1;
		display: flex;
		align-items: center;
		justify-content: center;
	}

	.twin {
		display: flex;
		align-items: center;
		justify-content: center;
		gap: 1.6mm;
		padding-top: 1.4mm;
	}

	.legend {
		margin: 1.4mm 0 0;
		font-size: 6pt;
		color: var(--paper-muted);
		letter-spacing: 0.03em;
	}

	.notes {
		flex: 1;
		min-height: 12mm;
		margin-bottom: 1mm;
	}
</style>
