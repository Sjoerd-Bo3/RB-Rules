<script lang="ts">
	// Milestone review (#342): periodieke terugblik over meerdere sessies —
	// pagina 1 resultaten & deck, pagina 2 meta & mindset. Concept naar de
	// Codex-journal, bewoording en indeling van onszelf.
	import SheetFrame from './SheetFrame.svelte';

	let { part = 1, bw = false }: { part?: 1 | 2; bw?: boolean } = $props();
</script>

{#if part === 1}
	<SheetFrame {bw} title="Milestone Review" sub="Results & deck — page 1 of 2">
		<div class="sh-row">
			<span class="sh-field"><span class="micro">Period</span><span class="fill"></span></span>
			<span class="sh-field"><span class="micro">Legend / deck</span><span class="fill"></span></span>
			<span class="sh-field" style="flex: 0 1 26mm"
				><span class="micro">Matches</span><span class="fill"></span></span
			>
		</div>

		<div class="sec">
			<span>Matchups I keep winning</span><span class="sec-note">and what wins them</span>
		</div>
		<div class="wbox" style="height: 24mm"></div>

		<div class="sec">
			<span>Matchups that hurt</span><span class="sec-note">and where they slip away</span>
		</div>
		<div class="wbox" style="height: 24mm"></div>

		<div class="cards">
			<div>
				<div class="sec"><span>Cards earning their slot</span></div>
				<div class="wline"></div>
				<div class="wline"></div>
				<div class="wline"></div>
			</div>
			<div>
				<div class="sec"><span>Cards underdelivering</span></div>
				<div class="wline"></div>
				<div class="wline"></div>
				<div class="wline"></div>
			</div>
		</div>

		<div class="sec"><span>Consistency</span></div>
		<div class="optrow">
			<span class="micro">I can play my gameplan</span>
			<span class="opt"><span class="cb small"></span> Consistently</span>
			<span class="opt"><span class="cb small"></span> Sometimes</span>
			<span class="opt"><span class="cb small"></span> Rarely</span>
		</div>
		<div class="optrow">
			<span class="micro">Opening hands</span>
			<span class="opt"><span class="cb small"></span> Fine as is</span>
			<span class="opt"><span class="cb small"></span> Mulligan too often</span>
		</div>
		<div class="optrow">
			<span class="micro">Rune curve lets me stay on tempo</span>
			<span class="opt"><span class="cb small"></span> Yes</span>
			<span class="opt"><span class="cb small"></span> Mostly</span>
			<span class="opt"><span class="cb small"></span> No</span>
		</div>

		<div class="sec"><span>Notes</span></div>
		<div class="xnotes rlines"></div>
	</SheetFrame>
{:else}
	<SheetFrame {bw} title="Milestone Review" sub="Meta & mindset — page 2 of 2">
		<div class="sh-row">
			<span class="sh-field"><span class="micro">Period</span><span class="fill"></span></span>
			<span class="sh-field"><span class="micro">Legend / deck</span><span class="fill"></span></span>
		</div>

		<div class="sec">
			<span>New strategies I ran into</span><span class="sec-note">and how I handled them</span>
		</div>
		<div class="wbox" style="height: 22mm"></div>

		<div class="sec"><span>Tech worth adopting</span><span class="sec-note">seen at other tables</span></div>
		<div class="wline"></div>
		<div class="wline"></div>

		<div class="sec"><span>Battlefields</span></div>
		<div class="optrow">
			<span class="micro">Is my battlefield lineup still right?</span>
			<span class="opt"><span class="cb small"></span> Yes</span>
			<span class="opt"><span class="cb small"></span> Doubting</span>
		</div>
		<div class="fline"><span class="micro">Worth testing</span><span class="wline grow"></span></div>

		<div class="sec"><span>Mindset</span></div>
		<div class="optrow">
			<span class="micro">My losses mostly come from</span>
			<span class="opt"><span class="cb small"></span> My decisions</span>
			<span class="opt"><span class="cb small"></span> Mixed</span>
			<span class="opt"><span class="cb small"></span> The deck</span>
		</div>
		<div class="optrow">
			<span class="micro">Confidence piloting this deck</span>
			<span class="opt"><span class="cb small"></span> High</span>
			<span class="opt"><span class="cb small"></span> OK</span>
			<span class="opt"><span class="cb small"></span> Low</span>
		</div>

		<div class="sec"><span>Top 3 changes before the next event</span></div>
		{#each [1, 2, 3] as n (n)}
			<div class="fline"><span class="num tnum">{n}</span><span class="wline grow"></span></div>
		{/each}

		<div class="sec"><span>Notes</span></div>
		<div class="xnotes rlines"></div>
	</SheetFrame>
{/if}

<style>
	.cards {
		display: grid;
		grid-template-columns: 1fr 1fr;
		gap: 4mm;
	}
	.fline {
		display: flex;
		align-items: baseline;
		gap: 2mm;
		margin-bottom: 1.2mm;
	}
	.grow {
		flex: 1;
	}
	.num {
		font-size: 7pt;
		font-weight: 800;
	}
	.xnotes {
		flex: 1;
		min-height: 8mm;
		margin-bottom: 1mm;
	}
</style>
