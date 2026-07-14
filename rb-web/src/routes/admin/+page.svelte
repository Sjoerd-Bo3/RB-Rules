<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import AnswerView from '$lib/AnswerView.svelte';

	let { data, form } = $props();

	interface Source {
		id: string; name: string; url: string; trustTier: number;
		cadence: string; enabled: boolean; lastChecked: string | null;
		// Herkomst (#167): welke feed dit artikel ontdekte; null = handmatig/legacy.
		feedId: string | null;
	}
	// Naam + id volstaat voor de "stamt van: …"-koppeling hieronder; het
	// volledige beheer (toevoegen/bewerken/verwijderen) zit op de eigen
	// overzichtspagina (#167).
	interface FeedRef { id: string; name: string; }
	interface Job {
		name: string; status: string; startedAt: string;
		finishedAt: string | null; detail: string | null; progress: string | null;
	}
	interface Log { id: number; kind: string; ref: string | null; status: string; detail: string | null; createdAt: string; }
	// Laatste afronding per job uit het run_log-grootboek (#122): overleeft
	// een herstart en toont ook de automatische runs van de scheduler.
	interface JobRun { name: string; status: string; at: string; }
	interface Status {
		running: Job | null;
		lastJob: Job | null;
		jobRuns?: JobRun[];
		counts: Record<string, number>;
		logs: Log[];
	}

	const JOBS: { name: string; label: string; hint: string }[] = [
		{ name: 'all', label: 'Alles bijwerken', hint: 'alle stappen in de juiste volgorde' },
		{ name: 'scan', label: 'Bronnen scannen', hint: 'regels en changelogs ophalen en vergelijken' },
		{ name: 'cards', label: 'Kaarten synchroniseren', hint: 'nieuwe sets en reveals binnenhalen' },
		{ name: 'embed', label: 'Embeddings berekenen', hint: 'voedt het semantisch zoeken' },
		{ name: 'mine', label: 'Mechanieken analyseren', hint: 'LLM-analyse van kaartteksten' },
		{ name: 'rules', label: 'Regels indexeren', hint: 'sectie-chunks en embeddings opbouwen' },
		{ name: 'bans', label: 'Bans en errata structureren', hint: 'uit de officiële documenten' },
		{ name: 'primer', label: 'Primer genereren', hint: 'spelbegrip-docs destilleren uit de regels (drafts ter review)' },
		{ name: 'graph', label: 'Graph synchroniseren', hint: 'Neo4j bijwerken' },
		{ name: 'interactions', label: 'Interacties minen', hint: 'kandidaten zoeken en LLM-verifiëren' },
		{ name: 'setrelease', label: 'Set-release-keten', hint: 'kaarten → mechanieken → embeddings → graph → primer-herziening; draait automatisch zodra de classifier een set-release herkent' },
		{ name: 'classify', label: 'Classificaties aanvullen', hint: 'changes zonder samenvatting/duiding alsnog classificeren' },
		{ name: 'claims', label: 'Claims minen', hint: 'community-claims destilleren uit registerbronnen (trust 3+), met corroboratie en toets tegen de officiële regels' },
		{ name: 'clarify', label: 'FAQ-concepten minen', hint: 'losse verduidelijkingen uit officiële FAQ-/clarificatie-artikelen (trust 1) destilleren als geverifieerde rulings met eigen gefocuste embedding — pakt ook al-geïngeste bronnen mee; draait ook elke nacht automatisch' },
		{ name: 'relations', label: 'Relaties minen', hint: 'LLM ontdekt relaties tussen de kennislagen (concepten, mechanieken, secties, kaarten, claims); voorstellen en nieuwe kind-labels komen in de reviewqueue — nooit rechtstreeks de graph in; draait ook elke nacht automatisch' },
		{ name: 'scout', label: 'Bronnen zoeken (web)', hint: 'rb-ai doorzoekt het web naar nieuwe regelbronnen; vondsten komen als voorstel in de reviewqueue, nooit automatisch in het register; draait ook wekelijks automatisch' },
		{ name: 'decks', label: 'Decks binnenhalen', hint: 'publieke decks van Piltover Archive via de sitemap (robots-compliant, met bronvermelding); throttled en gecapt per run — een volgende run gaat verder waar het grootboek gebleven is; draait ook elke 3 uur automatisch' },
		{ name: 'benchmark', label: 'Judge-benchmark draaien', hint: 'de vaste scheidsrechter-vragenset één keer door de /ask-pipeline (standaardmodel); geïsoleerd van de kennisbank — geen trace, metric of relatie-terugkoppeling' },
		{ name: 'benchmarksweep', label: 'Model-sweep draaien', hint: 'dezelfde vragenset door élk beschikbaar model (AI_BENCHMARK_MODELS), elk 2× — voor een eerlijke score/tijd-vergelijking en een consistentie-check; kostbaar (N modellen × 2 × vragen ask-aanroepen), geschatte omvang verschijnt in het log zodra de job start' },
		{ name: 'feeds', label: 'Bron-feeds afspeuren', hint: 'nieuwe artikelen op de Riot-nieuwspagina\'s ontdekken; vertrouwde feeds zetten direct een bron, andere een voorstel — draait ook als eerste stap van "Bronnen scannen"' }
	];

	interface Correction {
		id: number; scope: string; ref: string; text: string;
		question: string | null; status: string; createdAt: string;
	}

	interface AskTrace {
		id: number; question: string; questionType: string | null;
		sourceBias: string | null; mentionsCard: boolean;
		mechanicMatches: string | null; sections: string | null;
		contextCards: string | null; primerDocs: string | null;
		communityClaims: string | null; verifiedRulings: number;
		model: string | null; hadImage: boolean; durationMs: number;
		phaseTimings: string | null;
		agentic: boolean; escalatedBy: string | null; brainSteps: string | null;
		ok: boolean; createdAt: string;
	}

	// Fase-timings (#152): compacte JSON van de trace naar één leesbare regel;
	// kapotte of ontbrekende JSON degradeert naar een streepje.
	function phaseLine(json: string | null): string {
		if (!json) return '—';
		try {
			const p = JSON.parse(json);
			const s = (ms: unknown) => `${((typeof ms === 'number' ? ms : 0) / 1000).toFixed(1)}s`;
			return `rewrite ${s(p.rewriteMs)} · embed ${s(p.embedMs)} · retrieval ${s(p.retrievalMs)} · AI ${s(p.aiMs)} (fasen overlappen)`;
		} catch {
			return '—';
		}
	}

	// Het volledige gesprek achter een trace (#143): de lijst is slank,
	// antwoord + eerdere beurten komen lazy uit het detail-endpoint.
	interface AskTraceTurn { question: string; answer: string }
	interface AskTraceDetail { id: number; answer: string | null; history: AskTraceTurn[] }
	interface TraceDetailState { loading: boolean; error: string | null; data: AskTraceDetail | null }

	interface MechanicKeyword {
		id: number; term: string; status: string; occurrences: number;
		firstSeen: string; reviewedAt: string | null;
	}

	// Bewijs bij een kandidaat (#123): kaart + snippet in drie delen zodat de
	// term gemarkeerd kan worden zonder {@html}.
	interface KeywordCard { id: string; name: string; before: string; match: string; after: string; }
	interface KeywordCards { term: string; total: number; items: KeywordCard[]; }
	interface KeywordEvidence { loading: boolean; error: string | null; data: KeywordCards | null; }

	interface UpcomingSet {
		setId: string; name: string; publishedOn: string; cardCount: number | null;
	}

	// Bron-dossier (#171): herkomst + opbrengst per soort + verwerkingsstatus,
	// spiegelbeeld van het kaart-dossier (#127).
	interface SourceDossierOrigin { feedId: string | null; feedName: string | null; }
	interface SourceYieldChange {
		id: number; changeType: string; severity: string; summary: string | null; detectedAt: string;
	}
	interface SourceYieldBan {
		id: number; name: string; cardRiftboundId: string | null; kind: string; format: string;
		effectiveFrom: string | null; detectedAt: string;
	}
	interface SourceYieldErratum {
		id: number; cardName: string; cardRiftboundId: string | null; detectedAt: string;
	}
	interface SourceYieldRuling {
		id: number; scope: string; ref: string; question: string | null; text: string;
		status: string; at: string;
	}
	interface SourceYieldClaim {
		id: number; topicType: string; topicRef: string; statement: string; status: string; lastSeen: string;
	}
	interface SourceDossierYield {
		documents: number; lastDocumentAt: string | null; ruleChunks: number;
		changesTotal: number; changes: SourceYieldChange[];
		bansTotal: number; bans: SourceYieldBan[];
		errataTotal: number; errata: SourceYieldErratum[];
		rulingsTotal: number; rulings: SourceYieldRuling[];
		claimsTotal: number; claims: SourceYieldClaim[];
	}
	interface SourceDossierScan { status: string; detail: string | null; at: string; }
	interface SourceDossierStep { kind: string; status: string; detail: string | null; at: string; }
	interface SourceDossierProcessing {
		lastScan: SourceDossierScan | null; followUps: SourceDossierStep[];
		completenessStatus: string; completenessNote: string;
	}
	interface SourceDossier {
		sourceId: string; sourceName: string; trustTier: number;
		origin: SourceDossierOrigin; yield: SourceDossierYield; processing: SourceDossierProcessing;
	}
	interface SourceDossierState { loading: boolean; error: string | null; data: SourceDossier | null; }

	// Compleetheidssignaal (#171, Domain-functie SourceDossierCompleteness) →
	// badge-kleur + NL-label; status = kleur + tekst, geen emoji's.
	function completenessBadge(status: string): string {
		if (status === 'volledig') return 'ok-b';
		if (status === 'leeg') return 'warn-b';
		return 'err';
	}
	function completenessLabel(status: string): string {
		switch (status) {
			case 'volledig': return 'Volledig verwerkt';
			case 'leeg': return 'Niets opgeleverd';
			case 'onvolledig': return 'Onvolledig';
			default: return 'Nog nooit gescand';
		}
	}

	const sources = $derived(data.sources as Source[]);
	// Herkomst-lookup (#167): id → naam, voor de "stamt van: …"-koppeling in
	// de bronnentabel; het volledige feed-beheer zit op de eigen pagina.
	const feedNames = $derived(
		new Map(((data.feeds ?? []) as FeedRef[]).map((f) => [f.id, f.name]))
	);
	const corrections = $derived((data.corrections ?? []) as Correction[]);
	const openCorrections = $derived(corrections.filter((c) => c.status === 'unverified'));
	const askTraces = $derived((data.askTraces ?? []) as AskTrace[]);
	const mechanics = $derived((data.mechanics ?? []) as MechanicKeyword[]);
	const mechanicCandidates = $derived(mechanics.filter((m) => m.status === 'candidate'));
	const acceptedMechanics = $derived(mechanics.filter((m) => m.status === 'accepted'));

	// Lazy bewijs per kandidaat (#123): pas fetchen bij de eerste uitklap —
	// nooit alle kandidaten × kaarten vooraf laden. Na een fout kan dicht/open
	// het opnieuw proberen.
	let keywordCards = $state<Record<number, KeywordEvidence>>({});
	async function loadKeywordCards(id: number) {
		const cur = keywordCards[id];
		if (cur?.loading || cur?.data) return;
		keywordCards[id] = { loading: true, error: null, data: null };
		try {
			const r = await fetch(`/admin/mechanics/${id}/cards`);
			if (!r.ok) throw new Error(`status ${r.status}`);
			keywordCards[id] = { loading: false, error: null, data: await r.json() };
		} catch {
			keywordCards[id] = {
				loading: false, data: null,
				error: 'Kaarten laden mislukt — klap opnieuw uit om het nog eens te proberen'
			};
		}
	}
	// Bron-dossier (#171): lazy per bron, pas bij uitklappen — force=true na
	// een re-trigger, zodat de zojuist opnieuw gedraaide scan direct zichtbaar
	// wordt zonder de hele pagina te herladen.
	let sourceDossiers = $state<Record<string, SourceDossierState>>({});
	async function loadSourceDossier(id: string, force = false) {
		const cur = sourceDossiers[id];
		if (!force && (cur?.loading || cur?.data)) return;
		sourceDossiers[id] = { loading: true, error: null, data: null };
		try {
			const r = await fetch(`/admin/sources/${encodeURIComponent(id)}`);
			if (!r.ok) throw new Error(`status ${r.status}`);
			sourceDossiers[id] = { loading: false, error: null, data: await r.json() };
		} catch {
			sourceDossiers[id] = {
				loading: false, data: null,
				error: 'Dossier laden mislukt — klap opnieuw uit om het nog eens te proberen'
			};
		}
	}

	const upcoming = $derived((data.upcoming ?? []) as UpcomingSet[]);

	// Lazy gesprek per trace (#143): pas fetchen bij de eerste uitklap —
	// zelfde patroon als het keyword-bewijs hierboven; na een fout kan
	// dicht/open het opnieuw proberen.
	let traceDetails = $state<Record<number, TraceDetailState>>({});
	async function loadTraceDetail(id: number) {
		const cur = traceDetails[id];
		if (cur?.loading || cur?.data) return;
		traceDetails[id] = { loading: true, error: null, data: null };
		try {
			const r = await fetch(`/admin/asktraces/${id}`);
			if (!r.ok) throw new Error(`status ${r.status}`);
			traceDetails[id] = { loading: false, error: null, data: await r.json() };
		} catch {
			traceDetails[id] = {
				loading: false, data: null,
				error: 'Gesprek laden mislukt — klap opnieuw uit om het nog eens te proberen'
			};
		}
	}

	interface KnowledgeDoc {
		id: number; kind: string; topic: string; title: string; body: string;
		sectionRefs: string | null; status: string; updatedAt: string;
	}
	const knowledge = $derived((data.knowledge ?? []) as KnowledgeDoc[]);
	const draftDocs = $derived(knowledge.filter((k) => k.status === 'draft'));
	const approvedDocs = $derived(knowledge.filter((k) => k.status === 'approved'));
	// svelte-ignore state_referenced_locally
	let live = $state<Status | null>(data.status as Status | null);
	const running = $derived(live?.running ?? null);

	// Live polling zolang de admin open staat; sneller pollen als er iets draait.
	$effect(() => {
		if (!data.authed) return;
		let stop = false;
		const tick = async () => {
			try {
				const r = await fetch('/admin/status');
				if (r.ok) live = await r.json();
			} catch { /* rb-api even weg — volgende poll */ }
			if (!stop) setTimeout(tick, live?.running ? 2000 : 6000);
		};
		tick();
		return () => { stop = true; };
	});

	function fmtAgo(iso: string | null): string {
		if (!iso) return '';
		const s = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
		// Dagen erbij (#122): een wekelijkse scout-run is anders '168u geleden'.
		return s < 60 ? `${s}s`
			: s < 3600 ? `${Math.round(s / 60)}m`
			: s < 48 * 3600 ? `${Math.round(s / 3600)}u`
			: `${Math.round(s / 86400)}d`;
	}
	function jobLabel(name: string): string {
		return JOBS.find((j) => j.name === name)?.label ?? name;
	}
	// Laatste run per job (#122), handmatig én automatisch via de scheduler.
	function lastRunLabel(name: string): string | null {
		const r = live?.jobRuns?.find((x) => x.name === name);
		if (!r) return null;
		return `laatste run ${fmtAgo(r.at)} geleden${r.status === 'error' ? ' — mislukt' : ''}`;
	}

	// Elke tegel klikt door naar het onderliggende overzicht (#61).
	const TILES: { key: string; label: string; slug: string }[] = [
		{ key: 'cards', label: 'Kaarten', slug: 'kaarten' },
		{ key: 'cardsEmbedded', label: 'Geëmbed', slug: 'embeddings' },
		{ key: 'cardsMined', label: 'Geanalyseerd', slug: 'analyse' },
		{ key: 'ruleChunks', label: 'Regelsecties', slug: 'regelsecties' },
		{ key: 'bans', label: 'Bans', slug: 'bans' },
		{ key: 'errata', label: 'Errata', slug: 'errata' },
		{ key: 'interactions', label: 'Interacties', slug: 'interacties' },
		{ key: 'changes', label: 'Wijzigingen', slug: 'wijzigingen' },
		{ key: 'openCorrections', label: 'Open correcties', slug: 'correcties' },
		{ key: 'knowledge', label: 'Spelbegrip-docs', slug: 'primer' },
		{ key: 'claims', label: 'Claims', slug: 'claims' },
		{ key: 'relations', label: 'Relaties', slug: 'relaties' },
		{ key: 'openProposals', label: 'Bronvoorstellen', slug: 'voorstellen' },
		// Bron-feeds (#167): index-pagina's die automatisch nieuwe bronnen ontdekken.
		{ key: 'feeds', label: 'Bron-feeds', slug: 'feeds' },
		// Piltover Archive-decks (#15).
		{ key: 'decks', label: 'Decks', slug: 'decks' },
		// Accounts + kosteninzicht (#42).
		{ key: 'users', label: 'Gebruikers', slug: 'gebruikers' }
	];
