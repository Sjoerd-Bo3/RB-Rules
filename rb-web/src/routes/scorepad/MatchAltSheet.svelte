<script lang="ts">
	// Bo3 match sheet, variant B (#342): de RiftHub-ÍNDELING (trapsgewijze
	// game-blokken met een groot notitievlak) nagebouwd in eigen Poracle-stijl —
	// bewust geen kopie van hun merk of exacte vormgeving, wel dezelfde
	// ruimtelijke opzet. Verschil met variant A: geen first-player- of
	// battlefield-velden (die zitten op A), wél YOU/OPP-kleurbanden per game en
	// veel notitieruimte.
	import SheetFrame from './SheetFrame.svelte';

	let { bw = false }: { bw?: boolean } = $props();

	const GAMES = [1, 2, 3];
	const POINTS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
</script>

<SheetFrame {bw} title="Match Sheet" sub="Variant B · 1v1 · Best of 3 · First to 8 — Core Rules §486">
	<div class="sh-row">
		<span class="sh-field"><span class="micro">Date</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 1.6"
			><span class="micro">Event</span><span class="fill"></span></span
		>
	</div>
	<div class="sh-row">
		<!-- Name naast Legend (keuze Sjoerd, UX-review): zelfde velden als A. -->
		<span class="pl p1"
			><span class="micro">P1 · You</span><span class="micro">Name</span><span class="fill"
			></span><span class="micro">Legend</span><span class="fill"></span></span
		>
		<span class="pl p2"
			><span class="micro">P2 · Opponent</span><span class="micro">Name</span><span class="fill"
			></span><span class="micro">Legend</span><span class="fill"></span></span
		>
	</div>

	<div class="games">
		{#each GAMES as g (g)}
			<section class="game">
				<header class="gnum">Game {g}</header>
				<!-- Kleurbanden: linkerhelft You (P1-goud), rechterhelft Opp
				     (P2-rood); de cijferkolom in het midden blijft open zodat de
				     banden exact boven de C/H-kolommen van de track staan. -->
				<!-- P1/P2 in de band zelf: het groepslabel per trackhelft moet ook in
				     bw leesbaar blijven, en de band zit al exact boven de C/H-koppen
				     (#342-fix). -->
				<div class="bands">
					<span class="band b1">P1 · You</span>
					<span class="bn"></span>
					<span class="band b2">P2 · Opp</span>
				</div>
				<div class="trk">
					<div class="trow th">
						<span class="tc trk-h c1">C</span><span class="tc trk-h c1">H</span><span class="tn"
						></span>
						<span class="tc trk-h c2">C</span><span class="tc trk-h c2">H</span>
					</div>
					{#each POINTS as p (p)}
						<div class="trow">
							<span class="tc"><span class="cb p1"></span></span>
							<span class="tc"><span class="cb p1"></span></span>
							<span class="tn"><span class="trk-num" class:vs={p === 8}>{p}</span></span>
							<span class="tc"><span class="cb p2"></span></span>
							<span class="tc"><span class="cb p2"></span></span>
						</div>
					{/each}
				</div>
				<!-- Tekstlabel per box: kleur alleen is in bw niet te onderscheiden
				     (#342-fix). -->
				<footer class="gwin">
					<span class="micro">Winner</span><span class="fp"
						><span class="cb small p1"></span><span class="micro">P1</span></span
					><span class="fp"><span class="cb small p2"></span><span class="micro">P2</span></span>
				</footer>
			</section>
		{/each}
	</div>

	<!-- Legenda direct onder de games-grid, boven het winnaar-blok — zelfde
	     positie en formulering als variant A (#342-fix). -->
	<p class="legend">
		C = point by Conquer · H = point by Hold — the track runs past 8 on purpose (§194.2.a)
	</p>

	<div class="matchrow">
		<span class="mw"
			><span class="mwl">Match winner</span><span class="opt"><span class="cb p1"></span> P1</span
			><span class="opt"><span class="cb p2"></span> P2</span></span
		>
		<span class="score"
			><span class="micro">Games</span><span class="sfill"></span><span class="dash">–</span><span
				class="sfill"
			></span></span
		>
	</div>

	<div class="sec"><span>Notes</span></div>
	<div class="notes dots"></div>
</SheetFrame>

<style>
	/* Drie games strak naast elkaar op gelijke hoogte — de RiftHub-trap is op
	   Sjoerds verzoek geschrapt (twee pogingen bleven rommelig ogen); het
	   eigen karakter van variant B zit in de kleurbanden en het grote
	   notes-vlak. */
	.games {
		display: grid;
		grid-template-columns: repeat(3, 1fr);
		gap: 2.6mm;
		align-items: start;
	}

	.game {
		border: 0.3mm solid var(--paper-line);
		border-radius: 1.2mm;
		padding: 1.6mm 1.8mm;
		display: flex;
		flex-direction: column;
	}
	.gnum {
		font-size: 6.6pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.08em;
		margin-bottom: 1.2mm;
	}

	/* YOU/OPP-banden: gevulde balkjes in de spelerkleuren met papier-witte
	   tekst; in bw-modus remapt SheetFrame de kleuren naar inkt en blijft de
	   tekst leesbaar. De .bn-spacer is exact de .tn-breedte van de track. */
	.bands {
		display: flex;
		align-items: center;
		margin-bottom: 1.2mm;
	}
	.band {
		flex: 1;
		height: 3.5mm;
		border-radius: 0.8mm;
		display: flex;
		align-items: center;
		justify-content: center;
		font-size: 5.2pt;
		font-weight: 800;
		text-transform: uppercase;
		letter-spacing: 0.1em;
		color: var(--paper);
	}
	.band.b1 {
		background: var(--paper-p1);
	}
	.band.b2 {
		background: var(--paper-p2);
	}
	.bn {
		flex: none;
		width: 4.6mm;
	}

	/* Rijen als échte rijen (flex): één doorlopende scheidingslijn per rij —
	   losse cel-randjes lopen door hoogteverschillen nooit strak (#342,
	   zelfde patroon als variant A). */
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

	/* Ruime gap tussen de P1/P2-paren zodat label en box als paar lezen. */
	.gwin {
		display: flex;
		align-items: center;
		justify-content: center;
		gap: 2.5mm;
		padding-top: 1.4mm;
	}
	.fp {
		display: inline-flex;
		align-items: center;
		gap: 0.7mm;
	}

	.matchrow {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: 4mm;
		margin-top: 2.2mm;
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
	/* Score als "n – n": baseline-uitlijning zet de onderkant van de lege
	   invullijntjes op de tekst-baseline, zelfde truc als .sh-field. */
	.score {
		display: inline-flex;
		align-items: baseline;
		gap: 1.6mm;
	}
	.sfill {
		width: 9mm;
		height: 4.2mm;
		border-bottom: 0.3mm solid var(--paper-line);
	}
	.dash {
		color: var(--paper-muted);
	}

	.legend {
		margin: 1.4mm 0 0;
		font-size: 6pt;
		color: var(--paper-muted);
		letter-spacing: 0.03em;
	}

	/* Het grote notitievlak is het kenmerk van deze indeling: alle resthoogte
	   van het vel gaat hierheen. */
	.notes {
		flex: 1;
		min-height: 40mm;
		margin-bottom: 1mm;
	}
</style>
