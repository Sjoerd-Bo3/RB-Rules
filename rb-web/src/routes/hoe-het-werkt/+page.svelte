<script lang="ts">
	import { useShell } from '$lib/shell.svelte';

	let { data } = $props();
	const shell = useShell();
	const ont = $derived(data.ontology);

	// Alleen de relaties die als kale edge bestaan; de gekwalificeerde
	// (gereïficeerde) staan in hun eigen blok, want die zijn juist het punt
	// van paragraaf 5.
	const kaleRelaties = $derived(ont ? ont.relaties.filter((r) => !r.gereificeerd) : []);
	const gekwalificeerd = $derived(ont ? ont.relaties.filter((r) => r.gereificeerd) : []);

	const HOOFDSTUKKEN = [
		{ id: 'bronnen', label: '1. Bronnen binnenhalen' },
		{ id: 'opdelen', label: '2. Opdelen in stukken' },
		{ id: 'zoeken', label: '3. Twee manieren van zoeken' },
		{ id: 'ontologie', label: '4. De ontologie' },
		{ id: 'graaf', label: '5. De kennisgraaf' },
		{ id: 'samen', label: '6. Vector en graaf samen' },
		{ id: 'vertrouwen', label: '7. Wat mag een antwoord dragen' },
		{ id: 'antwoord', label: '8. Het antwoord' },
		{ id: 'koppelen', label: '9. Grafen aan elkaar knopen' },
		{ id: 'grenzen', label: 'Waar het niet voor is' }
	];

	const nf = new Intl.NumberFormat('nl-NL');

	$effect(() => {
		shell.rail = { snippet: rail, kind: 'context', title: 'Op deze pagina' };
		return () => (shell.rail = null);
	});
</script>

<svelte:head>
	<title>Hoe het werkt — Poracle</title>
	<meta
		name="description"
		content="Van officiële bron tot antwoord: hoe Poracle regels, kaarten en rulings inleest, doorzoekbaar maakt met vectoren en een kennisgraaf, en wat een antwoord mag dragen."
	/>
</svelte:head>

