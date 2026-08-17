<script lang="ts">
	let { data } = $props();

	const RELATION_LABELS: Record<string, string> = {
		Card: 'Kaart',
		Set: 'Set',
		Domain: 'Domein',
		Tag: 'Tag',
		Mechanic: 'Mechaniek',
		RuleSection: 'Regelsectie',
		Concept: 'Spelbegrip-concept',
		Erratum: 'Erratum',
		BanEntry: 'Banlijst-item'
	};
	const label = (t: string) => RELATION_LABELS[t] ?? t;

	const medianSeconds = $derived(
		data.stats?.medianMs ? Math.round(data.stats.medianMs / 1000) : null
	);
</script>

<svelte:head>
	<title>Hoe het werkt — RB Rules</title>
	<meta
		name="description"
		content="Hoe Riftbound Rules Companion officiële regels, kaarten en spelbegrip combineert tot controleerbare rulings."
	/>
</svelte:head>

<main>
	<h1>Hoe het <span>werkt</span></h1>
	<p class="lead">
		Deze site beantwoordt regelvragen over Riftbound met antwoorden die je kunt controleren:
		elk oordeel verwijst naar de officiële regeltekst waar het op leunt. Hieronder staat
		precies hoe dat gebeurt, van bron tot antwoord.
	</p>

	<section>
		<h2>1. Bronnen binnenhalen</h2>
		<p>
			Een achtergrondtaak controleert automatisch de officiële bronnen: de Core Rules en
			Tournament Rules (PDF), de Rules Hub, de learn-to-play-artikelen van Riot en de
			kaartendatabase. Van elk document bewaren we een momentopname. Verandert er iets, dan
			zie je op de <a href="/">wijzigingen-pagina</a> precies wat er is toegevoegd of
			verdwenen, met een duiding van de impact.
		</p>
		<p class="meta">
			Bronnen hebben een betrouwbaarheidsniveau. Officiële documenten wegen altijd zwaarder
			dan afgeleide of community-informatie.
		</p>
	</section>

	<section>
		<h2>2. Opdelen in secties</h2>
		<p>
			De regeldocumenten worden gesplitst op hun eigen nummering, zodat § 601.2.d een
			zelfstandig stuk wordt met een deelbare link en een verwijzing naar de juiste pagina in
			de officiële PDF. Die nummering vormt ook een boom: 601.2.d hoort onder 601.2, dat weer
			onder 601 hoort. Daarom kan een citaat zijn bovenliggende regel meetonen — anders weet
			je niet waar "deze" of "dat effect" naar verwijst.
		</p>
		<p><a href="/rules">Blader door de regels</a></p>
	</section>

	<section>
		<h2>3. Twee manieren van zoeken</h2>
		<p>
			Iemand die een vraag stelt gebruikt zelden de woorden uit het regelboek. Daarom zoeken
			we op twee manieren tegelijk, en combineren we de uitkomsten.
		</p>
		<div class="split">
			<div class="panel">
				<h3>Betekenis (vectoren)</h3>
				<p>
					Elke regelsectie en kaart is omgezet naar een reeks getallen die de
					<em>betekenis</em> vastlegt. Een vraag wordt op dezelfde manier omgezet, waarna
					we zoeken naar de dichtstbijzijnde teksten. Zo vindt "mag ik reageren als hij
					aanvalt" de juiste sectie, ook als daar heel andere woorden staan — en werkt een
					Nederlandse vraag op Engelse regels.
				</p>
			</div>
			<div class="panel">
				<h3>Woorden (full-text)</h3>
				<p>
					Daarnaast zoeken we klassiek op de woorden zelf. Dat is scherp waar
					betekeniszoeken vaag wordt: exacte keywords, kaartnamen en §-nummers. De twee
					ranglijsten worden samengevoegd, zodat een sectie die in beide voorkomt bovenaan
					eindigt.
				</p>
			</div>
		</div>
	</section>

	<section>
		<h2>4. De kennisgraaf</h2>
		<p>
			Zoeken levert losse fragmenten. Om te begrijpen hoe dingen samenhángen, leggen we alle
			kennis ook vast als een netwerk van punten en verbindingen: kaarten, sets, domeinen,
			mechanieken, regelsecties, spelbegrip-concepten, errata en banlijst-items — met daartussen
			relaties die zeggen wát ze met elkaar te maken hebben.
		</p>
		<p>
			Het bijzondere is dat de graaf feiten kan <strong>afleiden</strong> die nergens zijn
			ingevoerd. Draagt een kaart de mechaniek Deflect, en is er een regelsectie die Deflect
			definieert, dan volgt daaruit dat die sectie deze kaart beheerst. Dat verband is nooit
			door iemand opgeschreven; het komt uit de structuur. Bij een vraag over zo'n kaart
			halen we die regels er automatisch bij.
		</p>
		<p><a href="/graph">Verken de graaf</a></p>

		{#if data.ontology}
			<h3>Wat er in de graaf zit</h3>
			<p class="meta">
				Punttypen: {data.ontology.nodeTypes.map(label).join(' · ')}
			</p>
			<div class="table-wrap">
				<table>
					<thead>
						<tr><th>Verbinding</th><th>Van</th><th>Naar</th><th>Betekenis</th></tr>
					</thead>
					<tbody>
						{#each data.ontology.edges as e (e.type + e.from + e.to)}
							<tr>
								<td><code>{e.type}</code>{#if e.inferred}<span class="badge">afgeleid</span>{/if}</td>
								<td>{label(e.from)}</td>
								<td>{label(e.to)}</td>
								<td class="meta">{e.description}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			<p class="meta small">
				Identiteitsregel: {data.ontology.identityRule}. Alternatieve illustraties en
				herdrukken van dezelfde kaart zijn één punt in de graaf, want ze zijn in het spel
				dezelfde kaart.
			</p>
		{/if}
	</section>

	<section>
		<h2>5. Spelbegrip als fundament</h2>
		<p>
			Losse regelsecties missen het grotere plaatje. Daarom staat er een set korte
			achtergrondteksten klaar over de beurtstructuur, resources, combat, prioriteit, zones en
			de kernbegrippen — gedestilleerd uit de officiële regels, met verwijzingen naar de
			secties waarop ze zijn gebaseerd, en met de hand nagekeken voordat ze meetellen. Bij elke
			vraag gaan de meest relevante daarvan mee als achtergrond.
		</p>
	</section>

	<section>
		<h2>6. Het antwoord</h2>
		<p>
			Al die context gaat naar het taalmodel met een vaste opdracht: geef een oordeel als een
			scheidsrechter. Het antwoord begint altijd met het oordeel zelf en een
			<strong>zekerheidslabel</strong>, gevolgd door de redenering in spelvolgorde en de
			regels waarop die leunt.
		</p>
		<ul class="certainty">
			<li><strong>Bevestigd</strong> — de geciteerde regels dekken dit letterlijk.</li>
			<li><strong>Afgeleid</strong> — het volgt logisch uit de regels, maar staat er niet met zoveel woorden.</li>
			<li><strong>Onzeker</strong> — de benodigde regeltekst ontbreekt; er staat bij wat er nodig is.</li>
		</ul>
		<p>
			Het model mag niets beweren dat niet in de meegegeven context staat. Klopt er iets niet,
			dan kun je onder het antwoord een correctie insturen; na controle stuurt die correctie
			toekomstige antwoorden over hetzelfde onderwerp.
		</p>
		{#if medianSeconds && data.stats?.count}
			<p class="meta">
				Een antwoord kost meestal ongeveer {medianSeconds} seconden, gemeten over de laatste
				{data.stats.count} vragen.
			</p>
		{/if}
		<p><a href="/ask">Stel een vraag</a></p>
	</section>

	<section>
		<h2>Waar het niet voor is</h2>
		<p>
			Dit is een hulpmiddel, geen officiële uitspraak. Bij een toernooi beslist de judge, en
			de officiële documenten van Riot zijn altijd leidend. Elk antwoord linkt daarom naar de
			bron, zodat je zelf kunt nakijken waar het op gebaseerd is.
		</p>
	</section>
</main>

<style>
	main { max-width: 820px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: var(--accent); }
	.lead { font-size: 1.05rem; line-height: 1.65; }
	section { margin-top: 34px; }
	h2 {
		color: var(--accent); font-size: 1.15rem;
		border-bottom: 1px solid var(--border); padding-bottom: 6px; margin-bottom: 10px;
	}
	h3 { font-size: 0.98rem; margin: 18px 0 6px; }
	p { line-height: 1.7; }
	.meta { color: var(--muted); }
	.small { font-size: 0.85rem; }
	.split { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); }
	.split .panel { padding: 14px 16px; }
	.split .panel h3 { margin-top: 0; color: var(--text); }
	.split p { font-size: 0.92rem; margin: 0; }
	.certainty { line-height: 1.8; padding-left: 20px; }
	table { width: 100%; border-collapse: collapse; font-size: 0.88rem; }
	th, td { text-align: left; padding: 7px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
	th { color: var(--muted); font-size: 0.8rem; }
	code {
		background: var(--surface-deep); border: 1px solid var(--border);
		border-radius: 5px; padding: 1px 6px; font-size: 0.85em;
	}
	.badge {
		margin-left: 6px; font-size: 0.65rem; text-transform: uppercase;
		letter-spacing: 0.05em; background: var(--ok-soft); color: var(--ok);
		border-radius: 999px; padding: 1px 7px;
	}
	a { color: var(--accent); }
</style>
