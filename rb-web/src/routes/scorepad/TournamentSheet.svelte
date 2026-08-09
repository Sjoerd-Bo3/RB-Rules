<script lang="ts">
	// Toernooi-dag (#342): één vel voor een hele dag — acht rondes met
	// tegenstander, first player, resultaat en games, onderaan het record.
	import SheetFrame from './SheetFrame.svelte';

	let { bw = false }: { bw?: boolean } = $props();

	const ROUNDS = [1, 2, 3, 4, 5, 6, 7, 8];
</script>

<SheetFrame {bw} title="Tournament Day" sub="Swiss rounds, results & record">
	<!-- Date vóór Event: zelfde veldvolgorde als alle andere vellen (#342-fix). -->
	<div class="sh-row">
		<span class="sh-field"><span class="micro">Date</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 2"
			><span class="micro">Event</span><span class="fill"></span></span
		>
	</div>
	<div class="sh-row">
		<span class="sh-field"><span class="micro">Format</span><span class="fill"></span></span>
		<span class="sh-field"><span class="micro">My legend</span><span class="fill"></span></span>
		<span class="sh-field"><span class="micro">Deck</span><span class="fill"></span></span>
	</div>

	<div class="rtable">
		<div class="rhead">
			<span class="tnum">Rd</span>
			<span>Tbl</span>
			<span>Opponent — name over legend</span>
			<span>First</span>
			<span>Result</span>
			<span>Games</span>
		</div>
		{#each ROUNDS as r (r)}
			<div class="rrow">
				<span class="rd tnum">{r}</span>
				<span class="tblline"></span>
				<span class="opp"><span class="oline"></span><span class="oline"></span></span>
				<span class="first">
					<span class="opt"><span class="cb p1"></span> Me</span>
					<span class="opt"><span class="cb p2"></span> Op</span>
				</span>
				<span class="res">
					<span class="opt"><span class="cb"></span> W</span>
					<span class="opt"><span class="cb"></span> L</span>
					<span class="opt"><span class="cb"></span> D</span>
				</span>
				<span class="games"><span class="gline"></span><span class="dash">–</span><span class="gline"></span></span>
			</div>
		{/each}
	</div>

	<div class="record">
		<span class="mwl">Record</span>
		<span class="sh-field" style="flex: 0 1 16mm"><span class="micro">W</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 0 1 16mm"><span class="micro">L</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 0 1 16mm"><span class="micro">D</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 0 1 30mm"
			><span class="micro">Finish</span><span class="fill"></span></span
		>
	</div>

	<div class="sec">
		<span>Notes</span><span class="sec-note">meta observed, decks to expect, things to test</span>
	</div>
	<div class="notes rlines"></div>
</SheetFrame>

<style>
	.rtable {
		margin-top: 1mm;
		border: 0.3mm solid var(--paper-line);
		border-radius: 1.2mm;
		overflow: hidden;
	}
	/* First/Result iets breder sinds de boxjes 3.4mm zijn — anders wrappen de
	   opt-paren binnen hun kolom. */
	/* Tbl-kolom (keuze Sjoerd, UX-review): Swiss-pairings draaien op
	   tafelnummers. */
	.rhead,
	.rrow {
		display: grid;
		grid-template-columns: 6mm 7mm 1fr 19mm 25mm 15mm;
		gap: 2mm;
		align-items: center;
		padding: 0 2mm;
	}
	.tblline {
		border-bottom: 0.28mm solid var(--paper-line);
		height: 4mm;
		align-self: end;
		margin-bottom: 1.4mm;
	}
	.rhead {
		font-size: 5pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.07em;
		color: var(--paper-muted);
		border-bottom: 0.3mm solid var(--paper-line);
		padding-top: 1mm;
		padding-bottom: 1mm;
	}
	.rrow {
		border-bottom: 0.2mm solid var(--paper-line-soft);
		padding-top: 1.1mm;
		padding-bottom: 1.1mm;
	}
	.rrow:last-child {
		border-bottom: 0;
	}
	.rd {
		font-size: 7pt;
		font-weight: 800;
		text-align: center;
	}
	.opp {
		display: flex;
		flex-direction: column;
		gap: 3.2mm;
		padding-top: 0.6mm;
	}
	.oline {
		border-bottom: 0.28mm solid var(--paper-line);
		height: 3.2mm;
	}
	.first,
	.res {
		display: flex;
		align-items: center;
		gap: 1.6mm;
		flex-wrap: wrap;
	}
	.games {
		display: flex;
		align-items: center;
		gap: 1mm;
	}
	.gline {
		flex: 1;
		border-bottom: 0.28mm solid var(--paper-line);
		height: 3.6mm;
	}
	.dash {
		color: var(--paper-muted);
	}

	.record {
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

	.notes {
		flex: 1;
		min-height: 14mm;
		margin-bottom: 1mm;
	}
</style>
