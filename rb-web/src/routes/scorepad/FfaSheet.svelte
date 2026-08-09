<script lang="ts">
	// Free-for-all (#342): FFA3 Skirmish (§487) en FFA4 War (§488) delen dit
	// vel — bij drie spelers blijft de vierde kolom leeg. Ieder een eigen
	// track naar 8; turn order en de drie battlefields bovenaan.
	import SheetFrame from './SheetFrame.svelte';

	let { bw = false }: { bw?: boolean } = $props();

	const PLAYERS = [1, 2, 3, 4];
	const POINTS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
</script>

<SheetFrame {bw} title="Free-for-all" sub="FFA3 Skirmish §487 · FFA4 War §488 · First to 8">
	<div class="sh-row">
		<span class="sh-field"><span class="micro">Date</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 2"
			><span class="micro">Event</span><span class="fill"></span></span
		>
	</div>

	<div class="sh-row">
		<span class="sh-field" style="flex: 1.2">
			<span class="micro">Turn order</span>
			{#each PLAYERS as n (n)}
				<span class="ordnum tnum">{n}</span><span class="fill"></span>
			{/each}
		</span>
		<span class="sh-field">
			<span class="micro">Battlefields</span>
			<span class="fill"></span><span class="fill"></span><span class="fill"></span>
		</span>
	</div>

	<div class="grid4">
		{#each PLAYERS as n (n)}
			<section class="fpl">
				<header class="fhead">P{n}</header>
				<div class="fline"><span class="micro">Name</span><span class="fill"></span></div>
				<div class="fline"><span class="micro">Legend</span><span class="fill"></span></div>
				<div class="trk">
					<span></span><span class="trk-h">C</span><span class="trk-h">H</span>
					{#each POINTS as p (p)}
						<span class="trk-num" class:vs={p === 8}>{p}</span>
						<span class="cell"><span class="cb small"></span></span>
						<span class="cell"><span class="cb small"></span></span>
					{/each}
				</div>
				<footer class="fwin"><span class="micro">Winner</span><span class="cb small"></span></footer>
			</section>
		{/each}
	</div>
	<p class="legend">
		C = point by Conquer · H = point by Hold — three players: leave one column blank
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

	.grid4 {
		display: grid;
		grid-template-columns: repeat(4, 1fr);
		gap: 2.4mm;
	}
	.fpl {
		border: 0.3mm solid var(--paper-line);
		border-radius: 1.2mm;
		padding: 1.6mm 1.8mm;
		display: flex;
		flex-direction: column;
	}
	.fhead {
		font-size: 6.6pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.08em;
		margin-bottom: 1mm;
	}
	.fline {
		display: flex;
		align-items: baseline;
		gap: 1.2mm;
		margin-bottom: 0.8mm;
	}
	.fline .fill {
		flex: 1;
		border-bottom: 0.28mm solid var(--paper-line);
		height: 3.4mm;
	}

	.trk {
		display: grid;
		grid-template-columns: 4.6mm 1fr 1fr;
		align-items: center;
		margin-top: 0.8mm;
	}
	.cell {
		display: flex;
		align-items: center;
		justify-content: center;
		padding: 0.55mm 0;
	}
	.trk > :global(*) {
		border-bottom: 0.2mm solid var(--paper-line-soft);
	}
	/* Eén doorlopende lijn onder de complete kopregel (alle drie cellen). */
	.trk > :global(:nth-child(-n + 3)) {
		border-bottom: 0.28mm solid var(--paper-line);
	}

	.fwin {
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

	.notes {
		flex: 1;
		min-height: 16mm;
		margin-bottom: 1mm;
	}
</style>
