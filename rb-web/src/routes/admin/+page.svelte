<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import AnswerView from '$lib/AnswerView.svelte';
	import ChangeCard from '$lib/ChangeCard.svelte';
	import RbText from '$lib/RbText.svelte';
	import { trustLabel } from '$lib/changeCard';

	let { data, form } = $props();

	interface Source {
		id: string; name: string; url: string; trustTier: number;
		cadence: string; enabled: boolean; lastChecked: string | null;
		// Herkomst (#167): welke feed dit artikel ontdekte; null = handmatig/legacy.
		feedId: string | null;
		// Negeren met reden (#180): los van `enabled` ("tijdelijk uit") — een
		// bewuste, blijvende beoordeling dat de bron niets oplevert.
		// isIgnoreCandidate: berekend signaal (SourceIgnoreCandidacy) — na
		// meerdere scans nog steeds 0 changes/claims/rulings.
		ignoredAt: string | null; ignoreReason: string | null;
		isIgnoreCandidate: boolean;
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
	// Graph-drift (#214 overzicht): aantallen per knooptype — Postgres is de
	// bron, Neo4j de projectie. Zelfde vorm als het gaten-rapport (#52).
	interface DriftEntry { label: string; postgres: number; graph: number; delta: number; }
	interface Drift { graphAvailable: boolean; detail: string | null; entries: DriftEntry[]; }

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
		{ name: 'feeds', label: 'Bron-feeds afspeuren', hint: 'nieuwe artikelen op de Riot-nieuwspagina\'s ontdekken; vertrouwde feeds zetten direct een bron, andere een voorstel — draait ook als eerste stap van "Bronnen scannen"' },
		// Wipe-mechanisme voor de LLM-afgeleide kennislaag (#187): in JOBS voor
		// het label/laatste-run-opzoekwerk hierboven, maar expliciet uit de
		// gewone jobs-grid gefilterd (zie hieronder) — eigen, gewaarschuwd
		// paneel met een confirm()-stap, geen kale "Start"-knop.
		{
			name: 'regenerateknowledge', label: 'Kennis regenereren (Engels)',
			hint: 'verwijdert ALLE claims, primer-docs, correcties en relaties (de afgeleide/gemínede laag) en reset de mining-markers; genereert daarna NIETS automatisch — draai zelf primer → claims → clarify → relations, in die volgorde'
		}
	];

	// Paden (#190): geordende jobs die vanzelf doorstromen. De stappenlijst
	// zelf komt van /api/admin/paths (pathDefs hieronder) — hier alleen het
	// label/de uitleg per pad, in dezelfde vorm als JOBS hierboven.
	interface PathStepInfo { jobName: string; drain: boolean; maxRepeats: number; }
	interface PathInfo { name: string; steps: PathStepInfo[]; }
	const pathDefs = $derived((data.paths ?? []) as PathInfo[]);

	const PATHS: { name: string; label: string; hint: string }[] = [
		{
			name: 'ingest', label: 'Ingest-pad',
			hint: 'nieuwe/gewijzigde bronnen volledig verwerken: bronnen scannen → classificaties aanvullen → mechanieken/claims/FAQ-concepten minen → embeddings → graph'
		},
		{
			name: 'card', label: 'Kaart-pad',
			hint: 'nieuwe/gewijzigde kaarten door de pijplijn: kaarten synchroniseren → embeddings → graph'
		},
		{
			name: 'knowledge', label: 'Kennis-pad',
			hint: 'de LLM-afgeleide kennislaag bijwerken zonder de bron-scan opnieuw te draaien: claims → FAQ-concepten → relaties → graph'
		},
		{
			name: 'full', label: 'Volledige regeneratie',
			hint: 'primer → het hele kennis-pad (claims → FAQ-concepten → relaties → graph); bevat NIET de wipe — die blijft de aparte Gevarenzone-actie hieronder'
		}
	];

	interface Correction {
		id: number; scope: string; ref: string; text: string;
		question: string | null; status: string; createdAt: string;
		// #177: reden dat een clarify-item ter review staat (citaat niet in bron / onderwerp niet herkend).
		statusReason: string | null;
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
	// Negeren (#180): client-side gefilterd, zelfde patroon als de andere
	// chip-gefilterde lijsten hieronder (openCorrections, mechanicCandidates)
	// — de admin-endpoint levert bewust alles in één keer.
	const activeSources = $derived(sources.filter((s) => !s.ignoredAt));
	const ignoredSources = $derived(sources.filter((s) => s.ignoredAt));
	let showIgnored = $state(false);
	// Reden-invoer per bron vóór het negeren (#180) — pas gelezen bij submit.
	let ignoreReasonDraft = $state<Record<string, string>>({});
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

	// Graph-drift voor het Overzicht (#214): losse fetch in de load, hier alleen
	// weergeven. Null = graph/AI even weg → de tabel degradeert naar een notitie.
	const drift = $derived((data.drift ?? null) as Drift | null);
	const driftInSync = $derived(
		!!drift?.graphAvailable && drift.entries.every((e) => e.delta === 0)
	);

	// Recente runs (#122): laatste afronding per job, nieuwste eerst.
	const recentRuns = $derived(
		[...(live?.jobRuns ?? [])]
			.sort((a, b) => new Date(b.at).getTime() - new Date(a.at).getTime())
			.slice(0, 6)
	);

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
	function fmtTime(iso: string): string {
		return new Date(iso).toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' });
	}
	function jobLabel(name: string): string {
		// Review-fix #190: ook paden draaien als JobRunner-run onder hun eigen
		// naam — de running/laatste-run-banner toont anders de rauwe padnaam
		// ("ingest") in plaats van het label ("Ingest-pad").
		return (
			JOBS.find((j) => j.name === name)?.label ??
			PATHS.find((p) => p.name === name)?.label ??
			name
		);
	}
	// Laatste run per job (#122), handmatig én automatisch via de scheduler.
	function lastRunLabel(name: string): string | null {
		const r = live?.jobRuns?.find((x) => x.name === name);
		if (!r) return null;
		return `laatste run ${fmtAgo(r.at)} geleden${r.status === 'error' ? ' — mislukt' : ''}`;
	}

	// Elke tegel klikt door naar het onderliggende overzicht (#61). De
	// accentstreep (domeinkleur) is puur decoratief — geen semantiek.
	const TILE_COLORS = [
		'var(--dom-mind)', 'var(--dom-chaos)', 'var(--dom-body)',
		'var(--dom-calm)', 'var(--dom-fury)', 'var(--dom-order)'
	];
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
	// Rapporten (geen telling, wél doorklikbaar): eigen rij onder de tellingen.
	const REPORTS: { label: string; slug: string }[] = [
		{ label: 'Kennis-gaten', slug: 'gaten' },
		{ label: 'Set-dekking', slug: 'setdekking' },
		{ label: 'Benchmark', slug: 'benchmark' }
	];
</script>

<svelte:head><title>Beheer — Poracle</title></svelte:head>

{#if !data.authed}
	<div class="login-wrap">
		<div class="login panel">
			<h1>Beheer</h1>
			<p class="sub">Log in om de kennisbank te beheren.</p>
			<form method="POST" action="?/login" use:enhance>
				<label>Wachtwoord
					<input type="password" name="password" autocomplete="current-password" />
				</label>
				{#if form?.error}<p class="warn">{form.error}</p>{/if}
				<button type="submit" class="cta">Inloggen</button>
			</form>
		</div>
	</div>
{:else}
	<div class="mhead">
		<h1>Overzicht</h1>
		<span class="sp"></span>
		<form
			method="POST" action="?/job"
			use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }}
		>
			<input type="hidden" name="name" value="all" />
			<button
				class="ghost"
				disabled={running !== null}
				title="Alle stappen in de juiste volgorde; een haperende stap stopt de rest niet"
			>{running?.name === 'all' ? 'Bezig…' : 'Alles bijwerken'}</button>
		</form>
		<form method="POST" action="?/logout" use:enhance>
			<button class="ghost">Uitloggen</button>
		</form>
	</div>

	<main class="mbody">
		{#if data.apiDown}<p class="notice err-notice">rb-api is niet bereikbaar.</p>{/if}
		{#if form?.error}<p class="notice err-notice">{form.error}</p>{/if}

		<!-- Nu bezig / laatste job -->
		{#if running}
			<div class="panel live-panel">
				<div class="ph">
					<h4>Nu bezig</h4>
					<span class="sp"></span>
					<span class="live"><span class="p"></span>{jobLabel(running.name)}</span>
				</div>
				<div class="bar indet" role="progressbar" aria-label="Voortgang"><i></i></div>
				<div class="barmeta">
					<span>{running.progress ?? 'bezig…'}</span>
					<span class="tnum">draait {fmtAgo(running.startedAt)}</span>
				</div>
			</div>
		{:else if live?.lastJob}
			{@const last = live.lastJob}
			<div class="panel status-line">
				<span class="status-dot {last.status === 'ok' ? 'ok' : 'err'}"></span>
				<strong>{jobLabel(last.name)}</strong>
				<span class="meta">{last.status === 'ok' ? 'afgerond' : 'mislukt'} · {fmtAgo(last.finishedAt)} geleden</span>
				{#if last.detail}<span class="meta detail">{last.detail}</span>{/if}
			</div>
		{/if}

		<!-- Aankomende set (#52): op de releasedag draait de keten automatisch. -->
		{#each upcoming as s (s.setId)}
			<div class="panel upcoming">
				<span class="status-dot warn"></span>
				<div>
					<strong>Aankomende set: {s.name}{s.name !== s.setId ? ` (${s.setId})` : ''}</strong>
					<p class="meta">release {new Date(s.publishedOn).toLocaleDateString('nl-NL', { day: 'numeric', month: 'long', year: 'numeric' })}{s.cardCount ? ` · ${s.cardCount} kaarten` : ''} — de set-release-keten triggert automatisch; met de actie "Set-release-keten" kan hij ook handmatig draaien.</p>
				</div>
			</div>
		{/each}

		<!-- Paden (#190): geordende jobs die vanzelf doorstromen. -->
		{#if pathDefs.length}
			<section class="panel" id="jobs">
				<div class="ph"><h4>Paden</h4><span class="sp"></span><span class="meta">of losse jobs onderaan</span></div>
				<div class="paths">
					{#each PATHS as p (p.name)}
						{@const def = pathDefs.find((x) => x.name === p.name)}
						{@const hot = running?.name === p.name}
						<form
							method="POST" action="?/path"
							use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }}
						>
							<input type="hidden" name="name" value={p.name} />
							<button class="pathbtn" class:hot disabled={running !== null} title={p.hint}>
								<span class="pn">{p.label}</span>
								<span class="ps">
									{#if hot && running?.progress}{running.progress}
									{:else if def}{def.steps.map((s) => jobLabel(s.jobName)).join(' → ')}
									{:else}{p.hint}{/if}
								</span>
								{#if lastRunLabel(p.name)}<span class="ps meta">{lastRunLabel(p.name)}</span>{/if}
							</button>
						</form>
					{/each}
				</div>
			</section>
		{/if}

		<!-- Tellingen — elke tegel klikt door naar het onderliggende overzicht. -->
		{#if live?.counts}
			<div class="tiles">
				{#each TILES as t, i (t.key)}
					<a class="tile" href="/admin/overview/{t.slug}">
						<span class="tb" style="background: {TILE_COLORS[i % TILE_COLORS.length]}"></span>
						<span class="tn tnum">{(live.counts[t.key] ?? 0).toLocaleString('nl-NL')}</span>
						<span class="tl">{t.label}</span>
					</a>
				{/each}
			</div>
			<div class="reports">
				{#each REPORTS as r (r.slug)}
					<a class="report-link" href="/admin/overview/{r.slug}">{r.label} <span aria-hidden="true">→</span></a>
				{/each}
			</div>
		{/if}

		<!-- Graph-drift + recente runs -->
		<div class="two">
			<section class="panel">
				<div class="ph">
					<h4>Graph-drift</h4>
					<span class="sp"></span>
					{#if drift?.graphAvailable}
						<span class="tag {driftInSync ? 'ok' : 'warn'}">{driftInSync ? 'alles in sync' : 'drift'}</span>
					{/if}
				</div>
				{#if !drift}
					<p class="meta">Drift niet beschikbaar — het gaten-rapport kon nu niet worden opgehaald.</p>
				{:else if !drift.graphAvailable}
					<p class="meta">Graph niet beschikbaar{drift.detail ? ` — ${drift.detail}` : ''}; drift is nu niet te meten.</p>
				{:else}
					<div class="table-wrap">
						<table class="drift">
							<thead><tr><th>Label</th><th>postgres</th><th>graph</th><th>Δ</th></tr></thead>
							<tbody>
								{#each drift.entries as e (e.label)}
									<tr>
										<td>{e.label}</td>
										<td class="tnum">{e.postgres.toLocaleString('nl-NL')}</td>
										<td class="tnum">{e.graph.toLocaleString('nl-NL')}</td>
										<td class="tnum {e.delta === 0 ? 'd0' : 'dn'}">{e.delta === 0 ? '0' : e.delta > 0 ? `+${e.delta}` : e.delta}</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
				{/if}
			</section>

			<section class="panel">
				<div class="ph"><h4>Recente runs</h4></div>
				{#if recentRuns.length}
					<div class="runlog">
						{#each recentRuns as r (r.name + r.at)}
							<div class="r">
								<span class="status-dot {r.status === 'error' ? 'err' : 'ok'}"></span>
								<span class="rk">{jobLabel(r.name)}</span>
								{#if r.status === 'error'}<span class="meta">mislukt</span>{/if}
								<span class="rt tnum">{fmtAgo(r.at)}</span>
							</div>
						{/each}
					</div>
				{:else}
					<p class="meta">Nog geen runs geregistreerd.</p>
				{/if}
			</section>
		</div>

		<!-- Losse jobs -->
		<section class="section">
			<h2>Acties</h2>
			<div class="jobs">
				{#each JOBS.filter((j) => j.name !== 'all' && j.name !== 'regenerateknowledge') as j (j.name)}
					<form
						method="POST" action="?/job"
						use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }}
						class="job panel"
					>
						<input type="hidden" name="name" value={j.name} />
						<div class="job-info">
							<strong>{j.label}</strong>
							<span class="hint">{j.hint}</span>
							{#if lastRunLabel(j.name)}<span class="hint">{lastRunLabel(j.name)}</span>{/if}
						</div>
						<button class="cta" disabled={running !== null}>
							{running?.name === j.name ? 'Bezig' : 'Start'}
						</button>
					</form>
				{/each}
			</div>
		</section>

		<!-- Reviewqueue (self-learning correcties) -->
		{#if openCorrections.length}
			<section class="section" id="reviewqueue">
				<h2>Reviewqueue <span class="meta">({openCorrections.length} open — geverifieerde correcties sturen toekomstige antwoorden)</span></h2>
				{#each openCorrections as c (c.id)}
					<div class="review-row panel">
						<div class="review-body">
							{#if c.question}<p class="q">{c.question}</p>{/if}
							<p class="t"><RbText text={c.text} /></p>
							{#if c.statusReason}
								<p class="meta"><span class="badge warn-b">ter review</span> {c.statusReason}</p>
							{/if}
							<p class="meta">
								{c.ref === 'down' ? 'Gemeld als onjuist' : c.ref === 'up' ? 'Bevestigd als juist' : c.ref}
								· {new Date(c.createdAt).toLocaleString('nl-NL')}
							</p>
						</div>
						<div class="review-actions">
							<form method="POST" action="?/verifyCorrection" use:enhance>
								<input type="hidden" name="id" value={c.id} />
								<button class="cta small" title="Maakt dit een gezaghebbende ruling voor toekomstige antwoorden">Verifieer</button>
							</form>
							<form method="POST" action="?/rejectCorrection" use:enhance>
								<input type="hidden" name="id" value={c.id} />
								<button class="ghost small" title="Wijst dit af; een volgende mining-run maakt het niet opnieuw aan">Verwerp</button>
							</form>
							<form method="POST" action="?/deleteCorrection" use:enhance>
								<input type="hidden" name="id" value={c.id} />
								<button class="ghost small">Verwijder</button>
							</form>
						</div>
					</div>
				{/each}
			</section>
		{/if}

		<!-- Mechaniek-kandidaten (#52): het vocabulaire groeit met elke set -->
		{#if mechanicCandidates.length}
			<section class="section">
				<h2>Mechaniek-kandidaten <span class="meta">({mechanicCandidates.length} open — bracketed termen uit kaartteksten die nog niet in het vocabulaire staan)</span></h2>
				<p class="meta">Accepteren neemt de term op in het mining-vocabulaire en zet de betrokken kaarten opnieuw in de mine-wachtrij; verwerpen laat de term blijvend genegeerd.</p>
				{#each mechanicCandidates as m (m.id)}
					{@const evidence = keywordCards[m.id]}
					<div class="review-row panel">
						<div class="review-body">
							<p class="t"><strong>{m.term}</strong> <span class="badge warn-b">kandidaat</span></p>
							<p class="meta">komt voor op {m.occurrences} {m.occurrences === 1 ? 'kaart' : 'kaarten'} · gezien {new Date(m.firstSeen).toLocaleDateString('nl-NL')}</p>
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
						<div class="review-actions">
							<form method="POST" action="?/acceptMechanic" use:enhance>
								<input type="hidden" name="id" value={m.id} />
								<button class="cta small" title="Wordt vocabulaire; de kaarten met dit keyword worden opnieuw gemined (volgende mining-run)">Accepteer</button>
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
			</section>
		{/if}

		<!-- Bronnen (#180) -->
		<section class="section" id="bronnen">
			<h2>
				Bronnen <span class="meta">(<a class="meta-link" href="/admin/overview/feeds">bron-feeds beheren</a> — index-pagina's die hier automatisch nieuwe bronnen in zetten)</span>
			</h2>
			<div class="bronnen-head">
				<span class="meta tnum">{activeSources.length} actief</span>
				<button type="button" class="ghost small" onclick={() => (showIgnored = !showIgnored)}>
					Genegeerd ({ignoredSources.length})
				</button>
			</div>

			{#each activeSources as s (s.id)}
				{@const dossier = sourceDossiers[s.id]}
				{@const trust = trustLabel(s.trustTier)}
				<div class="srow panel" id="bron-{s.id}">
					<div class="srow-head">
						<div class="srow-id">
							<p class="sname">{s.name}</p>
							<a class="surl" href={s.url}>{s.id}</a>
						</div>
						{#if trust}
							<span class="trust trust-{trust.tone}">{trust.label}</span>
						{/if}
						<span class="kk">{s.cadence}</span>
						{#if s.feedId}
							<a class="kk kk-link" href="/admin/overview/feeds#feed-{s.feedId}">via {feedNames.get(s.feedId) ?? s.feedId}</a>
						{:else}
							<span class="kk">handmatig</span>
						{/if}
						<span class="sp"></span>
						<span class="meta lastchecked tnum">{s.lastChecked ? new Date(s.lastChecked).toLocaleDateString('nl-NL') : 'nooit gescand'}</span>
					</div>

					{#if s.isIgnoreCandidate}
						<p class="cand"><span class="status-dot warn"></span> levert niets op na meerdere scans — negeren?</p>
					{/if}

					<div class="srow-actions">
						<form method="POST" action="?/toggle" use:enhance>
							<input type="hidden" name="id" value={s.id} />
							<input type="hidden" name="enabled" value={String(!s.enabled)} />
							<button class="ghost small" title={s.enabled ? 'Bron staat aan — klik om tijdelijk uit te zetten' : 'Bron staat uit — klik om aan te zetten'}>
								<span class="status-dot {s.enabled ? 'ok' : 'err'}"></span> {s.enabled ? 'Actief' : 'Uit'}
							</button>
						</form>
						<form method="POST" action="?/ignoreSource" class="ignore-form" use:enhance>
							<input type="hidden" name="id" value={s.id} />
							<input
								type="text" name="reason" placeholder="reden (optioneel)"
								value={ignoreReasonDraft[s.id] ?? ''}
								oninput={(e) => (ignoreReasonDraft[s.id] = e.currentTarget.value)}
							/>
							<button class="ghost small" title="Bron blijft geregistreerd (geen delete) — de scan-lus slaat 'm voortaan over">Negeer met reden</button>
						</form>
					</div>

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
											{#each d.yield.changes as c (c.id)}
												<ChangeCard change={c} compact />
											{/each}
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
				</div>
			{/each}

			{#if showIgnored}
				<div class="ignored-block">
					<h3>Genegeerde bronnen</h3>
					{#each ignoredSources as s (s.id)}
						<div class="srow panel ign">
							<div class="srow-head">
								<div class="srow-id">
									<p class="sname">{s.name}</p>
									<a class="surl" href={s.url}>{s.id}</a>
								</div>
								<span class="ignflag">genegeerd{s.ignoreReason ? ` · ${s.ignoreReason}` : ''}</span>
								<span class="sp"></span>
								<span class="meta tnum">{s.ignoredAt ? new Date(s.ignoredAt).toLocaleDateString('nl-NL') : ''}</span>
								<form method="POST" action="?/unignoreSource" use:enhance>
									<input type="hidden" name="id" value={s.id} />
									<button class="ghost small">Terugzetten</button>
								</form>
							</div>
						</div>
					{:else}
						<p class="meta">Geen genegeerde bronnen.</p>
					{/each}
				</div>
			{/if}
		</section>

		<!-- Primer-kennisdocs (#49): spelbegrip reviewen -->
		{#if knowledge.length}
			<section class="section">
				<h2>Spelbegrip-primer <span class="meta">({approvedDocs.length} goedgekeurd, {draftDocs.length} te reviewen — goedgekeurde docs voeden elke ruling) · <a class="meta-link" href="/admin/overview/primer">alle docs bekijken en bewerken</a></span></h2>
				{#each draftDocs as k (k.id)}
					<div class="review-row panel">
						<div class="review-body">
							<p class="t"><strong>{k.title}</strong> <span class="badge warn-b">Draft</span></p>
							<details>
								<summary class="meta">Lees de gegenereerde tekst (gebaseerd op {k.sectionRefs || 'regels'})</summary>
								<p class="primer-body"><RbText text={k.body} /></p>
							</details>
						</div>
						<div class="review-actions">
							<form method="POST" action="?/approveKnowledge" use:enhance>
								<input type="hidden" name="id" value={k.id} />
								<button class="cta small" title="Goedgekeurde docs gaan mee als spelbegrip in elke vraag">Keur goed</button>
							</form>
							<form method="POST" action="?/deleteKnowledge" use:enhance>
								<input type="hidden" name="id" value={k.id} />
								<button class="ghost small">Verwijder</button>
							</form>
						</div>
					</div>
				{/each}
			</section>
		{/if}

		<!-- Vraag-traces (#40) -->
		{#if askTraces.length}
			<section class="section" id="traces">
				<h2>Vraag-traces <span class="meta">(laatste {askTraces.length} — de route die elke vraag door de pipeline nam, met het volledige gesprek)</span></h2>
				{#each askTraces as t (t.id)}
					{@const detail = traceDetails[t.id]}
					<details class="trace panel" ontoggle={(e) => { if (e.currentTarget.open) loadTraceDetail(t.id); }}>
						<summary>
							<span class="badge {t.ok ? 'ok-b' : 'err'}">{t.questionType ?? '?'}</span>
							{#if t.agentic}<span class="badge warn-b">agentic ({t.escalatedBy === 'user' ? 'gebruiker' : 'gate'})</span>{/if}
							<span class="trace-q">{t.question}</span>
							<span class="meta tnum">{(t.durationMs / 1000).toFixed(1)}s{t.hadImage ? ' · foto' : ''} · {new Date(t.createdAt).toLocaleTimeString('nl-NL')}</span>
						</summary>
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
							<dt>Kennislagen</dt>
							<dd>primer: {t.primerDocs || '—'} · community: {t.communityClaims || '—'}</dd>
							<dt>Fasen</dt>
							<dd>{phaseLine(t.phaseTimings)}</dd>
							<dt>Overig</dt>
							<dd>{t.verifiedRulings} geverifieerde rulings · model {t.model} · {t.ok ? 'geslaagd' : 'AI niet beschikbaar'}</dd>
						</dl>
					</details>
				{/each}
			</section>
		{/if}

		<!-- Gevarenzone (#187): eigen, zwaar gewaarschuwd paneel. -->
		<section class="section" id="gevarenzone">
			<h2 class="danger-h">Gevarenzone</h2>
			<form
				method="POST" action="?/job"
				use:enhance={({ cancel }) => {
					if (!confirm('Dit verwijdert ALLE claims, primer-docs, correcties en relaties (de afgeleide/gemínede kennislaag) definitief en genereert niets automatisch opnieuw. Alleen doen ná een deploy die de mining-prompts naar het Engels heeft omgezet, en niet terwijl er nog een job draait. Doorgaan?')) {
						cancel();
						return;
					}
					return async ({ update }) => { await update(); await invalidateAll(); };
				}}
				class="job panel danger"
			>
				<input type="hidden" name="name" value="regenerateknowledge" />
				<div class="job-info">
					<strong>Kennis regenereren (Engels)</strong>
					<span class="hint">
						Onomkeerbaar: verwijdert alle claims, primer-docs, correcties en relaties, en reset de
						mining-markers zodat brondocumenten opnieuw gemined worden. Genereert daarna NIETS
						automatisch — start daarna zelf het pad "Volledige regeneratie" hierboven (primer →
						claims → FAQ-concepten → relaties → graph, elke mining-stap gedraineerd tot de cap niet
						meer geraakt wordt).
					</span>
					{#if lastRunLabel('regenerateknowledge')}<span class="hint">{lastRunLabel('regenerateknowledge')}</span>{/if}
				</div>
				<button disabled={running !== null} class="danger-button">
					{running?.name === 'regenerateknowledge' ? 'Bezig' : 'Verwijder en her-mine'}
				</button>
			</form>
		</section>

		<!-- Live logs -->
		<section class="section">
			<h2>Recente activiteit <span class="live-tag">live</span></h2>
			<div class="table-wrap">
				<table>
					<thead><tr><th>Tijd</th><th>Soort</th><th>Ref</th><th>Status</th><th>Detail</th></tr></thead>
					<tbody>
						{#each live?.logs ?? [] as l (l.id)}
							<tr>
								<td class="meta tnum">{fmtTime(l.createdAt)}</td>
								<td>{l.kind}</td>
								<td class="meta">{l.ref ?? '—'}</td>
								<td><span class="badge {l.status === 'error' ? 'err' : l.status === 'changed' || l.status === 'new' ? 'warn-b' : 'ok-b'}">{l.status}</span></td>
								<td class="meta">{l.detail ?? ''}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</section>
	</main>
{/if}

<style>
	/* ── Login ─────────────────────────────────────────────────────── */
	.login-wrap {
		display: flex;
		justify-content: center;
		align-items: flex-start;
		padding: clamp(40px, 12vh, 120px) 20px;
	}
	.login {
		width: 100%;
		max-width: 360px;
		padding: 22px 22px 24px;
	}
	.login h1 {
		margin: 0 0 4px;
	}
	.login .sub {
		color: var(--muted);
		margin: 0 0 16px;
		font-size: 0.9rem;
	}
	.login form {
		display: flex;
		flex-direction: column;
		gap: 12px;
	}
	.login label {
		display: block;
		color: var(--muted);
		font-size: 0.85rem;
	}
	.login input {
		width: 100%;
		margin-top: 6px;
		background: var(--surface-deep);
		color: var(--text);
		border: 1px solid var(--border);
		border-radius: var(--radius);
		padding: 9px 11px;
		font-size: 16px;
	}
	.login .cta {
		width: 100%;
	}

	/* ── Header ────────────────────────────────────────────────────── */
	.mhead {
		display: flex;
		align-items: center;
		gap: 10px;
		padding: 14px clamp(16px, 4vw, 28px);
		border-bottom: 1px solid var(--border);
		flex-wrap: wrap;
	}
	.mhead h1 {
		margin: 0;
		font-size: 1.25rem;
		font-weight: 700;
		letter-spacing: -0.01em;
	}
	.mhead .sp {
		flex: 1;
	}

	.mbody {
		display: block;
		max-width: 1120px;
		margin: 0 auto;
		padding: clamp(16px, 4vw, 28px);
	}
	.mbody > * + * {
		margin-top: 16px;
	}

	/* ── Knoppen ───────────────────────────────────────────────────── */
	.cta {
		background: var(--accent);
		color: var(--accent-ink);
		border: 0;
		border-radius: var(--radius);
		padding: 8px 14px;
		font-weight: 650;
		font-size: 0.85rem;
		cursor: pointer;
	}
	.ghost {
		background: transparent;
		color: var(--muted);
		border: 1px solid var(--border);
		border-radius: var(--radius);
		padding: 8px 12px;
		font-weight: 600;
		font-size: 0.82rem;
		cursor: pointer;
	}
	.ghost:hover {
		color: var(--text);
	}
	.cta:disabled,
	.ghost:disabled {
		opacity: 0.5;
		cursor: default;
	}
	.small {
		padding: 5px 10px;
		font-size: 0.8rem;
	}

	.notice {
		margin: 0;
		padding: 10px 14px;
		border-radius: var(--radius);
		font-size: 0.88rem;
	}
	.err-notice {
		background: var(--err-soft);
		border: 1px solid var(--err);
		color: var(--err);
	}
	.warn {
		color: var(--err);
	}

	/* ── Panelen / paneelkop ───────────────────────────────────────── */
	.panel {
		padding: 15px 17px;
	}
	.ph {
		display: flex;
		align-items: center;
		gap: 10px;
		margin-bottom: 12px;
	}
	.ph h4 {
		margin: 0;
		font-size: 0.86rem;
		font-weight: 650;
	}
	.ph .sp {
		flex: 1;
	}
	.tag {
		font-size: 0.68rem;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.04em;
		border-radius: 999px;
		padding: 2px 9px;
	}
	.tag.ok {
		color: var(--ok);
		background: var(--ok-soft);
	}
	.tag.warn {
		color: var(--warn);
		background: var(--warn-soft);
	}

	/* ── Nu bezig ──────────────────────────────────────────────────── */
	.live {
		display: inline-flex;
		align-items: center;
		gap: 7px;
		font-size: 0.72rem;
		font-family: ui-monospace, 'SF Mono', Menlo, Consolas, monospace;
		letter-spacing: 0.06em;
		text-transform: uppercase;
		color: var(--ok);
	}
	.live .p {
		width: 7px;
		height: 7px;
		border-radius: 50%;
		background: var(--ok);
	}
	.bar {
		height: 8px;
		border-radius: 8px;
		background: var(--surface-deep);
		overflow: hidden;
		margin: 2px 0 8px;
	}
	.bar > i {
		display: block;
		height: 100%;
		border-radius: 8px;
		background: linear-gradient(90deg, var(--dom-calm), var(--dom-mind));
	}
	.bar.indet > i {
		width: 42%;
	}
	.barmeta {
		display: flex;
		justify-content: space-between;
		gap: 12px;
		font-size: 0.8rem;
		color: var(--muted);
	}
	@media (prefers-reduced-motion: no-preference) {
		.live .p {
			animation: pulse 1.4s ease-in-out infinite;
		}
		.bar.indet > i {
			animation: indet 1.4s ease-in-out infinite;
		}
		@keyframes indet {
			0% { transform: translateX(-110%); }
			100% { transform: translateX(320%); }
		}
	}
	/* Zonder animatie: een gevulde balk als "bezig"-signaal, geen beweging. */
	@media (prefers-reduced-motion: reduce) {
		.bar.indet > i {
			width: 100%;
		}
	}

	.status-line {
		display: flex;
		align-items: center;
		gap: 9px;
		flex-wrap: wrap;
		padding: 11px 16px;
	}
	.status-line .detail {
		flex-basis: 100%;
	}
	.upcoming {
		display: flex;
		align-items: flex-start;
		gap: 10px;
		padding: 12px 16px;
		border-color: color-mix(in srgb, var(--accent) 45%, var(--border));
		background: var(--accent-soft);
	}
	.upcoming .meta {
		margin: 3px 0 0;
	}

	/* ── Paden ─────────────────────────────────────────────────────── */
	.paths {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
		gap: 10px;
	}
	.paths form {
		display: flex;
	}
	.pathbtn {
		flex: 1;
		display: flex;
		flex-direction: column;
		align-items: flex-start;
		gap: 3px;
		text-align: left;
		border: 1px solid var(--border);
		background: var(--surface);
		border-radius: var(--radius);
		padding: 11px 13px;
		cursor: pointer;
		color: var(--text);
	}
	.pathbtn:hover {
		border-color: var(--border-strong);
	}
	.pathbtn:disabled {
		cursor: default;
		opacity: 0.65;
	}
	.pathbtn.hot {
		border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
		background: color-mix(in srgb, var(--accent) 8%, var(--surface));
		opacity: 1;
	}
	.pathbtn .pn {
		font-size: 0.84rem;
		font-weight: 650;
	}
	.pathbtn .ps {
		font-size: 0.72rem;
		color: var(--muted);
		line-height: 1.4;
	}

	/* ── Tellingen ─────────────────────────────────────────────────── */
	.tiles {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(128px, 1fr));
		gap: 10px;
	}
	.tile {
		display: flex;
		flex-direction: column;
		gap: 3px;
		background: var(--surface);
		border: 1px solid var(--border);
		border-radius: var(--radius-lg);
		box-shadow: var(--shadow-card);
		padding: 12px 13px;
		text-decoration: none;
		color: var(--text);
	}
	.tile:hover {
		border-color: var(--border-strong);
	}
	.tile .tb {
		width: 24px;
		height: 3px;
		border-radius: 3px;
		margin-bottom: 7px;
	}
	.tile .tn {
		font-size: 1.35rem;
		font-weight: 750;
		letter-spacing: -0.02em;
	}
	.tile .tl {
		font-size: 0.72rem;
		color: var(--muted);
	}
	.reports {
		display: flex;
		flex-wrap: wrap;
		gap: 8px 18px;
	}
	.report-link {
		font-size: 0.82rem;
		color: var(--accent);
		text-decoration: none;
		font-weight: 600;
	}
	.report-link:hover {
		text-decoration: underline;
	}

	/* ── Twee kolommen: drift + runs ───────────────────────────────── */
	.two {
		display: grid;
		grid-template-columns: 1fr 1fr;
		gap: 14px;
	}
	@media (max-width: 720px) {
		.two {
			grid-template-columns: 1fr;
		}
	}
	table.drift {
		width: 100%;
		border-collapse: collapse;
		font-size: 0.8rem;
	}
	table.drift th,
	table.drift td {
		padding: 6px 4px;
		text-align: right;
		border-bottom: 1px solid var(--border);
	}
	table.drift th {
		color: var(--muted);
		font-weight: 500;
		font-size: 0.66rem;
		letter-spacing: 0.05em;
		text-transform: uppercase;
	}
	table.drift th:first-child,
	table.drift td:first-child {
		text-align: left;
	}
	.d0 {
		color: var(--ok);
		font-weight: 600;
	}
	.dn {
		color: var(--warn);
		font-weight: 600;
	}
	.runlog .r {
		display: flex;
		align-items: center;
		gap: 9px;
		padding: 7px 0;
		border-top: 1px solid var(--border);
		font-size: 0.82rem;
	}
	.runlog .r:first-child {
		border-top: 0;
	}
	.runlog .rk {
		font-weight: 500;
	}
	.runlog .rt {
		margin-left: auto;
		color: var(--muted);
	}

	/* ── Secties ───────────────────────────────────────────────────── */
	.section h2 {
		font-size: 1rem;
		font-weight: 650;
		margin: 6px 0 12px;
	}
	.section h2 .meta {
		font-weight: 400;
	}
	.danger-h {
		color: var(--err);
	}
	.section h3 {
		font-size: 0.9rem;
		margin: 16px 0 10px;
	}

	.jobs {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(290px, 1fr));
		gap: 10px;
	}
	.job {
		display: flex;
		align-items: center;
		gap: 12px;
		padding: 12px 14px;
	}
	.job-info {
		flex: 1;
		display: flex;
		flex-direction: column;
		min-width: 0;
	}
	.job-info .hint {
		color: var(--muted);
		font-size: 0.76rem;
	}
	.job.danger {
		border-color: var(--err);
		background: var(--err-soft);
	}
	.danger-button {
		background: var(--err);
		color: #fff;
		border: 0;
		border-radius: var(--radius);
		padding: 8px 14px;
		font-weight: 650;
		font-size: 0.85rem;
		cursor: pointer;
		white-space: nowrap;
	}
	.danger-button:disabled {
		opacity: 0.5;
		cursor: default;
	}

	/* ── Reviewrijen (correcties, mechanieken, primer) ─────────────── */
	.review-row {
		display: flex;
		gap: 14px;
		padding: 13px 15px;
		flex-wrap: wrap;
	}
	.review-body {
		flex: 1 1 320px;
		min-width: 0;
	}
	.review-actions {
		display: flex;
		flex-direction: column;
		gap: 6px;
	}
	.review-body .q {
		margin: 0 0 4px;
		color: var(--muted);
		font-size: 0.88rem;
	}
	.review-body .t {
		margin: 0 0 4px;
	}
	.primer-body {
		white-space: pre-wrap;
		margin: 8px 0 2px;
		line-height: 1.6;
	}

	.evidence {
		margin-top: 8px;
	}
	.evidence summary {
		cursor: pointer;
		color: var(--accent);
		font-size: 0.85rem;
	}
	.evidence-list {
		margin: 8px 0 0;
		padding-left: 18px;
	}
	.evidence-list li {
		margin-bottom: 6px;
		line-height: 1.5;
	}
	.evidence-list a {
		color: var(--accent);
		text-decoration: none;
	}
	.evidence-list a:hover {
		text-decoration: underline;
	}
	.evidence-list .snippet {
		display: block;
		color: var(--muted);
		font-size: 0.85rem;
		overflow-wrap: anywhere;
	}
	.evidence-list mark {
		background: var(--warn-soft);
		color: var(--warn);
		border-radius: 4px;
		padding: 0 3px;
	}

	/* ── Bronnen ───────────────────────────────────────────────────── */
	.bronnen-head {
		display: flex;
		align-items: center;
		gap: 12px;
		margin-bottom: 10px;
	}
	.bronnen-head .meta {
		margin-right: auto;
	}
	.srow {
		padding: 13px 15px;
		margin-bottom: 9px;
	}
	.srow.ign {
		opacity: 0.7;
	}
	.srow-head {
		display: flex;
		align-items: center;
		gap: 8px 10px;
		flex-wrap: wrap;
	}
	.srow-id {
		min-width: 0;
		margin-right: 4px;
	}
	.sname {
		margin: 0;
		font-size: 0.9rem;
		font-weight: 600;
	}
	.surl {
		display: block;
		font-size: 0.74rem;
		color: var(--muted);
		font-family: ui-monospace, Menlo, Consolas, monospace;
		text-decoration: none;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
		max-width: 320px;
	}
	.surl:hover {
		color: var(--text);
	}
	.srow-head .sp {
		flex: 1;
	}
	.trust {
		font-size: 0.66rem;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.04em;
		border-radius: 5px;
		padding: 2px 8px;
	}
	.trust-official {
		color: var(--ok);
		background: var(--ok-soft);
	}
	.trust-community {
		color: var(--warn);
		background: var(--warn-soft);
	}
	.kk {
		font-size: 0.7rem;
		color: var(--muted);
		background: var(--surface-deep);
		border-radius: 5px;
		padding: 2px 8px;
		font-family: ui-monospace, Menlo, Consolas, monospace;
		text-decoration: none;
	}
	.kk-link:hover {
		color: var(--text);
	}
	.lastchecked {
		white-space: nowrap;
	}
	.cand {
		display: inline-flex;
		align-items: center;
		gap: 7px;
		margin: 8px 0 0;
		font-size: 0.8rem;
		color: var(--warn);
	}
	.srow-actions {
		display: flex;
		flex-wrap: wrap;
		gap: 8px;
		align-items: center;
		margin-top: 10px;
	}
	.srow-actions .ghost .status-dot {
		margin-right: 2px;
	}
	.ignore-form {
		display: flex;
		flex-wrap: wrap;
		gap: 6px;
		align-items: center;
	}
	.ignore-form input[type='text'] {
		width: 150px;
		background: var(--surface-deep);
		color: var(--text);
		border: 1px solid var(--border);
		border-radius: var(--radius);
		padding: 6px 9px;
	}
	.ignflag {
		font-size: 0.68rem;
		font-weight: 600;
		color: var(--muted);
		background: var(--surface-deep);
		border-radius: 5px;
		padding: 2px 8px;
	}

	.dossier-body {
		overflow-wrap: anywhere;
		margin-top: 8px;
	}
	.dossier-body form {
		margin: 8px 0;
	}
	.dossier-grid {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
		gap: 14px;
		margin-top: 8px;
	}
	.dossier-list {
		margin: 4px 0 0;
		padding-left: 18px;
	}
	.dossier-list li {
		margin-bottom: 4px;
		line-height: 1.5;
		overflow-wrap: anywhere;
	}

	/* ── Traces ────────────────────────────────────────────────────── */
	.trace {
		padding: 11px 14px;
		margin-bottom: 8px;
	}
	.trace summary {
		cursor: pointer;
		display: flex;
		gap: 10px;
		align-items: center;
		flex-wrap: wrap;
	}
	.trace-q {
		flex: 1;
		min-width: 180px;
		overflow: hidden;
		text-overflow: ellipsis;
		white-space: nowrap;
	}
	.chat {
		display: flex;
		flex-direction: column;
		gap: 10px;
		margin: 12px 0 4px;
	}
	.chat-label {
		display: block;
		color: var(--muted);
		font-size: 0.75rem;
		text-transform: uppercase;
		letter-spacing: 0.05em;
		margin-bottom: 4px;
	}
	.chat-q {
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-radius: var(--radius);
		padding: 10px 14px;
	}
	.chat-question {
		margin: 0;
		font-weight: 600;
		overflow-wrap: anywhere;
	}
	.chat-a {
		border-left: 2px solid var(--border);
		padding-left: 12px;
		min-width: 0;
		overflow-wrap: anywhere;
	}
	.trace dl {
		margin: 10px 0 2px;
	}
	.trace dt {
		color: var(--muted);
		font-size: 0.75rem;
		text-transform: uppercase;
		letter-spacing: 0.05em;
		margin-top: 8px;
	}
	.trace dd {
		margin: 2px 0 0;
		font-size: 0.9rem;
	}
	.brain-steps {
		white-space: pre-line;
		overflow-wrap: anywhere;
		font-family: ui-monospace, monospace;
		font-size: 0.82rem;
	}

	/* ── Tabellen / meta ───────────────────────────────────────────── */
	table {
		width: 100%;
		border-collapse: collapse;
	}
	th,
	td {
		text-align: left;
		padding: 8px 10px;
		border-bottom: 1px solid var(--border);
	}
	th {
		color: var(--muted);
		font-size: 0.8rem;
		font-weight: 600;
	}
	.meta {
		color: var(--muted);
		font-size: 0.85rem;
	}
	.meta-link {
		color: var(--accent);
		text-decoration: none;
	}
	.meta-link:hover {
		text-decoration: underline;
	}
	.live-tag {
		font-size: 0.62rem;
		text-transform: uppercase;
		letter-spacing: 0.08em;
		color: var(--muted);
		border: 1px solid var(--border);
		border-radius: 999px;
		padding: 2px 8px;
		margin-left: 6px;
	}
</style>