{#snippet rail()}
	<nav class="rail-nav">
		{#each HOOFDSTUKKEN as h (h.id)}
			<a href="#{h.id}">{h.label}</a>
		{/each}
	</nav>
	<div class="rail-block">
		<p class="rail-h">Verder lezen</p>
		<a class="rail-link" href="/graph">Het brein verkennen</a>
		<a class="rail-link" href="/rules">De regels zelf</a>
		<a class="rail-link" href="/ask">Een vraag stellen</a>
	</div>
{/snippet}

<main>
	<header class="intro">
		<p class="eyebrow">Uitleg</p>
		<h1>Hoe Poracle werkt</h1>
		<p class="lead">
			Poracle beantwoordt Riftbound-regelvragen. Niet door een taalmodel te laten gokken, maar
			door eerst de officiële bronnen binnen te halen, ze doorzoekbaar te maken op betekenis én
			op letter, ze aan elkaar te knopen in een kennisgraaf, en pas dán een antwoord te laten
			formuleren — met de bron eronder. Deze pagina legt die keten stap voor stap uit.
		</p>

		<div class="tiles">
			<div class="tile">
				<span class="tile-n tnum">{nf.format(data.stats.cards)}</span>
				<span class="tile-l">kaarten</span>
			</div>
			<div class="tile">
				<span class="tile-n tnum">{nf.format(data.stats.verifiedRulings)}</span>
				<span class="tile-l">geverifieerde rulings</span>
			</div>
			<div class="tile">
				<span class="tile-n tnum">{nf.format(data.stats.bans)}</span>
				<span class="tile-l">ban-vermeldingen</span>
			</div>
			<div class="tile">
				<span class="tile-n tnum">{nf.format(data.stats.recentChanges)}</span>
				<span class="tile-l">wijzigingen (14 dagen)</span>
			</div>
		</div>
		<p class="meta small">
			Deze cijfers komen rechtstreeks uit de database, de ontologie-tabellen verderop rechtstreeks
			uit het schema in de code. De uitleg eromheen is met de hand geschreven.
		</p>
	</header>

	<section class="kort">
		<h2>In het kort</h2>
		<ol class="kort-list">
			<li>We halen de officiële regels, kaarten, bans en errata automatisch op en zien meteen wát er veranderde.</li>
			<li>Alles wordt in kleine stukken geknipt en op twee manieren doorzoekbaar gemaakt: op woord en op betekenis.</li>
			<li>Een ontologie legt vast welke soorten dingen er bestaan en welke verbanden tussen welke soorten mogen lopen.</li>
			<li>Die verbanden vormen een graaf, waarin nieuwe feiten afgeleid kunnen worden die niemand heeft ingevoerd.</li>
			<li>Bij een vraag zoekt de vector het startpunt en levert de graaf de onderbouwing — met bronvermelding en een eerlijk label als iets géén officiële bron heeft.</li>
		</ol>
	</section>

	<!-- 1 ─────────────────────────────────────────────────────────────── -->
	<section id="bronnen">
		<h2>1. Bronnen binnenhalen</h2>
		<p>
			Alles begint bij Riot zelf. Poracle bezoekt de officiële Rules Hub en zoekt daar elke ronde
			opnieuw de actuele Core Rules- en Tournament Rules-PDF's op, want die links wisselen per
			versie. De banlijst leest Poracle uit de tekst van de Rules Hub zelf; de errata en patch
			notes komen uit de officiële nieuwsartikelen per set, en de kaartgegevens uit de officiële
			kaartgallery. Dat gebeurt op een vaste ronde, niet met de hand.
		</p>
		<p>
			Belangrijk detail: we bewaren niet alleen de nieuwste versie, maar ook wát er veranderde.
			Elke ronde wordt de nieuwe tekst vergeleken met de vorige; verschilt er iets, dan komt dat
			als <a href="/wijzigingen">wijziging</a> in de feed met bron, datum en een voor/na-diff.
			Zo is niet alleen te zien wat de regel nú is, maar ook sinds wanneer en waarom.
		</p>
		<div class="note">
			<p>
				<strong>Waarom dit zo strak moet.</strong> De Rules Hub geeft de artikellinks per
				bezoek in een andere volgorde terug. Zonder tegenmaatregel meldt de feed elke ronde
				een "wijziging" die er geen is. Poracle houdt daarom een hash-historie bij en
				onderdrukt zulke heen-en-weer-veranderingen.
			</p>
		</div>
	</section>

	<!-- 2 ─────────────────────────────────────────────────────────────── -->
	<section id="opdelen">
		<h2>2. Opdelen in stukken</h2>
		<p>
			Een regelboek van honderden pagina's is als geheel nutteloos voor een zoekopdracht: je
			wilt § 601.2.d, niet "het hoofdstuk waarin dat ergens staat". De PDF wordt daarom
			opgeknipt in genummerde secties. Elke sectie krijgt haar eigen adres, haar plaats in de
			hiërarchie (601 → 601.2 → 601.2.d) en het paginanummer waarop ze in de officiële PDF
			staat.
		</p>
		<p>
			Daardoor kan elk citaat later doorlinken naar precies de juiste
			<a href="/rules">regelpagina</a> én naar de bijbehorende pagina in de officiële PDF. Wie
			het antwoord niet vertrouwt, klikt door naar de bron — dat is het hele punt.
		</p>
	</section>

	<!-- 3 ─────────────────────────────────────────────────────────────── -->
	<section id="zoeken">
		<h2>3. Twee manieren van zoeken</h2>
		<p>
			Zoeken op woorden werkt prima zolang de vraag dezelfde woorden gebruikt als de regel.
			Dat is zelden zo. Iemand vraagt "kan ik dat blokken?" terwijl de regel het over
			<em>Deflect</em> heeft. Letterlijk zoeken vindt dan niets. Daarom doet Poracle het op twee
			manieren tegelijk.
		</p>

		<div class="split">
			<div class="box">
				<h3>Letterlijk (full-text)</h3>
				<p>
					De klassieke variant: Postgres indexeert alle woorden en vindt exacte termen,
					kaartnamen en codes. Sterk waar precisie telt — "§ 601.2" of "Teemo" moet gewoon
					de juiste treffer geven.
				</p>
			</div>
			<div class="box">
				<h3>Op betekenis (vector)</h3>
				<p>
					Elk tekstfragment gaat door een embedding-model (bge-m3, lokaal) dat er een rij
					van 1024 getallen van maakt. Teksten met een vergelijkbare betekenis krijgen
					rijen die dicht bij elkaar liggen. Zoeken is dan: maak van de vraag óók zo'n rij
					en pak wat er het dichtst bij ligt.
				</p>
			</div>
		</div>

		<h3>Wat een vector-database precies doet</h3>
		<p>
			Het idee klinkt abstract, maar de intuïtie is eenvoudig. Stel dat elke tekst een punt op
			een landkaart is. Teksten die over hetzelfde gaan komen in dezelfde streek te liggen,
			ook als ze andere woorden gebruiken. "Blokken", "Deflect" en "aanval voorkomen" belanden
			bij elkaar in de buurt. Zoeken op betekenis is dan simpelweg: zet de vraag ook op die
			kaart en kijk wie de buren zijn.
		</p>
		<p>
			Die "kaart" heeft in werkelijkheid 1024 dimensies in plaats van twee, en de opslag zit in
			Postgres met de pgvector-uitbreiding. Om niet elke keer alle punten te hoeven vergelijken
			ligt er over de grootste verzamelingen — regels, kaarten en community-inzichten — een
			HNSW-index: een gelaagd netwerk van kortere en langere sprongen, waarmee de buren gevonden
			worden zonder de hele verzameling af te lopen. De twee kleinere verzamelingen, spelbegrip
			en rulings, hebben zo'n index niet: die zijn kort genoeg om nog gewoon punt voor punt te
			vergelijken.
		</p>
		<div class="note">
			<p>
				<strong>Eén model, één maat.</strong> Vectoren van verschillende modellen zijn
				onvergelijkbaar — hetzelfde woord komt bij een ander model ergens anders te liggen.
				Poracle legt daarom bij de vectoren vast welk model ze maakte, en de kolom heeft een
				vaste maat: een model met een andere dimensie wordt hard geweigerd in plaats van er
				stilletjes naast te gaan liggen. Wisselt het model, dan is opnieuw berekenen een
				expliciete stap: kaarten pakt de embed-job vanzelf op, de andere lagen volgen bij hun
				eerstvolgende herindexering of hergeneratie.
			</p>
		</div>

		<h3>De twee lijsten samenvoegen</h3>
		<p>
			Beide zoekwijzen leveren een eigen ranglijst. Die worden gecombineerd met Reciprocal Rank
			Fusion: elk resultaat krijgt punten op basis van zijn plaats in elke lijst
			(1&nbsp;/&nbsp;(60&nbsp;+&nbsp;positie)), en de punten worden opgeteld. Het effect is dat
			hoge posities zwaarder wegen, maar geen enkele lijst de uitslag alleen bepaalt. Iets dat
			in beide lijsten redelijk scoort wint van iets dat in één lijst toevallig bovenaan staat.
		</p>
		<p>
			Dat zoeken gebeurt over vijf gescheiden kennislagen — regels, kaarten,
			<a href="/primer">spelbegrip</a>, <a href="/rulings">rulings</a> en community-inzichten —
			elk met een eigen verzameling vectoren. Welke laag zwaarder meetelt hangt af van het
			soort vraag: bij een legaliteitsvraag telt de banlijst, bij een definitievraag de
			regeltekst.
		</p>
	</section>

	<!-- 4 ─────────────────────────────────────────────────────────────── -->
	<section id="ontologie">
		<h2>4. De ontologie: de afspraak over wat er bestaat</h2>
		<p>
			Voordat je dingen aan elkaar kunt knopen, moet vastliggen wát voor dingen het zijn. Dat
			is een ontologie: een expliciete lijst van soorten (klassen) en van de verbanden die
			tussen welke soorten mogen lopen. Zonder die afspraak groeit een graaf vanzelf scheef —
			dan staat "Deflect" er drie keer in als drie verschillende soorten dingen, en klopt geen
			enkele telling meer.
		</p>
		<p>
			Bij Poracle staat die afspraak in code, op één plek, en is ze machine-leesbaar. Uit
			diezelfde bron komen de validatieregels, de afleidingsregels en de tabel hieronder. Een
			verband over Riftbound zelf dat hier niet staat, bestaat niet. Wat de graaf daarnaast nog
			vastlegt is herkomst en eigen boekhouding — welke bron een bewering staaft, uit welke
			ronde ze komt, welke entiteit met welke is samengevoegd — en dat valt bewust buiten de
			ontologie, omdat het niets over het spel beweert.
		</p>

		{#if ont}
			<p class="meta small">
				Ontologie-versie <strong>{ont.versie}</strong> — {ont.klassen.length} klassen,
				{ont.relaties.length} relaties. Live opgehaald uit de API, dus deze tabel veroudert niet.
			</p>

			<h3>Relaties die als directe verbinding bestaan</h3>
			<div class="table-wrap">
				<table>
					<thead>
						<tr><th>Relatie</th><th>Van</th><th>Naar</th><th>Aantal</th></tr>
					</thead>
					<tbody>
						{#each kaleRelaties as r (r.edge)}
							<tr>
								<td><code>{r.edge}</code></td>
								<td>{r.van.join(', ') || '—'}</td>
								<td>{r.naar.join(', ') || '—'}</td>
								<td class="tnum">{r.kardinaliteit}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			<p class="meta small">
				"Aantal" is de kardinaliteit: <code>1..*</code> betekent minstens één en verder
				onbeperkt, <code>0..1</code> hooguit één.
			</p>

			{#if gekwalificeerd.length}
				<h3>Relaties die nooit een kale verbinding mogen zijn</h3>
				<p>
					Sommige verbanden zijn nooit onvoorwaardelijk waar. "Kaart A countert kaart B"
					klopt alleen binnen een bepaald tijdvenster, bij een bepaalde toestand of vanaf
					een bepaalde kostendrempel. Zo'n verband wordt daarom niet als losse verbinding
					opgeslagen maar als een eigen knoop, met de voorwaarden eraan vast. Anders zou de
					graaf iets beweren dat in de helft van de gevallen onwaar is.
				</p>
				<ul class="chips">
					{#each gekwalificeerd as r (r.edge)}<li><code>{r.edge}</code></li>{/each}
				</ul>
			{/if}

			{#if ont.disjuncteParen.length}
				<h3>Wat niet tegelijk kan</h3>
				<p>
					De ontologie legt ook vast welke soorten elkaar uitsluiten. Een Spell is geen
					Object: hij lost op en verlaat het spel. Zulke uitsluitingen maken fouten
					vindbaar in plaats van onzichtbaar.
				</p>
				<ul class="chips">
					{#each ont.disjuncteParen as p (p)}<li>{p}</li>{/each}
				</ul>
			{/if}
		{:else}
			<p class="meta small">De ontologie-tabel is nu niet op te halen; de uitleg hierboven blijft gelden.</p>
		{/if}

		<h3>Wel een ontologie, geen triplestore</h3>
		<p>
			Wie zich in dit onderwerp inleest komt al snel de <em>triplestore</em> tegen: een database
			die kennis opslaat als losse drieslagen — onderwerp, relatie, object. "Kaart X — heeft
			mechaniek — Deflect." Daar hoort een hele wereld bij (RDF, SPARQL, OWL) met één groot
			voordeel: het is een W3C-standaard, dus twee organisaties kunnen elkaars kennis
			letterlijk uitwisselen.
		</p>
		<p>
			Poracle gebruikt die opslagvorm niet, maar wél de begrippen eruit. De graaf is een
			<em>property graph</em>: knopen en verbindingen dragen zelf eigenschappen, in plaats van
			dat elk feitje een aparte drieslag is. De ontologie hierboven is intussen wel degelijk in
			de taal van die wereld geschreven — klassen met overerving, relaties met een toegestaan
			domein en bereik, kardinaliteit, uitsluitingen, en eigenschappen als transitief en
			symmetrisch. De afleidingsregels uit paragraaf 5 zijn letterlijk OWL-achtige
			ketenregels, alleen uitgevoerd als graafquery in plaats van door een reasoner.
		</p>
		<div class="note">
			<p>
				<strong>Waarom die keuze.</strong> Precies om de gekwalificeerde relaties hierboven.
				In een triplestore kan een drieslag zelf geen eigenschappen dragen: wil je vastleggen
				dat "A countert B" alleen geldt binnen een bepaald tijdvenster, dan moet je die
				uitspraak opsplitsen in hulpknopen. Wat bij ons een bewuste modelleerkeuze is, zou
				daar een verplichting worden — en juist die voorwaarden zijn de kern van een
				regelvraag. Daar komt bij dat de graaf een projectie is: de waarheid staat in
				Postgres, en dat maakt het uitwisselformaat van de graaf een implementatiedetail in
				plaats van een fundament.
			</p>
		</div>

		<h3>De ontologie groeit mee</h3>
		<p>
			Elke set brengt nieuwe kaarten, nieuwe keywords en soms een heel nieuw soort ding. De
			ontologie is daarom geversioneerd, met vaste regels voor wat een versie-ophoging
			rechtvaardigt: nieuwe kaarten en keywords zijn gewoon nieuwe gevallen binnen het
			bestaande schema, een nieuw relatietype is een uitbreiding, en het splitsen van een
			klasse of het wijzigen van een uitsluiting is een breuk: het schema krijgt een nieuw
			hoofdversienummer, het teken dat alles wat al in de graaf staat opnieuw tegen dat schema
			getoetst moet worden. Wijzigt de structuur van het schema zonder dat de vastgelegde
			momentopname wordt bijgewerkt, dan faalt de build — precies zoals een databasemigratie
			dat doet. Bij dat bijwerken hoort de bewuste keuze welk deel van het versienummer
			opschuift.
		</p>
	</section>

	<!-- 5 ─────────────────────────────────────────────────────────────── -->
	<section id="graaf">
		<h2>5. De kennisgraaf</h2>
		<p>
			Met die afspraak op zak kan alles aan elkaar. Kaarten hangen aan hun set, hun domein,
			hun factie en hun mechanieken; regelsecties hangen aan hun bovenliggende sectie;
			primer-concepten leggen secties uit; errata wijzigen kaarten; rulings gaan érgens over.
			Dat geheel leeft in een graafdatabase (Neo4j) en is te doorlopen op
			<a href="/graph">de brein-pagina</a>.
		</p>
		<p>
			Eén principe is daarbij hard: Postgres blijft de bron van waarheid, de graaf is een
			projectie. Alles in de graaf is opnieuw op te bouwen uit de relationele data. Raakt de
			graaf beschadigd of loopt hij achter, dan is dat een herbouw — nooit dataverlies.
		</p>

		<h3>Feiten die niemand heeft ingevoerd</h3>
		<p>
			Het aardige van een graaf met een ontologie eronder is dat er conclusies uit volgen. Een
			voorbeeld: draagt een kaart de mechaniek <em>Deflect</em>, en definieert § 809.1 wat
			Deflect doet, dan valt die kaart onder die regel — zonder dat iemand dat feit intypt; het
			volgt uit twee andere feiten. De regels die zulke ketens aflopen worden automatisch uit de
			ontologie gegenereerd en staan klaar, maar juist deze keten levert vandaag nog niets op:
			de graaf legt de tweede stap — van mechaniek naar regelsectie — nog niet vast. Wat er nu
			wél uit volgt is de terugweg van een verbinding die maar één kant op is vastgelegd: werkt
			kaart A in op kaart B, dan geldt die verbinding ook andersom. Elke afgeleide verbinding
			draagt een stempel: welke regel hem maakte en in welke ronde. Afgeleide kennis is nooit
			bron; bij een herbouw wordt ze opnieuw afgeleid.
		</p>
		{#if ont && ont.inferenties.length}
			<p class="meta small">
				Op dit moment worden {ont.inferenties.length} afleidingsregels automatisch uit de ontologie
				gegenereerd — ze staan nergens los onderhouden.
			</p>
			<div class="table-wrap">
				<table>
					<thead><tr><th>Leidt af</th><th>Soort</th><th>Waaruit</th></tr></thead>
					<tbody>
						{#each ont.inferenties.slice(0, 12) as i (i.naam)}
							<tr>
								<td><code>{i.edge}</code></td>
								<td class="nowrap">{i.familie}</td>
								<td>{i.omschrijving}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if ont.inferenties.length > 12}
				<p class="meta small">Eerste 12 van {ont.inferenties.length} getoond.</p>
			{/if}
		{/if}
	</section>

	<!-- 6 ─────────────────────────────────────────────────────────────── -->
	<section id="samen">
		<h2>6. Vector en graaf samen</h2>
		<p>
			Vectoren en grafen lossen verschillende problemen op, en dat is precies waarom ze
			samenwerken. Een vector-zoekactie is goed in <em>vinden</em>: ze komt uit bij de tekst
			die op de vraag lijkt, zelfs als er geen woord overeenkomt. Maar ze kan niets
			<em>bewijzen</em> — "dit lijkt op elkaar" is geen redenering. Een graaf is omgekeerd:
			hij weet niets van formulering, maar kan wél een keten leggen van A naar B en die keten
			laten zien.
		</p>
		<div class="split">
			<div class="box">
				<h3>Vector-database</h3>
				<ul>
					<li>Vindt op betekenis, ook zonder woordovereenkomst</li>
					<li>Bestand tegen andere formuleringen en typefouten</li>
					<li>Levert gelijkenis, geen onderbouwing</li>
					<li>Kan geen "waarom" beantwoorden</li>
				</ul>
			</div>
			<div class="box">
				<h3>Kennisgraaf</h3>
				<ul>
					<li>Legt expliciete, benoemde verbanden vast</li>
					<li>Kan een keten van A naar B tonen als bewijs</li>
					<li>Leidt nieuwe feiten af uit bestaande</li>
					<li>Vindt niets als je het beginpunt niet kent</li>
				</ul>
			</div>
		</div>
		<p>
			De combinatie heet GraphRAG en werkt in die volgorde: <strong>de vector levert het
			startpunt, de graaf levert de onderbouwing.</strong> Een vraag wordt eerst gekoppeld aan
			concrete dingen die Poracle kent — een kaartnaam, een mechaniek, een sectie. Vanaf die
			ankers loopt de graaf uit over toegestane verbanden, en de gevonden knopen halen hun
			tekst weer op uit de kennislagen.
		</p>
		<p>
			Welke vorm dat uitlopen krijgt hangt af van de vraag. Een scherpe interactievraag over
			twee kaarten vraagt om een korte, gerichte uitbreiding rond die twee. Een brede vraag
			("hoe werkt de gevechtsfase in het algemeen?") heeft juist samenvattende concepten nodig
			in plaats van losse knopen. Een "waarom"-vraag vraagt om een pad tussen twee punten. En
			een ban-vraag heeft helemaal geen graaf nodig: de banlijst opzoeken is exacter.
		</p>
		<div class="note">
			<p>
				<strong>De kortste keten, met de stevigheid erbij.</strong> Als de graaf een keten als
				onderbouwing levert, zoekt hij de kortste verbinding tussen twee ankers en toont die
				stap voor stap. Bij elke stap legt Poracle vast hoe zwaar de betrokken kennislaag
				weegt en hoe zeker de verbinding is, zodat zichtbaar blijft waar een keten op zwak
				bewijs leunt — en zodat die weging meetelt in de vraag of een antwoord genoeg
				officiële dekking heeft. Het pad kiezen op die stevigheid in plaats van op lengte —
				elke stap kost dan 1&nbsp;/&nbsp;(betrouwbaarheid&nbsp;×&nbsp;zekerheid) — is
				uitgewerkt en getest, maar staat nog niet aan.
			</p>
		</div>
		<p class="meta small">
			De graafverrijking van de vraagbaak is een schakelaar in beheer. Staat hij uit, dan
			beantwoordt Poracle de vraag met de gelaagde zoekactie uit stap 3 — nooit met een fout.
		</p>
	</section>

	<!-- 7 ─────────────────────────────────────────────────────────────── -->
	<section id="vertrouwen">
		<h2>7. Wat een antwoord mag dragen</h2>
		<p>
			Niet alle kennis is gelijk. De officiële regels zijn iets anders dan een goed
			beargumenteerde forumpost, en dat verschil mag nooit wegvallen in de opmaak van een
			antwoord. Poracle labelt daarom elk opgehaald feit met zijn laag, en die volgorde is
			bindend.
		</p>
		<div class="table-wrap">
			<table>
				<thead><tr><th>Laag</th><th>Wat het is</th><th>Gewicht</th></tr></thead>
				<tbody>
					<tr><td>Officieel</td><td>Regels, kaartgegevens, toernooiregels</td><td class="tnum">1,00</td></tr>
					<tr><td>Geverifieerde ruling</td><td>Ruling of erratum, automatisch tegen de bron getoetst; twijfelgevallen eerst langs de beheerder</td><td class="tnum">0,85</td></tr>
					<tr><td>Spelbegrip</td><td>Uit de regels gedestilleerde uitleg</td><td class="tnum">0,65</td></tr>
					<tr><td>Community</td><td>Interpretatie van spelers, met bron en bijval</td><td class="tnum">0,45</td></tr>
					<tr><td>Meta</td><td>Tactiek en deckgebruik; veroudert het snelst</td><td class="tnum">0,25</td></tr>
				</tbody>
			</table>
		</div>
		<p>
			Dat gewicht is geen weegschaal waarop community-kennis wordt weggedrukt — het bepaalt de
			<em>route</em>. Is er officiële dekking voor de vraag, dan draagt die het antwoord en
			mag community-kennis hooguit kleuren, altijd zichtbaar gelabeld. Is die dekking er niet,
			dan mag een goed onderbouwde community-lezing het antwoord dragen, mét de eerlijke
			melding dat er geen officiële bron voor is. En is er ook dat niet, dan zegt Poracle dat
			het het niet weet. Dat laatste is een functie, geen tekortkoming: een verzonnen regel is
			schadelijker dan geen antwoord.
		</p>
		<p>
			Community-uitspraken worden bovendien niet één-op-één overgenomen. Ze worden
			geparafraseerd, voorzien van hun bron met citaat, getoetst aan de officiële tekst, en
			gewogen op hoeveel onafhankelijke bronnen hetzelfde zeggen. Eén losse post is een
			signaal; vijf bronnen die elkaar bevestigen is iets anders.
		</p>
	</section>

	<!-- 8 ─────────────────────────────────────────────────────────────── -->
	<section id="antwoord">
		<h2>8. Het antwoord</h2>
		<p>
			Pas als al het bovenstaande klaar is, komt het taalmodel in beeld — en dan met een
			strakke opdracht: schrijf uitsluitend op wat in de aangeleverde context staat, in de
			vorm die een scheidsrechter zou gebruiken.
		</p>
		<ul class="format">
			<li><strong>Oordeel</strong> — het antwoord in één zin</li>
			<li>
				<strong>Zekerheid</strong> — Bevestigd, Afgeleid, Community-consensus of Onzeker, met
				toelichting
			</li>
			<li><strong>Uitleg</strong> — de redenering, met [1], [2] op de plek waar ze op een fragment leunt</li>
			<li><strong>Let op</strong> — de randgevallen die het omdraaien</li>
		</ul>
		<p>
			De regelsecties zelf staan niet in het antwoord maar in de citatielijst eronder — bewust op
			één plek. Citaties zijn uitklapbaar en tonen naast de geciteerde sectie ook de bovenliggende
			regel, want een sectie los gelezen betekent vaak iets anders. Genoemde kaarten en regels
			verschijnen als aanklikbare blokken in plaats van kale tekst. En achter de schermen legt
			Poracle per vraag vast welke lagen meededen en hoe lang elke stap duurde — zichtbaar in
			beheer, zodat een slecht antwoord terug te herleiden is tot de stap waar het misging.
		</p>
		<p>
			Loopt er iets mis in een antwoord, dan is dat te melden. Zo'n melding komt in een
			reviewwachtrij; wordt ze goedgekeurd, dan wordt de ruling onderdeel van de kennisbank en
			doet ze mee bij volgende vragen. Het systeem leert dus van correcties in plaats van
			dezelfde fout te herhalen.
		</p>
	</section>

	<!-- 9 ─────────────────────────────────────────────────────────────── -->
	<section id="koppelen">
		<h2>9. Grafen aan elkaar knopen</h2>
		<p>
			Poracle's graaf is niet de enige denkbare graaf over Riftbound. Er kan er een bestaan
			over toernooien en uitslagen, een over decks en meta, een over kaartprijzen. De vraag is
			dan: hoe koppel je die aan elkaar zonder dat het één grote bak wordt waarin niemand meer
			weet wat waar vandaan komt?
		</p>
		<h3>Voorwaarde 1: stabiele identiteiten</h3>
		<p>
			Elk ding in Poracle heeft één vaste tekstuele naam, in dezelfde vorm in de
			vector-opslag, in de graaf en in de API — <code>card:ogn-011-298</code>,
			<code>mechanic:Deflect</code>, <code>section:core-rules-pdf/101.2</code>. Zonder zo'n
			stabiele verwijzing is koppelen onbegonnen werk: dan match je op namen, en namen
			veranderen, verschillen per taal en zijn niet uniek.
		</p>
		<p>
			Daar komt bij dat één kaart meerdere drukken kan hebben: alternatieve art, een herdruk in
			een latere set. Dat zijn geen verschillende kaarten. Poracle kiest per kaartnaam één
			canonieke druk en hangt de afgeleide kennis — mechanieken, rulings, relaties, ban-historie
			— daaraan. Een ban op één druk geldt daardoor voor álle drukken van dezelfde kaart.
		</p>
		<h3>Voorwaarde 2: gelijkheid als gewogen bewering</h3>
		<p>
			Twee grafen koppelen betekent zeggen: "dit ding hier is hetzelfde als dat ding daar."
			Zo'n uitspraak hoort zelf een feit te zijn, met een zekerheid en een herkomst — geen
			stille samensmelting. Blijkt de koppeling later fout, dan haal je één bewering weg in
			plaats van twee vervlochten datasets uit elkaar te pulken.
		</p>
		<h3>Twee manieren, met een duidelijke afweging</h3>
		<div class="split">
			<div class="box">
				<h3>Bevragen waar het staat</h3>
				<p>
					De andere graaf blijft van de ander; je stelt hem een vraag op het moment dat je
					het antwoord nodig hebt. Altijd actueel, geen kopie die veroudert — maar je bent
					afhankelijk van zijn beschikbaarheid en snelheid.
				</p>
			</div>
			<div class="box">
				<h3>Binnenhalen en bijhouden</h3>
				<p>
					Je haalt de feiten op en slaat ze zelf op, gelabeld met hun herkomst. Snel en
					altijd beschikbaar — maar het is een kopie, en kopieën lopen achter tenzij je ze
					actief ververst.
				</p>
			</div>
		</div>
		<p>
			De keuze hangt af van hoe snel de kennis veroudert. Regelteksten wijzigen zelden en
			lenen zich voor binnenhalen; toernooiuitslagen en prijzen veranderen doorlopend en kun
			je beter bevragen. Wat in beide gevallen geldt: het label van herkomst gaat mee. Kennis
			uit een externe graaf komt binnen op de laag die bij die bron past, niet als officiële
			regel.
		</p>
		<h3>Voorwaarde 3: één gedeelde begrippenlijst</h3>
		<p>
			De diepste voorwaarde is de saaiste: als de andere graaf "unit" gebruikt waar Poracle
			"Unit" bedoelt, of "kaart" voor iets dat hier "druk" heet, dan levert koppelen
			rommel op. Daarom is de ontologie uit stap 4 geen bureaucratie maar de aansluiting: ze
			maakt expliciet wat een term betekent, zodat een andere partij erop kan aansluiten of
			zijn eigen begrippen erop kan afbeelden. Dat is ook precies waarom ze publiek opvraagbaar
			is — de tabel op deze pagina komt uit diezelfde API.
		</p>
	</section>

	<!-- Grenzen ───────────────────────────────────────────────────────── -->
	<section id="grenzen">
		<h2>Waar het niet voor is</h2>
		<ul class="grenzen">
			<li>
				<strong>Geen officiële bron.</strong> Poracle is een onofficiële referentie en geen
				onderdeel van Riot Games. Bij twijfel geldt de officiële tekst, en daar linkt elk
				antwoord naartoe.
			</li>
			<li>
				<strong>Geen vervanging van een scheidsrechter.</strong> Op een toernooi beslist de
				aanwezige scheidsrechter. Poracle helpt bij voorbereiden en nazoeken.
			</li>
			<li>
				<strong>Geen orakel.</strong> Waar geen dekking is, zegt het systeem dat het het niet
				weet. Dat is de bedoeling.
			</li>
		</ul>
		<p class="afsluiter">
			Nieuwsgierig hoe dat er in de praktijk uitziet? <a href="/graph">Verken het brein</a> of
			<a href="/ask">stel een vraag</a>.
		</p>
	</section>
</main>

<style>
	main { max-width: 780px; margin: 0 auto; padding: 24px 20px 48px; }

	.intro { margin-bottom: 30px; }
	.eyebrow {
		font-size: 0.72rem; font-weight: 700; text-transform: uppercase;
		letter-spacing: 0.08em; color: var(--muted); margin: 0 0 6px;
	}
	h1 { margin: 0 0 12px; }
	.lead { font-size: 1.06rem; line-height: 1.7; color: var(--text); margin: 0 0 20px; }

	.tiles {
		display: grid; gap: 10px;
		grid-template-columns: repeat(auto-fit, minmax(130px, 1fr));
		margin-bottom: 6px;
	}
	.tile {
		background: var(--surface); border: 1px solid var(--border);
		border-radius: var(--radius); padding: 12px 14px;
		display: flex; flex-direction: column; gap: 2px; box-shadow: var(--shadow-card);
	}
	.tile-n { font-size: 1.35rem; font-weight: 700; letter-spacing: -0.02em; }
	.tile-l { font-size: 0.8rem; color: var(--muted); }

	.kort {
		background: var(--surface-deep); border: 1px solid var(--border);
		border-radius: var(--radius-lg); padding: 16px 20px; margin-bottom: 34px;
	}
	.kort h2 { margin-top: 0; }
	.kort-list { margin: 0; padding-left: 20px; display: grid; gap: 6px; }

	section[id] { scroll-margin-top: 20px; margin-bottom: 38px; }
	h2 {
		font-size: 1.18rem; margin: 0 0 12px;
		padding-bottom: 8px; border-bottom: 1px solid var(--border);
	}
	h3 { font-size: 0.98rem; margin: 22px 0 8px; color: var(--accent); }
	p { line-height: 1.68; }

	.split { display: grid; gap: 12px; grid-template-columns: 1fr 1fr; margin: 14px 0; }
	@media (max-width: 640px) { .split { grid-template-columns: 1fr; } }
	.box {
		background: var(--surface); border: 1px solid var(--border);
		border-radius: var(--radius); padding: 12px 16px; box-shadow: var(--shadow-card);
	}
	.box h3 { margin-top: 0; }
	.box p:last-child, .box ul:last-child { margin-bottom: 0; }
	.box ul { margin: 0; padding-left: 18px; display: grid; gap: 4px; font-size: 0.92rem; }

	.note {
		border-left: 3px solid var(--accent);
		background: var(--accent-soft);
		border-radius: 0 var(--radius) var(--radius) 0;
		padding: 2px 16px; margin: 16px 0;
	}
	.note p { margin: 12px 0; font-size: 0.93rem; }

	table { width: 100%; border-collapse: collapse; font-size: 0.88rem; }
	th, td {
		text-align: left; padding: 7px 10px;
		border-bottom: 1px solid var(--border); vertical-align: top;
	}
	th { color: var(--muted); font-weight: 600; white-space: nowrap; }
	code {
		font-size: 0.86em; background: var(--surface-deep);
		padding: 1px 5px; border-radius: 4px;
	}
	.nowrap { white-space: nowrap; }

	.chips { list-style: none; display: flex; flex-wrap: wrap; gap: 6px; margin: 10px 0; padding: 0; }
	.chips li {
		background: var(--surface); border: 1px solid var(--border);
		border-radius: 999px; padding: 3px 11px; font-size: 0.85rem;
	}

	.format { margin: 12px 0; padding-left: 20px; display: grid; gap: 5px; }
	.grenzen { margin: 12px 0; padding-left: 20px; display: grid; gap: 10px; }
	.afsluiter { margin-top: 20px; }

	.meta { color: var(--muted); }
	.small { font-size: 0.82rem; }

	/* Rail */
	.rail-nav { display: flex; flex-direction: column; gap: 2px; margin-bottom: 18px; }
	.rail-nav a {
		color: var(--muted); text-decoration: none; font-size: 0.88rem;
		padding: 5px 8px; border-radius: 6px; border-left: 2px solid var(--border);
	}
	.rail-nav a:hover { color: var(--text); border-left-color: var(--accent); background: var(--surface-deep); }
	.rail-block { border-top: 1px solid var(--border); padding-top: 12px; display: grid; gap: 6px; }
	.rail-h {
		font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em;
		color: var(--muted); margin: 0 0 2px;
	}
	.rail-link { color: var(--accent); text-decoration: none; font-size: 0.88rem; font-weight: 600; }
</style>