</script>

<svelte:head><title>Beheer — RB Rules</title></svelte:head>

<main>
	{#if !data.authed}
		<h1>Beheer</h1>
		<form method="POST" action="?/login" use:enhance class="panel narrow">
			<label>Wachtwoord <input type="password" name="password" autocomplete="current-password" /></label>
			{#if form?.error}<p class="warn">{form.error}</p>{/if}
			<button type="submit">Inloggen</button>
		</form>
	{:else}
		<header class="head">
			<h1>Beheer</h1>
			<form method="POST" action="?/logout" use:enhance><button class="ghost">Uitloggen</button></form>
		</header>

		{#if data.apiDown}<p class="warn">rb-api is niet bereikbaar.</p>{/if}

		<!-- Live status -->
		{#if running}
			<div class="banner running">
				<span class="spin"></span>
				<div class="banner-body">
					<strong>{jobLabel(running.name)}</strong>
					<span class="meta">draait {fmtAgo(running.startedAt)}</span>
					{#if running.progress}
						<p class="progress">{running.progress}</p>
					{/if}
				</div>
			</div>
		{:else if live?.lastJob}
			{@const last = live.lastJob}
			<div class="banner {last.status === 'ok' ? 'done' : 'failed'}">
				<span class="status-dot {last.status === 'ok' ? 'ok' : 'err'}"></span>
				<div class="banner-body">
					<strong>{jobLabel(last.name)}</strong>
					<span class="meta">{last.status === 'ok' ? 'afgerond' : 'mislukt'} · {fmtAgo(last.finishedAt)} geleden</span>
					{#if last.detail}<p class="progress">{last.detail}</p>{/if}
				</div>
			</div>
		{/if}
		{#if form?.error}<p class="warn">{form.error}</p>{/if}

		<!-- Aankomende set (#52): op de releasedag draait de keten automatisch
		     zodra de classifier de release herkent; handmatig kan altijd. -->
		{#each upcoming as s (s.setId)}
			<div class="banner upcoming-set">
				<span class="status-dot warn"></span>
				<div class="banner-body">
					<strong>Aankomende set: {s.name}{s.name !== s.setId ? ` (${s.setId})` : ''}</strong>
					<span class="meta">release {new Date(s.publishedOn).toLocaleDateString('nl-NL', { day: 'numeric', month: 'long', year: 'numeric' })}{s.cardCount ? ` · ${s.cardCount} kaarten` : ''}</span>
					<p class="progress">Bij de release triggert de set-release-keten automatisch; met de actie "Set-release-keten" kan hij ook handmatig draaien.</p>
				</div>
			</div>
		{/each}

		<!-- Statistieken -->
		{#if live?.counts}
			<div class="tiles">
				{#each TILES as t (t.key)}
					<a class="tile panel" href="/admin/overview/{t.slug}">
						<span class="num">{live.counts[t.key] ?? 0}</span>
						<span class="lbl">{t.label}</span>
					</a>
				{/each}
				<!-- Rapport, geen telling: waar is de kennisbank dun (#52). -->
				<a class="tile panel" href="/admin/overview/gaten">
					<span class="num">→</span>
					<span class="lbl">Kennis-gaten</span>
				</a>
				<!-- Rapport (#145): per set aanwezige en ontbrekende nummers. -->
				<a class="tile panel" href="/admin/overview/setdekking">
					<span class="num">→</span>
					<span class="lbl">Set-dekking</span>
				</a>
				<!-- Rapport (#158): score + antwoorden van de judge-benchmark. -->
				<a class="tile panel" href="/admin/overview/benchmark">
					<span class="num">→</span>
					<span class="lbl">Benchmark</span>
				</a>
			</div>
		{/if}

		<!-- Acties -->
		<h2>Acties</h2>
		<form method="POST" action="?/job" use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }} class="job panel job-all">
			<input type="hidden" name="name" value="all" />
			<div class="job-info">
				<strong>Alles bijwerken</strong>
				<span class="hint">kaarten → scan → regels → bans → embeddings → mechanieken → graph → interacties; een haperende stap stopt de rest niet</span>
				{#if lastRunLabel('all')}<span class="hint">{lastRunLabel('all')}</span>{/if}
			</div>
			<button disabled={running !== null}>
				{running?.name === 'all' ? 'Bezig' : 'Start alles'}
			</button>
		</form>
		<div class="jobs">
			{#each JOBS.filter((j) => j.name !== 'all') as j (j.name)}
				<form method="POST" action="?/job" use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }} class="job panel">
					<input type="hidden" name="name" value={j.name} />
					<div class="job-info">
						<strong>{j.label}</strong>
						<span class="hint">{j.hint}</span>
						{#if lastRunLabel(j.name)}<span class="hint">{lastRunLabel(j.name)}</span>{/if}
					</div>
					<button disabled={running !== null}>
						{running?.name === j.name ? 'Bezig' : 'Start'}
					</button>
				</form>
			{/each}
		</div>

		<!-- Reviewqueue (self-learning) -->
		{#if openCorrections.length}
			<h2>Reviewqueue <span class="meta">({openCorrections.length} open — geverifieerde correcties sturen toekomstige antwoorden)</span></h2>
			{#each openCorrections as c (c.id)}
				<div class="correction panel">
					<div class="correction-body">
						{#if c.question}<p class="q">{c.question}</p>{/if}
						<p class="t">{c.text}</p>
						<p class="meta">
							{c.ref === 'down' ? 'Gemeld als onjuist' : c.ref === 'up' ? 'Bevestigd als juist' : c.ref}
							· {new Date(c.createdAt).toLocaleString('nl-NL')}
						</p>
					</div>
					<div class="correction-actions">
						<form method="POST" action="?/verifyCorrection" use:enhance>
							<input type="hidden" name="id" value={c.id} />
							<button title="Maakt dit een gezaghebbende ruling voor toekomstige antwoorden">Verifieer</button>
						</form>
						<form method="POST" action="?/deleteCorrection" use:enhance>
							<input type="hidden" name="id" value={c.id} />
							<button class="ghost small">Verwijder</button>
						</form>
					</div>
				</div>
			{/each}
		{/if}

		<!-- Mechaniek-kandidaten (#52): het vocabulaire groeit met elke set -->
		{#if mechanicCandidates.length}
			<h2>Mechaniek-kandidaten <span class="meta">({mechanicCandidates.length} open — bracketed termen uit kaartteksten die nog niet in het vocabulaire staan)</span></h2>
			<!-- Eén regel duiding bij de acties (#123): wat accepteren/verwerpen doet. -->
			<p class="meta">Accepteren neemt de term op in het mining-vocabulaire en zet de betrokken kaarten opnieuw in de mine-wachtrij; verwerpen laat de term blijvend genegeerd.</p>
			{#each mechanicCandidates as m (m.id)}
				{@const evidence = keywordCards[m.id]}
				<div class="correction panel">
					<div class="correction-body">
						<p class="t"><strong>{m.term}</strong> <span class="badge warn-b">kandidaat</span></p>
						<p class="meta">komt voor op {m.occurrences} {m.occurrences === 1 ? 'kaart' : 'kaarten'} · gezien {new Date(m.firstSeen).toLocaleDateString('nl-NL')}</p>
						<!-- Bewijs (#123): lazy uitklap met de kaarten die de term dragen. -->
						<details class="evidence" ontoggle={(e) => { if (e.currentTarget.open) loadKeywordCards(m.id); }}>
							<summary>Bekijk de {m.occurrences === 1 ? 'kaart' : 'kaarten'} met [{m.term}]</summary>
							{#if evidence?.loading}
								<p class="meta">Kaarten laden…</p>
							{:else if evidence?.error}
								<p class="warn">{evidence.error}</p>
							{:else if evidence?.data}
								{#if evidence.data.items.length === 0}
									<p class="meta">Geen kaarten gevonden — mogelijk zijn de kaartteksten sinds de laatste mining-run gewijzigd.</p>
								{:else}
									<ul class="evidence-list">
										{#each evidence.data.items as c (c.id)}
											<li>
												<a href="/cards/{c.id}">{c.name}</a>
												<span class="snippet">{c.before}{#if c.match}<mark>{c.match}</mark>{/if}{c.after}</span>
											</li>
										{/each}
									</ul>
									{#if evidence.data.total > evidence.data.items.length}
										<p class="meta">en {evidence.data.total - evidence.data.items.length} meer</p>
									{/if}
								{/if}
							{/if}
						</details>
					</div>
					<div class="correction-actions">
						<form method="POST" action="?/acceptMechanic" use:enhance>
							<input type="hidden" name="id" value={m.id} />
							<button title="Wordt vocabulaire; de kaarten met dit keyword worden opnieuw gemined (volgende mining-run)">Accepteer</button>
						</form>
						<form method="POST" action="?/rejectMechanic" use:enhance>
							<input type="hidden" name="id" value={m.id} />
							<button class="ghost small" title="Komt niet opnieuw de queue in">Verwerp</button>
						</form>
					</div>
				</div>
			{/each}
			{#if acceptedMechanics.length}
				<p class="meta">Vocabulaire naast de seed-lijst: {acceptedMechanics.map((m) => m.term).join(', ')}</p>
			{/if}
		{/if}

		<!-- Bronnen -->
		<h2>Bronnen <span class="meta">(<a class="meta-link" href="/admin/overview/feeds">bron-feeds beheren</a> — index-pagina's die hier automatisch nieuwe bronnen in zetten)</span></h2>
		<div class="table-wrap">
		<table>
			<thead><tr><th>Bron</th><th>Trust</th><th>Cadans</th><th>Herkomst</th><th>Laatst gecontroleerd</th><th>Actief</th></tr></thead>
			<tbody>
				{#each sources as s (s.id)}
					{@const dossier = sourceDossiers[s.id]}
					<tr id="bron-{s.id}">
						<td><strong>{s.name}</strong><br /><a class="meta" href={s.url}>{s.id}</a></td>
						<td>{s.trustTier}</td>
						<td>{s.cadence}</td>
						<td class="meta">
							{#if s.feedId}
								stamt van: <a class="meta-link" href="/admin/overview/feeds#feed-{s.feedId}">{feedNames.get(s.feedId) ?? s.feedId}</a>
							{:else}
								handmatig
							{/if}
						</td>
						<td class="meta">{s.lastChecked ? new Date(s.lastChecked).toLocaleString('nl-NL') : '—'}</td>
						<td>
							<form method="POST" action="?/toggle" use:enhance>
								<input type="hidden" name="id" value={s.id} />
								<input type="hidden" name="enabled" value={String(!s.enabled)} />
								<button class="ghost small">{s.enabled ? 'Aan' : 'Uit'}</button>
							</form>
						</td>
					</tr>
					<tr class="dossier-row">
						<td colspan="6" class="dossier-cell">
							<!-- Bron-dossier (#171): wat heeft déze bron opgeleverd, en is dat
							     compleet verwerkt? Lazy, zelfde uitklap-patroon als de
							     mechaniek-bewijs hierboven. -->
							<details class="evidence" ontoggle={(e) => { if (e.currentTarget.open) loadSourceDossier(s.id); }}>
								<summary>Dossier — wat heeft deze bron opgeleverd, en is dat compleet verwerkt?</summary>
								{#if dossier?.loading}
									<p class="meta">Dossier laden…</p>
								{:else if dossier?.error}
									<p class="warn">{dossier.error}</p>
								{:else if dossier?.data}
									{@const d = dossier.data}
									<div class="dossier-body">
										<p>
											<span class="badge {completenessBadge(d.processing.completenessStatus)}">{completenessLabel(d.processing.completenessStatus)}</span>
											<span class="meta">{d.processing.completenessNote}</span>
										</p>
										{#if d.processing.lastScan}
											<p class="meta">
												Laatste scan: {d.processing.lastScan.status} · {new Date(d.processing.lastScan.at).toLocaleString('nl-NL')}
												{#if d.processing.lastScan.detail}— {d.processing.lastScan.detail}{/if}
											</p>
										{/if}
										{#if d.processing.followUps.length}
											<p class="meta">Vervolgstappen (classify/claims-/clarify-mining):</p>
											<ul class="dossier-list">
												{#each d.processing.followUps as f, i (i)}
													<li>
														<span class="badge {f.status === 'error' ? 'err' : 'ok-b'}">{f.kind}</span>
														{f.status}{#if f.detail} — {f.detail}{/if}
														<span class="meta">· {new Date(f.at).toLocaleString('nl-NL')}</span>
													</li>
												{/each}
											</ul>
										{/if}
										<form
											method="POST" action="?/rescanSource"
											use:enhance={() => async ({ update }) => { await update(); await loadSourceDossier(s.id, true); }}
										>
											<input type="hidden" name="id" value={s.id} />
											<button class="ghost small" title="Draait scan (en classify/claims-mining als vervolg) opnieuw voor deze ene bron">Opnieuw scannen</button>
										</form>

										<p class="meta">
											{d.yield.documents} document(en){#if d.yield.lastDocumentAt}, laatste {new Date(d.yield.lastDocumentAt).toLocaleDateString('nl-NL')}{/if}
											· <a class="meta-link" href="/admin/overview/regelsecties?source={s.id}">{d.yield.ruleChunks} regelsecties bekijken</a>
										</p>

										<div class="dossier-grid">
											{#if d.yield.changesTotal}
												<div>
													<p class="meta"><strong>Wijzigingen ({d.yield.changesTotal})</strong></p>
													<ul class="dossier-list">
														{#each d.yield.changes as c (c.id)}
															<li>
																{c.changeType} · {c.severity}{#if c.summary} — {c.summary}{/if}
																<span class="meta">· {new Date(c.detectedAt).toLocaleDateString('nl-NL')}</span>
															</li>
														{/each}
													</ul>
												</div>
											{/if}
											{#if d.yield.bansTotal}
												<div>
													<p class="meta"><strong>Bans ({d.yield.bansTotal})</strong></p>
													<ul class="dossier-list">
														{#each d.yield.bans as b (b.id)}
															<li>
																{#if b.cardRiftboundId}<a href="/cards/{b.cardRiftboundId}">{b.name}</a>{:else}{b.name}{/if}
																· {b.format}
															</li>
														{/each}
													</ul>
												</div>
											{/if}
											{#if d.yield.errataTotal}
												<div>
													<p class="meta"><strong>Errata ({d.yield.errataTotal})</strong></p>
													<ul class="dossier-list">
														{#each d.yield.errata as e (e.id)}
															<li>{#if e.cardRiftboundId}<a href="/cards/{e.cardRiftboundId}">{e.cardName}</a>{:else}{e.cardName}{/if}</li>
														{/each}
													</ul>
												</div>
											{/if}
											{#if d.yield.rulingsTotal}
												<div>
													<p class="meta"><strong>Rulings ({d.yield.rulingsTotal})</strong></p>
													<ul class="dossier-list">
														{#each d.yield.rulings as r (r.id)}
															<li>{r.scope}: {r.ref} <span class="meta">— {r.status}</span></li>
														{/each}
													</ul>
												</div>
											{/if}
											{#if d.yield.claimsTotal}
												<div>
													<p class="meta"><strong>Claims ({d.yield.claimsTotal})</strong></p>
													<ul class="dossier-list">
														{#each d.yield.claims as c (c.id)}
															<li>{c.topicRef} <span class="meta">— {c.status}</span></li>
														{/each}
													</ul>
												</div>
											{/if}
										</div>
									</div>
								{/if}
							</details>
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
		</div>

		<!-- Primer-kennisdocs (#49): spelbegrip reviewen -->
		{#if knowledge.length}
			<h2>Spelbegrip-primer <span class="meta">({approvedDocs.length} goedgekeurd, {draftDocs.length} te reviewen — goedgekeurde docs voeden elke ruling) · <a class="meta-link" href="/admin/overview/primer">alle docs bekijken en bewerken</a></span></h2>
			{#each draftDocs as k (k.id)}
				<div class="correction panel">
					<div class="correction-body">
						<p class="t"><strong>{k.title}</strong> <span class="badge warn-b">Draft</span></p>
						<details>
							<summary class="meta">Lees de gegenereerde tekst (gebaseerd op {k.sectionRefs || 'regels'})</summary>
							<p class="primer-body">{k.body}</p>
						</details>
					</div>
					<div class="correction-actions">
						<form method="POST" action="?/approveKnowledge" use:enhance>
							<input type="hidden" name="id" value={k.id} />
							<button title="Goedgekeurde docs gaan mee als spelbegrip in elke vraag">Keur goed</button>
						</form>
						<form method="POST" action="?/deleteKnowledge" use:enhance>
							<input type="hidden" name="id" value={k.id} />
							<button class="ghost small">Verwijder</button>
						</form>
					</div>
				</div>
			{/each}
		{/if}

		<!-- Vraag-traces (#40): denkstappen van de ask-pipeline; uitklappen
		     laadt het volledige gesprek (#143) lazy uit het detail-endpoint -->
		{#if askTraces.length}
			<h2>Vraag-traces <span class="meta">(laatste {askTraces.length} — de route die elke vraag door de pipeline nam, met het volledige gesprek)</span></h2>
			{#each askTraces as t (t.id)}
				{@const detail = traceDetails[t.id]}
				<details class="trace panel" ontoggle={(e) => { if (e.currentTarget.open) loadTraceDetail(t.id); }}>
					<summary>
						<span class="badge {t.ok ? 'ok-b' : 'err'}">{t.questionType ?? '?'}</span>
						<!-- #153: wie escaleerde — de gate of de gebruiker zelf -->
						{#if t.agentic}<span class="badge warn-b">agentic ({t.escalatedBy === 'user' ? 'gebruiker' : 'gate'})</span>{/if}
						<span class="trace-q">{t.question}</span>
						<span class="meta">{(t.durationMs / 1000).toFixed(1)}s{t.hadImage ? ' · foto' : ''} · {new Date(t.createdAt).toLocaleTimeString('nl-NL')}</span>
					</summary>
					<!-- Chatweergave (#143): eerdere beurten → vraag → brein-
					     stappen op hun plek in de flow → definitieve antwoord,
					     gerenderd zoals /ask hem toont (AnswerView saneert). -->
					<div class="chat">
						{#if detail?.loading}
							<p class="meta">Gesprek laden…</p>
						{:else if detail?.error}
							<p class="warn">{detail.error}</p>
						{/if}
						{#if detail?.data}
							{#each detail.data.history as turn, i (i)}
								<div class="chat-q">
									<span class="chat-label">Eerdere vraag</span>
									<p class="chat-question">{turn.question}</p>
								</div>
								<div class="chat-a">
									<span class="chat-label">Eerder antwoord</span>
									<AnswerView answer={turn.answer} />
								</div>
							{/each}
						{/if}
						<div class="chat-q">
							<span class="chat-label">Vraag</span>
							<p class="chat-question">{t.question}</p>
						</div>
						<!-- Agentic ask (#107): de brein-stappen van de agent —
						     tussen vraag en antwoord, waar ze in de flow zaten -->
						{#if t.agentic}
							<div class="chat-a">
								<span class="chat-label">Brein-stappen</span>
								<p class="brain-steps">{t.brainSteps || '—'}</p>
							</div>
						{/if}
						{#if detail?.data}
							<div class="chat-a">
								<span class="chat-label">Antwoord</span>
								{#if detail.data.answer}
									<AnswerView answer={detail.data.answer} />
								{:else}
									<p class="meta">Voor deze trace is geen antwoord opgeslagen — hij is van vóór het gesprek-in-de-trace.</p>
								{/if}
							</div>
						{/if}
					</div>
					<dl>
						<dt>Router</dt>
						<dd>type {t.questionType} · bron-bias {t.sourceBias ?? 'geen'} · kaartnaam herkend: {t.mentionsCard ? 'ja' : 'nee'}{t.mechanicMatches ? ` · mechanieken: ${t.mechanicMatches}` : ''}</dd>
						<dt>Regelsecties</dt>
						<dd>{t.sections || '—'}</dd>
						<dt>Kaartcontext</dt>
						<dd>{t.contextCards || '—'}</dd>
						<!-- Kennislagen (#51): welke lagen deden mee in de prompt -->
						<dt>Kennislagen</dt>
						<dd>primer: {t.primerDocs || '—'} · community: {t.communityClaims || '—'}</dd>
						<!-- Fase-timings (#152): waar zat de tijd van deze vraag -->
						<dt>Fasen</dt>
						<dd>{phaseLine(t.phaseTimings)}</dd>
						<dt>Overig</dt>
						<dd>{t.verifiedRulings} geverifieerde rulings · model {t.model} · {t.ok ? 'geslaagd' : 'AI niet beschikbaar'}</dd>
					</dl>
				</details>
			{/each}
		{/if}

		<!-- Live logs -->
		<h2>Recente activiteit <span class="meta live-tag">live</span></h2>
		<div class="table-wrap">
		<table>
			<thead><tr><th>Tijd</th><th>Soort</th><th>Ref</th><th>Status</th><th>Detail</th></tr></thead>
			<tbody>
				{#each live?.logs ?? [] as l (l.id)}
					<tr>
						<td class="meta">{new Date(l.createdAt).toLocaleTimeString('nl-NL')}</td>
						<td>{l.kind}</td>
						<td class="meta">{l.ref ?? '—'}</td>
						<td><span class="badge {l.status === 'error' ? 'err' : l.status === 'changed' || l.status === 'new' ? 'warn-b' : 'ok-b'}">{l.status}</span></td>
						<td class="meta">{l.detail ?? ''}</td>
					</tr>
				{/each}
			</tbody>
		</table>
		</div>
	{/if}
</main>

<style>
	main { max-width: 1080px; margin: 0 auto; padding: 24px 20px; }
	.head { display: flex; justify-content: space-between; align-items: center; }
	.narrow { max-width: 360px; padding: 18px; }
	label { display: block; color: var(--muted); margin-bottom: 10px; }
	input {
		width: 100%; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;
	}
	button {
		background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 8px 14px; font-weight: 600; cursor: pointer;
	}
	button:disabled { opacity: 0.45; cursor: default; }
	button.ghost { background: transparent; color: var(--muted); border: 1px solid var(--border); }
	button.small { padding: 4px 10px; font-size: 0.82rem; }
	h2 { color: var(--accent); font-size: 1.02rem; margin: 28px 0 10px; }
	.banner {
		display: flex; align-items: flex-start; gap: 12px;
		border-radius: var(--radius); padding: 12px 16px; margin: 14px 0;
	}
	.banner.running { background: var(--accent-soft); border: 1px solid var(--accent); }
	.banner.upcoming-set { background: var(--accent-soft); border: 1px solid var(--accent); }
	.banner.done { background: var(--ok-soft); border: 1px solid var(--ok); }
	.banner.failed { background: var(--err-soft); border: 1px solid var(--err); }
	.banner .spin { margin-top: 3px; }
	.banner .status-dot { margin-top: 7px; }
	.banner-body { flex: 1; }
	.banner-body .meta { margin-left: 8px; }
	.progress {
		margin: 6px 0 0; color: var(--muted); font-size: 0.88rem;
		font-variant-numeric: tabular-nums;
	}
	.tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(118px, 1fr)); gap: 10px; }
	.tile { padding: 10px 12px; display: flex; flex-direction: column; text-decoration: none; }
	.tile:hover { border-color: var(--accent); }
	.tile .num { font-size: 1.35rem; font-weight: 700; font-variant-numeric: tabular-nums; }
	.tile .lbl { color: var(--muted); font-size: 0.76rem; }
	.jobs { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 10px; }
	.job-all { border-color: var(--accent); margin-bottom: 10px; }
	.job { display: flex; align-items: center; gap: 12px; padding: 12px 14px; }
	.job-info { flex: 1; display: flex; flex-direction: column; }
	.job-info .hint { color: var(--muted); font-size: 0.78rem; }
	.primer-body { white-space: pre-wrap; margin: 8px 0 2px; line-height: 1.6; }
	.trace { padding: 10px 14px; margin-bottom: 6px; }
	.trace summary { cursor: pointer; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
	.trace-q { flex: 1; min-width: 180px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
	/* Chatweergave in de trace-uitklap (#143): vraag als eigen blok, het
	   antwoord met een gesprekslijn ervoor; alles mag breken op 390px. */
	.chat { display: flex; flex-direction: column; gap: 10px; margin: 12px 0 4px; }
	.chat-label {
		display: block; color: var(--muted); font-size: 0.75rem;
		text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 4px;
	}
	.chat-q {
		background: var(--surface-deep); border: 1px solid var(--border);
		border-radius: 10px; padding: 10px 14px;
	}
	.chat-question { margin: 0; font-weight: 600; overflow-wrap: anywhere; }
	.chat-a {
		border-left: 2px solid var(--border); padding-left: 12px;
		min-width: 0; overflow-wrap: anywhere;
	}
	.trace dl { margin: 10px 0 2px; }
	.trace dt { color: var(--muted); font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.05em; margin-top: 8px; }
	.trace dd { margin: 2px 0 0; font-size: 0.9rem; }
	/* Brein-stappen (#107): één tool-call per regel, monospace — leesbaar
	   zonder de JSON-argumenten te laten overlopen. */
	.brain-steps {
		white-space: pre-line; overflow-wrap: anywhere;
		font-family: ui-monospace, monospace; font-size: 0.82rem;
	}
	.correction { display: flex; gap: 14px; padding: 12px 14px; margin-bottom: 8px; }
	.correction-body { flex: 1; min-width: 0; /* snippets mogen de flex-rij op 390px niet oprekken */ }
	/* Bewijs-uitklap bij kandidaten (#123). */
	.evidence { margin-top: 6px; }
	.evidence summary { cursor: pointer; color: var(--accent); font-size: 0.85rem; }
	.evidence-list { margin: 8px 0 0; padding-left: 18px; }
	.evidence-list li { margin-bottom: 6px; line-height: 1.5; }
	.evidence-list a { color: var(--accent); text-decoration: none; }
	.evidence-list a:hover { text-decoration: underline; }
	.evidence-list .snippet {
		display: block; color: var(--muted); font-size: 0.85rem;
		overflow-wrap: anywhere; /* lange kaartteksten, geen overflow op 390px */
	}
	.evidence-list mark {
		background: var(--warn-soft); color: var(--warn);
		border-radius: 4px; padding: 0 3px;
	}
	.correction .q { margin: 0 0 4px; color: var(--muted); font-size: 0.88rem; }
	.correction .t { margin: 0 0 4px; }
	.correction-actions { display: flex; flex-direction: column; gap: 6px; }
	/* Bron-dossier (#171): losse rij ónder elke bron — geen eigen rand, de
	   bovenliggende rij scheidt de bronnen al. overflow-wrap overal: lange
	   changelog-samenvattingen/rulings mogen nooit horizontaal overlopen op
	   390px. */
	.dossier-row td { border-bottom: 1px solid var(--border); padding-top: 0; }
	.dossier-cell { padding-top: 0 !important; }
	.dossier-body { overflow-wrap: anywhere; }
	.dossier-body form { margin: 8px 0; }
	.dossier-grid {
		display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
		gap: 14px; margin-top: 8px;
	}
	.dossier-list { margin: 4px 0 0; padding-left: 18px; }
	.dossier-list li { margin-bottom: 4px; line-height: 1.5; overflow-wrap: anywhere; }
	table { width: 100%; border-collapse: collapse; margin-bottom: 20px; }
	th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--border); }
	th { color: var(--muted); font-size: 0.82rem; font-weight: 600; }
	.meta { color: var(--muted); font-size: 0.85rem; }
	.meta-link { color: var(--accent); text-decoration: none; }
	.meta-link:hover { text-decoration: underline; }
	.live-tag {
		font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.08em;
		border: 1px solid var(--border); border-radius: 999px; padding: 2px 8px; margin-left: 6px;
	}
	/* .badge-stijlen: gedeelde bouwsteen in app.css (#59). */
	.warn { color: var(--err); }
	a { color: inherit; }
</style>
