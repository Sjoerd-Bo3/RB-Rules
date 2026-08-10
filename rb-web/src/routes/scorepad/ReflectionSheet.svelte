<script lang="ts">
	// Match reflection (#342): het verhaal áchter de score — verwachting
	// vooraf, verloop, kaarten die op- of tegenvielen en de les voor de
	// volgende keer. Concept naar de Codex-journal, bewoording en indeling
	// zijn van onszelf.
	import SheetFrame from './SheetFrame.svelte';

	let { bw = false }: { bw?: boolean } = $props();
</script>

<SheetFrame {bw} title="Match Reflection" sub="The story behind the score">
	<div class="sh-row">
		<span class="sh-field"><span class="micro">Date</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 2"
			><span class="micro">Event</span><span class="fill"></span></span
		>
	</div>

	<div class="sh-row">
		<span class="pl p1"><span class="micro">My legend</span><span class="fill"></span></span>
		<span class="pl p2"><span class="micro">Their legend</span><span class="fill"></span></span>
		<span class="result">
			<span class="micro">Result</span>
			<span class="opt"><span class="cb small"></span> W</span>
			<span class="opt"><span class="cb small"></span> L</span>
			<span class="opt"><span class="cb small"></span> D</span>
		</span>
	</div>

	<div class="sec"><span>Before the match</span></div>
	<div class="optrow">
		<span class="micro">Gameplan</span>
		<span class="opt"><span class="cb small"></span> Clear</span>
		<span class="opt"><span class="cb small"></span> Rough idea</span>
		<span class="opt"><span class="cb small"></span> None</span>
		<span class="sep"></span>
		<span class="micro">First time facing this deck</span>
		<span class="opt"><span class="cb small"></span> Yes</span>
		<span class="opt"><span class="cb small"></span> No</span>
	</div>
	<div class="optrow">
		<span class="micro">Matchup read</span>
		<span class="opt"><span class="cb small"></span> Favored</span>
		<span class="opt"><span class="cb small"></span> Even</span>
		<span class="opt"><span class="cb small"></span> Unfavored</span>
		<span class="opt"><span class="cb small"></span> No idea</span>
	</div>

	<div class="sec">
		<span>The games</span><span class="sec-note">key moments, turning points, surprises</span>
	</div>
	<div class="wbox" style="height: 28mm"></div>
	<div class="sec">
		<span>Mistakes I caught</span><span class="sec-note">and what they cost</span>
	</div>
	<div class="wbox" style="height: 16mm"></div>

	<div class="cards">
		<div class="ccol">
			<div class="sec"><span>Overperformed</span></div>
			<div class="wline"></div>
			<div class="wline"></div>
			<div class="wline"></div>
			<div class="wline"></div>
		</div>
		<div class="ccol">
			<div class="sec"><span>Underperformed</span></div>
			<div class="wline"></div>
			<div class="wline"></div>
			<div class="wline"></div>
			<div class="wline"></div>
		</div>
	</div>

	<div class="sec"><span>Taking it forward</span></div>
	<div class="optrow">
		<span class="micro">Did this match change how I see the matchup?</span>
		<span class="opt"><span class="cb small"></span> Yes</span>
		<span class="opt"><span class="cb small"></span> No</span>
	</div>
	<!-- Label boven de regels en twee regels per vraag (keuze Sjoerd,
	     UX-review): wat je invult is wat je later terugleest. -->
	<p class="flabel micro">Biggest lesson</p>
	<div class="wline"></div>
	<div class="wline"></div>
	<p class="flabel micro">Try next time</p>
	<div class="wline"></div>
	<div class="wline"></div>

	<div class="sec"><span>Notes</span></div>
	<!-- Bewust dots waar de andere na-afloop-vellen lijntjes hebben (keuze
	     Sjoerd): vrije schetsruimte, bv. om een bordstand te tekenen. -->
	<div class="xnotes dots"></div>
</SheetFrame>

<style>
	.result {
		display: inline-flex;
		align-items: center;
		gap: 1.8mm;
		flex: none;
	}

	.sep {
		flex: 1;
	}

	.cards {
		display: grid;
		grid-template-columns: 1fr 1fr;
		gap: 4mm;
	}

	.flabel {
		margin: 1.4mm 0 0.4mm;
	}
	.xnotes {
		flex: 1;
		min-height: 8mm;
		margin-bottom: 1mm;
	}
</style>
