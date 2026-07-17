<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import { compactRanges } from '$lib/ranges';
	import { relationBulkActionsVisible } from '$lib/reviewBulk';
	import AnswerView from '$lib/AnswerView.svelte';

	let { data, form } = $props();

	interface CardItem {
		riftboundId: string; name: string; setLabel: string | null; rarity: string | null;
		type: string | null; variantOf: string | null; embedded: boolean;
		mechanics: string[] | null; updatedAt: string;
	}
	interface Paged<T> { total: number; page: number; pageSize: number; items: T[]; }
	interface ChunkSource { sourceId: string; count: number; }
	interface ChunkItem {
		id: number; sourceId: string; sectionCode: string | null;
		page: number | null; chunkIndex: number; snippet: string;
	}
	interface ChunkOverview extends Paged<ChunkItem> { sources: ChunkSource[]; }
	interface BanItem {
		id: number; name: string; cardRiftboundId: string | null; kind: string;
		format: string; effectiveFrom: string | null; sourceUrl: string; detectedAt: string;
	}
	interface ErratumItem {
		id: number; cardName: string; cardRiftboundId: string | null;
		newText: string; sourceUrl: string; detectedAt: string;
		/** Temporele precedentie (#168): vanaf wanneer deze tekst gold. */
		effectiveFrom: string | null;
		/** Supersede-signaal (#168): gevuld als een andere errata-rij over
		 *  dezelfde kaart een hogere precedentie heeft — kandidaat-signaal,
		 *  puur berekend, niets is verwijderd of overschreven. */
		supersededByErratumId: number | null;
	}
	interface InteractionItem {
		id: number; kind: string; explanation: string; cardAId: string; cardAName: string;
		cardBId: string; cardBName: string; detectedAt: string;
	}
	/** Bevestiging (#206): secundaire change (andere bron, zelfde
	 *  gebeurtenis) genest onder de primaire. sourceUrl is Source.Url — een
	 *  geregistreerde bron-kolom, geen aparte UrlGuard-Safe-vlag nodig
	 *  (dat patroon is voor vrije/LLM-tekst-URL's zoals bij correcties/claims). */
	interface ChangeConfirmationItem {
		id: number; sourceId: string; sourceName: string; sourceUrl: string;
		trustTier: number; summary: string | null; detectedAt: string;
	}
	interface ChangeItem {
		id: number; sourceId: string; sourceName: string; changeType: string;
		severity: string; summary: string | null; meaning: string | null; detectedAt: string;
		/** #206: leeg tenzij andere bronnen hetzelfde gebeurtenis bevestigden. */
		confirmedBy: ChangeConfirmationItem[];
	}
	interface CorrectionItem {
		id: number; scope: string; ref: string; text: string; question: string | null;
		provenance: string | null; status: string; createdAt: string; verifiedAt: string | null;
		/** "Waar besloten" (#166) — bewijs bij het reviewen van een voorstel. */
		sourceRef: string | null;
		/** Bron-naam (#184) — resolvet alleen voor clarify-mining-items; anders null. */
		sourceName: string | null;
		/** UrlGuard-gecheckt (#184, sanitize vóór {@html}) — alleen dan een klikbare link. */
		sourceRefSafe: boolean;
		/** Waarom een clarify-item (nog) niet verified is (#177 hybride poort). */
		statusReason: string | null;
		/** Beheerder-opmerking (#184) — bewaard en meegenomen bij her-evaluatie. */
		reviewNote: string | null;
	}
	interface KnowledgeItem {
		id: number; kind: string; topic: string; title: string; body: string;
		sectionRefs: string | null; status: string; updatedAt: string;
	}
	interface ClaimSourceItem {
		sourceId: string; sourceName: string; url: string;
		/** UrlGuard-gecheckt (#184, sanitize vóór {@html}) — alleen dan een klikbare link. */
		urlSafe: boolean;
		quote: string | null; seenAt: string;
	}
	interface ClaimItem {
		id: number; topicType: string; topicRef: string; statement: string;
		corroboration: number; trustScore: number; status: string;
		statusReason: string | null; officialStatus: string;
		reviewNote: string | null; archivedAt: string | null;
		firstSeen: string; lastSeen: string; sources: ClaimSourceItem[];
	}
	interface ClaimStatusCount { status: string; count: number; }
	interface ClaimOverview extends Paged<ClaimItem> {
		statusCounts: ClaimStatusCount[]; archived: number;
	}
	interface RelationItem {
		id: number; fromRef: string; fromName: string | null; toRef: string; toName: string | null;
		kind: string; explanation: string; provenance: string; trust: number;
		status: string; reviewNote: string | null; archivedAt: string | null; detectedAt: string;
		/** LLM-triage-aanbeveling (#199 v1): "accept"|"reject"|"unsure", null = nog niet getriaged. */
		recommendation: string | null;
		/** Eén zin motivering (Engels, met de geraadpleegde refs erin gevouwen). */
		recommendationReason: string | null;
	}
	interface RelationRecommendationCount {
		recommendation: string;
		count: number;
		/** Max RecommendedAt binnen de groep zoals geladen — samen met count
		 *  de TOCTOU-fence die de bulk-actie meestuurt (review-fix #199). */
		asOf: string;
	}
	interface RelationKindExample {
		fromRef: string; fromName: string | null; toRef: string; toName: string | null;
	}
	interface RelationKindItem {
		id: number; kind: string; status: string; occurrences: number;
		firstSeen: string; reviewedAt: string | null;
		// Bewijs bij kandidaat-kinds (#123): tot 3 voorbeeldvoorstellen.
		examples: RelationKindExample[];
	}
	interface RelationOverview extends Paged<RelationItem> {
		statusCounts: ClaimStatusCount[]; archived: number; kinds: RelationKindItem[];
		recommendationCounts: RelationRecommendationCount[];
	}
	interface ProposalItem {
		id: number; url: string; name: string; type: string; motivation: string;
		status: string; foundAt: string; reviewedAt: string | null;
	}
	interface ProposalStatusCount { status: string; count: number; }
	interface ProposalOverview extends Paged<ProposalItem> { statusCounts: ProposalStatusCount[]; }
	// Set-dekking (#145): exact uit de riftbound-id's afgeleid; missingNumbers
	// is de exacte lijst, de weergave vouwt hem compact (compactRanges).
	interface SetTotalDeviation { total: number; count: number; }
	interface SetCoverageItem {
		setId: string; name: string; publishedOn: string | null; syncedAt: string | null;
		baseTotal: number | null; present: number; missingNumbers: number[];
		variants: number; totalDeviations: SetTotalDeviation[];
	}
	interface SetCoverageOverview { sets: number; incomplete: number; items: SetCoverageItem[]; }
	interface GapCoverage {
		cards: number; cardsWithoutEmbedding: number; cardsWithoutMechanics: number;
		ruleChunks: number; ruleChunksWithoutEmbedding: number;
		primerTopics: number; primerTopicsMissing: number; primerDrafts: number;
		openMechanicCandidates: number; tracesConsidered: number;
	}
	interface GapQuestion {
		signal: string; question: string; questionType: string | null; createdAt: string;
	}
	interface GapSource {
		id: string; name: string; trustTier: number; documents: number; chunks: number;
		lastChecked: string | null; lastChangeAt: string | null;
	}
	interface GapDriftEntry { label: string; postgres: number; graph: number; delta: number; }
	interface GapDrift { graphAvailable: boolean; detail: string | null; entries: GapDriftEntry[]; }
	interface GapAgingSignal { kind: string; title: string; reason: string; at: string; }
	interface GapSetCoverageSignal { setId: string; name: string; missing: number; baseTotal: number; }
	// Verwerkingssignaal (#171, GapAgingSignal-stijl): een bron waarvan de
	// laatste scan/vervolgstap mislukte, hangt, of die nog nooit gescand is.
	interface GapSourceProcessingSignal {
		sourceId: string; name: string; status: string; reason: string; at: string | null;
	}
	interface GapsReport {
		coverage: GapCoverage; questions: GapQuestion[]; sources: GapSource[]; drift: GapDrift;
		aging: GapAgingSignal[]; setCoverage: GapSetCoverageSignal[];
		sourceProcessing: GapSourceProcessingSignal[];
	}
	// Piltover Archive-decks (#15): attributie per deck via sourceUrl;
	// unknownCards = kaartregels zonder koppeling aan onze kaarten.
	interface DeckItem {
		id: number; paId: string; name: string | null; domains: string[];
		cards: number; unknownCards: number; views: number; likes: number;
		sourceUrl: string; paUpdatedAt: string | null; fetchedAt: string;
	}
	// Bron-feeds (#167): index-pagina die periodiek op nieuwe artikel-URL's
	// wordt afgespeurd. sourceCount = hoeveel bronnen deze feed tot nu toe
	// ontdekte (alleen zinvol bij autoApprove).
	interface FeedItem {
		id: string; name: string; url: string; enabled: boolean; autoApprove: boolean;
		categoryFilter: string | null; cadence: string; lastChecked: string | null;
		sourceCount: number;
	}
	interface UserItem {
		id: number; email: string; blocked: boolean; dailyQuota: number; dailyPhotoQuota: number;
		dailyAgenticQuota: number;
		createdAt: string; lastLoginAt: string | null;
		questions: number; photos: number; cheap: number; hard: number;
		failed: number; avgDurationMs: number;
		inputTokens: number; outputTokens: number;
	}
	// Tokentotalen per antwoordpad (#121): over álle vragen in de periode,
	// dus ook anonieme. Vragen zonder gemeten usage tellen als 0 tokens —
	// de som is een ondergrens.
	interface PathUsage { path: string; questions: number; inputTokens: number; outputTokens: number; }
	interface UserOverview extends Paged<UserItem> {
		period: string; anonQuestions: number; anonPhotos: number; paths: PathUsage[];
	}

	// Judge-benchmark (#158): run-historie + het detail van de gekozen run.
	interface BenchmarkRunItem {
		id: number; label: string | null; questionCount: number; keyedCount: number;
		correctCount: number; scorePercent: number | null;
		startedAt: string; completedAt: string | null;
	}
	interface BenchmarkResultItem {
		id: number; questionId: number; externalKey: string; category: string; question: string;
		options: string[]; correctIndex: number | null; explanation: string | null;
		answer: string; chosenIndex: number | null; correct: boolean | null;
		durationMs: number; inputTokens: number | null; outputTokens: number | null;
	}
	interface BenchmarkRunDetail { run: BenchmarkRunItem; results: BenchmarkResultItem[]; }

	// Model-sweep (#174): elk model 2 runs naast elkaar + consistentie, en de
	// sweep-historie (verloop van modelkwaliteit/-snelheid over tijd).
	interface BenchmarkSweepRunItem {
		runId: number; runIndex: number; scorePercent: number | null;
		keyedCount: number; correctCount: number; questionCount: number;
		totalDurationMs: number; avgDurationMs: number;
		totalInputTokens: number; totalOutputTokens: number;
	}
	interface BenchmarkSweepModelItem {
		model: string; runs: BenchmarkSweepRunItem[]; consistent: boolean | null;
	}
	interface BenchmarkSweepSummary {
		sweepId: number; startedAt: string; modelCount: number; questionCount: number;
	}
	interface BenchmarkSweepDetail {
		sweepId: number; startedAt: string; models: BenchmarkSweepModelItem[];
	}
	interface BenchmarkOverview {
		runs: BenchmarkRunItem[]; selected: BenchmarkRunDetail | null;
		sweeps: BenchmarkSweepSummary[]; selectedSweep: BenchmarkSweepDetail | null;
	}

	const TITLES: Record<string, { title: string; sub: string }> = {
		kaarten: { title: 'Kaarten', sub: 'alle kaarten in de database, doorklikbaar naar de kaartpagina' },
		embeddings: { title: 'Embeddings', sub: 'welke kaarten een embedding hebben — en welke nog niet' },
		analyse: { title: 'Mechanieken-analyse', sub: 'LLM-geminede mechanieken per kaart, plus de restlijst' },
		regelsecties: { title: 'Regelsecties', sub: 'sectie-chunks per bron, in documentvolgorde' },
		bans: { title: 'Bans', sub: 'actuele banlijst uit de officiële bronnen' },
		errata: { title: 'Errata', sub: 'actuele oracle-teksten per kaart' },
		interacties: { title: 'Interacties', sub: 'LLM-geverifieerde kaart-interacties' },
		wijzigingen: { title: 'Wijzigingen', sub: 'de wijzigingshistorie die de feed voedt' },
		correcties: { title: 'Correcties', sub: 'feedback op antwoorden en geverifieerde rulings' },
		primer: { title: 'Spelbegrip-primer', sub: 'alle spelbegrip-docs, bewerkbaar — goedgekeurde docs voeden elke ruling' },
		claims: { title: 'Community-claims', sub: 'beweringen uit community-bronnen met corroboratie en bron-trust — geaccepteerde claims worden het community-kanaal (#51); standaard staat hier alleen wat aandacht vraagt, de rest zit achter de chips' },
		relaties: { title: 'Relaties', sub: 'LLM-ontdekte relaties tussen de kennislagen — de graph-sync projecteert alleen geaccepteerde en ongereviewde voorstellen met een geaccepteerd kind, verworpen nooit; standaard staat hier alleen wat aandacht vraagt' },
		voorstellen: { title: 'Bronvoorstellen', sub: 'webvondsten van de scout — accepteren zet de bron uitgeschakeld in het register, aanzetten gaat daarna via de bronnen-tabel; standaard staan hier alleen de nog te beoordelen vondsten' },
		gaten: { title: 'Kennis-gaten', sub: 'waar de kennisbank dun is — gemeten, niet geraden: dekking, vraag-signalen en bron-versheid' },
		decks: { title: 'Decks', sub: 'community-decks van Piltover Archive, met bronvermelding en deep-link terug — wij bouwen bewust geen eigen deckbuilder (#15)' },
		setdekking: { title: 'Set-dekking', sub: 'per set welke kaartnummers we hebben en wélke exact ontbreken — afgeleid uit de riftbound-id\'s zelf ("ogn-074-298" = nr. 74 van 298)' },
		gebruikers: { title: 'Gebruikers', sub: 'accounts met hun LLM-gebruik per periode — tokentotalen per pad en per account zijn het kosteninzicht; quota en blokkade zijn hier bij te stellen' },
		benchmark: { title: 'Benchmark', sub: 'de vaste scheidsrechter-vragenset door de /ask-pipeline — geïsoleerd van de kennisbank (geen trace, metric of relatie-terugkoppeling); score alleen over de vragen met een bevestigd antwoord' },
		feeds: { title: 'Bron-feeds', sub: 'index-pagina\'s die periodiek op nieuwe artikelen worden afgespeurd — vertrouwde feeds zetten een nieuw artikel direct als bron, andere als voorstel (#167)' }
	};
	const meta = $derived(TITLES[data.kind]);

	const isCardKind = $derived(['kaarten', 'embeddings', 'analyse'].includes(data.kind));
	const cards = $derived(isCardKind ? (data.data as Paged<CardItem> | null) : null);
	const chunks = $derived(data.kind === 'regelsecties' ? (data.data as ChunkOverview | null) : null);
	const bans = $derived(data.kind === 'bans' ? ((data.data ?? []) as BanItem[]) : []);
	const errata = $derived(data.kind === 'errata' ? ((data.data ?? []) as ErratumItem[]) : []);
	const interactions = $derived(data.kind === 'interacties' ? (data.data as Paged<InteractionItem> | null) : null);
	const changes = $derived(data.kind === 'wijzigingen' ? (data.data as Paged<ChangeItem> | null) : null);
	const corrections = $derived(data.kind === 'correcties' ? ((data.data ?? []) as CorrectionItem[]) : []);
	const knowledge = $derived(data.kind === 'primer' ? ((data.data ?? []) as KnowledgeItem[]) : []);
	const claims = $derived(data.kind === 'claims' ? (data.data as ClaimOverview | null) : null);
	const relations = $derived(data.kind === 'relaties' ? (data.data as RelationOverview | null) : null);
	const relationKindCandidates = $derived((relations?.kinds ?? []).filter((k) => k.status === 'candidate'));
	const acceptedRelationKinds = $derived((relations?.kinds ?? []).filter((k) => k.status === 'accepted'));
	const proposals = $derived(data.kind === 'voorstellen' ? (data.data as ProposalOverview | null) : null);

	// Status-vocabulaire van de claims-pipeline (#50): kleur + NL-label.
	const CLAIM_STATUS: Record<string, { label: string; badge: string }> = {
		unreviewed: { label: 'te reviewen', badge: 'warn-b' },
		accepted: { label: 'geaccepteerd', badge: 'ok-b' },
		rejected: { label: 'verworpen', badge: 'err' },
		superseded: { label: 'verouderd', badge: 'err' }
	};

	// Status-vocabulaire van de correcties/clarify-reviewqueue (#177/#184).
	const CORRECTION_STATUS: Record<string, { label: string; badge: string }> = {
		unverified: { label: 'ter review', badge: 'warn-b' },
		verified: { label: 'geverifieerd', badge: 'ok-b' },
		rejected: { label: 'afgewezen', badge: 'err' }
	};
	function correctionStatus(status: string): { label: string; badge: string } {
		return CORRECTION_STATUS[status] ?? { label: status, badge: 'warn-b' };
	}
	function claimStatus(status: string): { label: string; badge: string } {
		return CLAIM_STATUS[status] ?? { label: status, badge: 'warn-b' };
	}

	// Status-vocabulaire van de bronvoorstellen (#63): kleur + NL-label.
	const PROPOSAL_STATUS: Record<string, { label: string; badge: string }> = {
		proposed: { label: 'te beoordelen', badge: 'warn-b' },
		accepted: { label: 'geaccepteerd', badge: 'ok-b' },
		rejected: { label: 'verworpen', badge: 'err' }
	};
	function proposalStatus(status: string): { label: string; badge: string } {
		return PROPOSAL_STATUS[status] ?? { label: status, badge: 'warn-b' };
	}

	// Triage-aanbeveling (#199 v1): kleur + label — puur een aanbeveling, geen
	// autoriteit (status = kleur + tekst blijft ook hier de regel).
	const RECOMMENDATION_LABEL: Record<string, { label: string; badge: string }> = {
		accept: { label: 'aanbeveling: accepteren', badge: 'ok-b' },
		reject: { label: 'aanbeveling: verwerpen', badge: 'err' },
		unsure: { label: 'aanbeveling: onzeker', badge: 'warn-b' }
	};
	function recommendationInfo(rec: string): { label: string; badge: string } {
		return RECOMMENDATION_LABEL[rec] ?? { label: `aanbeveling: ${rec}`, badge: 'warn-b' };
	}

	// Chip-tellingen (#124): statussen tellen over het niet-gearchiveerde deel;
	// de default-chip is de te-reviewen-telling.
	function statusCount(counts: ClaimStatusCount[], status: string): number {
		return counts.find((s) => s.status === status)?.count ?? 0;
	}
	// "Archiveer alle afgehandelde": alles waarover al besloten is (of wat de
	// pipeline zelf afwees) — te-reviewen items blijven staan.
	const claimsHandled = $derived(
		(claims?.statusCounts ?? []).filter((s) => s.status !== 'unreviewed').reduce((a, s) => a + s.count, 0)
	);
	const relationsHandled = $derived(
		(relations?.statusCounts ?? []).filter((s) => s.status !== 'unreviewed').reduce((a, s) => a + s.count, 0)
	);
	// Bulk-actie per aanbevelingsgroep (#199 v1): de hele groep (telling +
	// asOf-fence) — de knop verdwijnt vanzelf als de groep leeg is, en
	// rendert alléén in de default-/te-reviewen-weergave (review-fix
	// findings 3/5/8: daar zijn telling, zichtbare items en actie-scope
	// hetzelfde universum — unreviewed én niet gearchiveerd).
	function recommendationGroup(rec: string): RelationRecommendationCount | undefined {
		return (relations?.recommendationCounts ?? []).find((c) => c.recommendation === rec);
	}
	const acceptGroup = $derived(recommendationGroup('accept'));
	const rejectGroup = $derived(recommendationGroup('reject'));
	// Bulk-uitkomst bij de juiste knop: het resultaat (succes-telling of de
	// 409-fence-/foutmelding) van de laatste bulk-actie, getypeerd — de
	// ActionData-union laat zich in de template niet netjes narrowen.
	const bulkForm = $derived(
		form && 'bulkDecision' in form
			? (form as { bulkDecision: string; bulkApplied?: number; error?: string })
			: null
	);

	const gaps = $derived(data.kind === 'gaten' ? (data.data as GapsReport | null) : null);
	const decks = $derived(data.kind === 'decks' ? (data.data as Paged<DeckItem> | null) : null);
	const coverage = $derived(data.kind === 'setdekking' ? (data.data as SetCoverageOverview | null) : null);
	const users = $derived(data.kind === 'gebruikers' ? (data.data as UserOverview | null) : null);
	const benchmark = $derived(data.kind === 'benchmark' ? (data.data as BenchmarkOverview | null) : null);

	// Model-sweep (#174): "modellen gerangschikt op score en op snelheid" —
	// client-side sorteren, geen extra rb-api-vorm nodig. Score/snelheid per
	// model = het gemiddelde over de 2 runs (mist een run z'n score nog geen
	// sleutel, dan telt alleen de wel-gescoorde run mee).
	let sweepSort = $state<'score' | 'speed'>('score');
	function sweepAvgScore(m: BenchmarkSweepModelItem): number | null {
		const scores = m.runs.map((r) => r.scorePercent).filter((s): s is number => s !== null);
		return scores.length ? scores.reduce((a, b) => a + b, 0) / scores.length : null;
	}
	function sweepAvgSpeedMs(m: BenchmarkSweepModelItem): number {
		const speeds = m.runs.map((r) => r.avgDurationMs).filter((s) => s > 0);
		return speeds.length ? speeds.reduce((a, b) => a + b, 0) / speeds.length : Infinity;
	}
	const sweepModelsSorted = $derived.by(() => {
		const models = benchmark?.selectedSweep?.models ?? [];
		const sorted = [...models];
		if (sweepSort === 'score') {
			sorted.sort((a, b) => (sweepAvgScore(b) ?? -1) - (sweepAvgScore(a) ?? -1));
		} else {
			sorted.sort((a, b) => sweepAvgSpeedMs(a) - sweepAvgSpeedMs(b));
		}
		return sorted;
	});
	const feeds = $derived(data.kind === 'feeds' ? ((data.data ?? []) as FeedItem[]) : []);
	// Welke feed in bewerk-modus staat; zelfde reset-bij-route-wissel-patroon
	// als primer se `editing` hierboven, maar op string-id (feed-id's zijn geen getallen).
	let editingFeed = $state<string | null>(null);
	$effect(() => {
		void data.kind;
		editingFeed = null;
	});

	// Meetperiode voor het gebruikers-overzicht (#42): chip-label per waarde.
	const PERIODS: { value: string; label: string }[] = [
		{ value: 'vandaag', label: 'Vandaag' },
		{ value: '7d', label: 'Laatste 7 dagen' },
		{ value: '30d', label: 'Laatste 30 dagen' }
	];

	// Vraag-signalen gegroepeerd per signaal, in vaste presentatievolgorde.
	const SIGNALS: Record<string, { label: string; hint: string }> = {
		'lege-retrieval': { label: 'Lege retrieval', hint: 'de vraag vond geen secties, kaarten of primer — hier weet de bank aantoonbaar niets' },
		'negatieve-feedback': { label: 'Negatieve feedback', hint: 'antwoorden die als onjuist zijn gemeld' },
		'ai-uitval': { label: 'AI-uitval', hint: 'vragen zonder antwoord doordat rb-ai niet beschikbaar was' }
	};
	const gapGroups = $derived(
		gaps
			? Object.entries(SIGNALS)
					.map(([key, m]) => ({ key, ...m, items: gaps.questions.filter((q) => q.signal === key) }))
					.filter((g) => g.items.length > 0)
			: []
	);

	function daysAgo(iso: string | null): number | null {
		return iso ? Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000) : null;
	}

	// saveKnowledge-fouten dragen het doc-id mee zodat de melding bij het
	// juiste bewerkformulier landt; andere actie-fouten hebben geen id.
	const formDocId = $derived(form && 'id' in form ? (form.id as number) : null);
	// Feed-id's zijn strings (geen getallen) — eigen variant van formDocId
	// hierboven, zelfde fail-vorm ({ error, id }) van saveFeed/deleteFeed.
	const formFeedId = $derived(form && 'id' in form ? String(form.id) : null);

	// Welk primer-doc in bewerk-modus staat; reset bij route-hergebruik
	// (dit component blijft leven bij navigatie tussen kinds).
	let editing = $state<number | null>(null);
	$effect(() => {
		void data.kind;
		editing = null;
	});

	const paged = $derived(cards ?? chunks ?? interactions ?? changes ?? claims ?? relations ?? proposals ?? decks ?? users);

	/** Deeplink naar de brein-verkenner: elke ref is daar klikbaar te verkennen. */
	function graphHref(ref: string): string {
		return `/graph?ref=${encodeURIComponent(ref)}`;
	}
	const totalPages = $derived(paged ? Math.max(1, Math.ceil(paged.total / paged.pageSize)) : 1);

	function href(overrides: { page?: number; filter?: string; source?: string }): string {
		const sp = new URLSearchParams();
		if (data.q) sp.set('q', data.q);
		const filter = overrides.filter ?? data.filter;
		if (filter) sp.set('filter', filter);
		const source = overrides.source ?? data.source;
		if (source) sp.set('source', source);
		sp.set('page', String(overrides.page ?? 1));
		return `?${sp}`;
	}

	function fmtDate(iso: string | null): string {
		return iso ? new Date(iso).toLocaleDateString('nl-NL') : '—';
	}

	// Tokentellingen (#121): NL-groepering (12.345) — grote getallen blijven
	// zo scanbaar in de tabellen.
	function fmtTokens(n: number): string {
		return n.toLocaleString('nl-NL');
	}

	// Judge-benchmark (#158): A/B/C/… voor optie-index 0/1/2/… — zelfde
	// letterlijst als BenchmarkPrompt.Label in rb-api.
	function optionLabel(i: number): string {
		return String.fromCharCode(65 + i);
	}
</script>

<svelte:head><title>{meta.title} — Beheer — RB Rules</title></svelte:head>

<main>
	<nav class="crumb"><a href="/admin">Beheer</a> / {meta.title}</nav>
	<h1>{meta.title}</h1>
	<p class="sub">{meta.sub}</p>

	{#if data.apiDown}
		<p class="warn">rb-api is niet bereikbaar — probeer het zo opnieuw.</p>
	{:else}
		<!-- Fouten mét doc-id landen bij het bijbehorende bewerkformulier. -->
		{#if form?.error && formDocId === null}<p class="warn">{form.error}</p>{/if}

		<!-- Filters -->
		{#if data.kind === 'kaarten'}
			<form method="GET" class="filters">
				<input type="search" name="q" value={data.q} placeholder="Zoek op kaartnaam" aria-label="Zoek op kaartnaam" />
				<button type="submit">Zoek</button>
			</form>
		{:else if data.kind === 'embeddings'}
			<div class="chips">
				<a class="chip" class:active={data.filter === 'embedded'} aria-current={data.filter === 'embedded' ? 'page' : undefined} href={href({ filter: 'embedded', page: 1 })}>Geëmbed</a>
				<a class="chip" class:active={data.filter === 'unembedded'} aria-current={data.filter === 'unembedded' ? 'page' : undefined} href={href({ filter: 'unembedded', page: 1 })}>Nog niet geëmbed</a>
			</div>
		{:else if data.kind === 'analyse'}
			<div class="chips">
				<a class="chip" class:active={data.filter === 'mined'} aria-current={data.filter === 'mined' ? 'page' : undefined} href={href({ filter: 'mined', page: 1 })}>Geanalyseerd</a>
				<a class="chip" class:active={data.filter === 'unmined'} aria-current={data.filter === 'unmined' ? 'page' : undefined} href={href({ filter: 'unmined', page: 1 })}>Nog niet geanalyseerd</a>
			</div>
		{:else if data.kind === 'claims' && claims}
			<!-- Default-weergave (#124): alleen wat aandacht vraagt; afgehandeld,
			     archief en alles achter de chips. -->
			<div class="chips">
				<a class="chip" class:active={!data.filter} aria-current={!data.filter ? 'page' : undefined} href={href({ filter: '', page: 1 })}>Te reviewen ({statusCount(claims.statusCounts, 'unreviewed')})</a>
				{#each claims.statusCounts.filter((s) => s.status !== 'unreviewed') as s (s.status)}
					<a class="chip" class:active={data.filter === s.status} aria-current={data.filter === s.status ? 'page' : undefined} href={href({ filter: s.status, page: 1 })}>{claimStatus(s.status).label} ({s.count})</a>
				{/each}
				<a class="chip" class:active={data.filter === 'archived'} aria-current={data.filter === 'archived' ? 'page' : undefined} href={href({ filter: 'archived', page: 1 })}>Gearchiveerd ({claims.archived})</a>
				<a class="chip" class:active={data.filter === 'all'} aria-current={data.filter === 'all' ? 'page' : undefined} href={href({ filter: 'all', page: 1 })}>Alles ({claims.statusCounts.reduce((a, s) => a + s.count, 0) + claims.archived})</a>
			</div>
			{#if claimsHandled}
				<form method="POST" action="?/archiveHandledClaims" use:enhance class="archive-all">
					<button class="ghost small" title="Geaccepteerd, verworpen en verouderd het archief in — te reviewen blijft staan; status en /ask-deelname veranderen niet">Archiveer alle afgehandelde ({claimsHandled})</button>
					{#if form && 'archived' in form && form.archived !== undefined}<span class="meta">{form.archived} {form.archived === 1 ? 'item' : 'items'} gearchiveerd.</span>{/if}
				</form>
			{/if}
		{:else if data.kind === 'relaties' && relations}
			<!-- Zelfde default-weergave als claims (#124); het statusvocabulaire
			     deelt de claims-labels (#116). -->
			<div class="chips">
				<a class="chip" class:active={!data.filter} aria-current={!data.filter ? 'page' : undefined} href={href({ filter: '', page: 1 })}>Te reviewen ({statusCount(relations.statusCounts, 'unreviewed')})</a>
				{#each relations.statusCounts.filter((s) => s.status !== 'unreviewed') as s (s.status)}
					<a class="chip" class:active={data.filter === s.status} aria-current={data.filter === s.status ? 'page' : undefined} href={href({ filter: s.status, page: 1 })}>{claimStatus(s.status).label} ({s.count})</a>
				{/each}
				<a class="chip" class:active={data.filter === 'archived'} aria-current={data.filter === 'archived' ? 'page' : undefined} href={href({ filter: 'archived', page: 1 })}>Gearchiveerd ({relations.archived})</a>
				<a class="chip" class:active={data.filter === 'all'} aria-current={data.filter === 'all' ? 'page' : undefined} href={href({ filter: 'all', page: 1 })}>Alles ({relations.statusCounts.reduce((a, s) => a + s.count, 0) + relations.archived})</a>
			</div>
			{#if relationsHandled}
				<form method="POST" action="?/archiveHandledRelations" use:enhance class="archive-all">
					<button class="ghost small" title="Geaccepteerd en verworpen het archief in — te reviewen blijft staan; de graph-projectie kijkt alleen naar de status">Archiveer alle afgehandelde ({relationsHandled})</button>
					{#if form && 'archived' in form && form.archived !== undefined}<span class="meta">{form.archived} {form.archived === 1 ? 'item' : 'items'} gearchiveerd.</span>{/if}
				</form>
			{/if}
			<!-- Bulk-actie per aanbevelingsgroep (#199 v1): de machine sorteert
			     voor (triage-job "relatie-triage"), de mens klikt — één klik
			     bevestigt/verwerpt de hele groep via hetzelfde accept-/reject-pad
			     als de losse knoppen per item. confirm() vóór de post, telling in
			     de knop zelf. Alléén in de default-/te-reviewen-weergave
			     (review-fix: telling, zichtbare items en actie-scope moeten
			     hetzelfde universum zijn) en mét de TOCTOU-fence (expectedCount +
			     asOf): is de groep intussen veranderd — bv. door een gelijktijdige
			     triage-run — dan weigert rb-api met 409 en is er niets beslist. -->
			{#if relationBulkActionsVisible(data.filter) && acceptGroup}
				<form
					method="POST"
					action="?/bulkDecideRelations"
					use:enhance={({ cancel }) => {
						if (
							!confirm(
								`Dit accepteert in één keer alle ${acceptGroup.count} voorstellen met aanbeveling "accepteren" — hetzelfde accept-pad als los bevestigen. Doorgaan?`
							)
						) {
							cancel();
							return;
						}
						return async ({ update }) => {
							await update();
							await invalidateAll();
						};
					}}
					class="archive-all"
				>
					<input type="hidden" name="recommendation" value="accept" />
					<input type="hidden" name="decision" value="accept" />
					<input type="hidden" name="expectedCount" value={acceptGroup.count} />
					<input type="hidden" name="asOf" value={acceptGroup.asOf} />
					<button class="small" title="Bevestigt alle voorstellen met aanbeveling 'accepteren' in één keer">
						Accepteer alle {acceptGroup.count} met aanbeveling accept
					</button>
					{#if bulkForm?.bulkDecision === 'accept' && bulkForm.bulkApplied !== undefined}
						<span class="meta">{bulkForm.bulkApplied} {bulkForm.bulkApplied === 1 ? 'voorstel' : 'voorstellen'} geaccepteerd.</span>
					{/if}
					<!-- 409-fence of andere fout — de melding landt bij de juiste knop. -->
					{#if bulkForm?.bulkDecision === 'accept' && bulkForm.error}
						<p class="warn">{bulkForm.error}</p>
					{/if}
				</form>
			{/if}
			{#if relationBulkActionsVisible(data.filter) && rejectGroup}
				<form
					method="POST"
					action="?/bulkDecideRelations"
					use:enhance={({ cancel }) => {
						if (
							!confirm(
								`Dit verwerpt in één keer alle ${rejectGroup.count} voorstellen met aanbeveling "verwerpen" — hetzelfde reject-pad als los verwerpen. Doorgaan?`
							)
						) {
							cancel();
							return;
						}
						return async ({ update }) => {
							await update();
							await invalidateAll();
						};
					}}
					class="archive-all"
				>
					<input type="hidden" name="recommendation" value="reject" />
					<input type="hidden" name="decision" value="reject" />
					<input type="hidden" name="expectedCount" value={rejectGroup.count} />
					<input type="hidden" name="asOf" value={rejectGroup.asOf} />
					<button class="ghost small" title="Verwerpt alle voorstellen met aanbeveling 'verwerpen' in één keer">
						Verwerp alle {rejectGroup.count} met aanbeveling reject
					</button>
					{#if bulkForm?.bulkDecision === 'reject' && bulkForm.bulkApplied !== undefined}
						<span class="meta">{bulkForm.bulkApplied} {bulkForm.bulkApplied === 1 ? 'voorstel' : 'voorstellen'} verworpen.</span>
					{/if}
					<!-- 409-fence of andere fout — de melding landt bij de juiste knop. -->
					{#if bulkForm?.bulkDecision === 'reject' && bulkForm.error}
						<p class="warn">{bulkForm.error}</p>
					{/if}
				</form>
			{/if}
		{:else if data.kind === 'voorstellen' && proposals}
			<!-- Default (#124): alleen te beoordelen — de bestaande statussen
			     zíjn hier het archief (KISS, geen schema-wijziging). -->
			<div class="chips">
				<a class="chip" class:active={data.filter === 'proposed'} aria-current={data.filter === 'proposed' ? 'page' : undefined} href={href({ filter: 'proposed', page: 1 })}>Te beoordelen ({statusCount(proposals.statusCounts, 'proposed')})</a>
				{#each proposals.statusCounts.filter((s) => s.status !== 'proposed') as s (s.status)}
					<a class="chip" class:active={data.filter === s.status} aria-current={data.filter === s.status ? 'page' : undefined} href={href({ filter: s.status, page: 1 })}>{proposalStatus(s.status).label} ({s.count})</a>
				{/each}
				<a class="chip" class:active={data.filter === 'all'} aria-current={data.filter === 'all' ? 'page' : undefined} href={href({ filter: 'all', page: 1 })}>Alles ({proposals.statusCounts.reduce((a, s) => a + s.count, 0)})</a>
			</div>
		{:else if data.kind === 'gebruikers'}
			<div class="chips">
				{#each PERIODS as p (p.value)}
					<a class="chip" class:active={data.filter === p.value} aria-current={data.filter === p.value ? 'page' : undefined} href={href({ filter: p.value, page: 1 })}>{p.label}</a>
				{/each}
			</div>
		{:else if chunks && chunks.sources.length > 1}
			<div class="chips">
				<!-- Som over de bronnen: chunks.total is het gefilterde totaal. -->
				<a class="chip" class:active={!data.source} aria-current={!data.source ? 'page' : undefined} href={href({ source: '', page: 1 })}>Alle bronnen ({chunks.sources.reduce((a, s) => a + s.count, 0)})</a>
				{#each chunks.sources as s (s.sourceId)}
					<a class="chip" class:active={data.source === s.sourceId} aria-current={data.source === s.sourceId ? 'page' : undefined} href={href({ source: s.sourceId, page: 1 })}>{s.sourceId} ({s.count})</a>
				{/each}
			</div>
		{/if}

		{#if paged}
			<p class="meta count">{paged.total} totaal{totalPages > 1 ? ` · pagina ${paged.page} van ${totalPages}` : ''}</p>
		{:else if data.kind === 'correcties' && corrections.length}
			<p class="meta count">{corrections.length} getoond{corrections.length >= 200 ? ' (de laatste 200 — oudere correcties vallen buiten dit overzicht)' : ''}</p>
		{:else if data.kind === 'primer' && knowledge.length}
			<p class="meta count">{knowledge.length} docs · {knowledge.filter((k) => k.status === 'approved').length} goedgekeurd · {knowledge.filter((k) => k.status !== 'approved').length} draft</p>
		{/if}

		<!-- Kaart-overzichten (kaarten / embeddings / analyse) -->
		{#if cards}
			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Kaart</th><th>Set</th><th>Type</th><th>Rarity</th>
							<th>Geëmbed</th><th>Mechanieken</th><th>Bijgewerkt</th>
						</tr>
					</thead>
					<tbody>
						{#each cards.items as c (c.riftboundId)}
							<tr>
								<td>
									<a href="/cards/{c.riftboundId}"><strong>{c.name}</strong></a>
									{#if c.variantOf}<span class="meta"> · variant van {c.variantOf}</span>{/if}
									<br /><span class="meta">{c.riftboundId}</span>
								</td>
								<td class="meta">{c.setLabel ?? '—'}</td>
								<td class="meta">{c.type ?? '—'}</td>
								<td class="meta">{c.rarity ?? '—'}</td>
								<td><span class="badge {c.embedded ? 'ok-b' : 'warn-b'}">{c.embedded ? 'ja' : 'nee'}</span></td>
								<td class="meta mech">
									{#if c.mechanics === null}<span class="badge warn-b">nog niet</span>
									{:else if c.mechanics.length === 0}geen
									{:else}{c.mechanics.join(', ')}{/if}
								</td>
								<td class="meta">{fmtDate(c.updatedAt)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}

		<!-- Regelsecties -->
		{#if chunks}
			<div class="table-wrap">
				<table>
					<thead><tr><th>§</th><th>Bron</th><th>Pagina</th><th>Tekst</th></tr></thead>
					<tbody>
						{#each chunks.items as rc (rc.id)}
							<tr>
								<td class="nowrap"><strong>{rc.sectionCode ?? '—'}</strong></td>
								<td class="meta nowrap">{rc.sourceId}</td>
								<td class="meta">{rc.page ?? '—'}</td>
								<td class="meta snippet">{rc.snippet}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}

		<!-- Bans -->
		{#if data.kind === 'bans'}
			<div class="table-wrap">
				<table>
					<thead><tr><th>Naam</th><th>Soort</th><th>Format</th><th>Vanaf</th><th>Bron</th></tr></thead>
					<tbody>
						{#each bans as b (b.id)}
							<tr>
								<td>
									{#if b.cardRiftboundId}<a href="/cards/{b.cardRiftboundId}"><strong>{b.name}</strong></a>
									{:else}<strong>{b.name}</strong> <span class="meta">(niet gematcht)</span>{/if}
								</td>
								<td class="meta">{b.kind}</td>
								<td class="meta">{b.format}</td>
								<td class="meta">{fmtDate(b.effectiveFrom)}</td>
								<td class="meta"><a href={b.sourceUrl}>bron</a></td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if !bans.length}<p class="meta">Geen bans bekend.</p>{/if}
		{/if}

		<!-- Errata -->
		{#if data.kind === 'errata'}
			{#each errata as e (e.id)}
				<div class="panel item">
					<p class="item-head">
						{#if e.cardRiftboundId}<a href="/cards/{e.cardRiftboundId}"><strong>{e.cardName}</strong></a>
						{:else}<strong>{e.cardName}</strong>{/if}
						<span class="meta">
							{#if e.effectiveFrom}geldig sinds {fmtDate(e.effectiveFrom)} ·{/if}
							waargenomen {fmtDate(e.detectedAt)} · <a href={e.sourceUrl}>bron</a>
						</span>
					</p>
					{#if e.supersededByErratumId}
						<p class="superseded-note">
							Kandidaat-supersede — mogelijk vervangen door een recentere errata-bron (#{e.supersededByErratumId}); beide blijven zichtbaar, geen automatische wijziging.
						</p>
					{/if}
					<p class="pre">{e.newText}</p>
				</div>
			{/each}
			{#if !errata.length}<p class="meta">Geen errata bekend.</p>{/if}
		{/if}

		<!-- Interacties -->
		{#if interactions}
			{#each interactions.items as i (i.id)}
				<div class="panel item">
					<p class="item-head">
						<span class="badge ok-b">{i.kind}</span>
						<a href="/cards/{i.cardAId}"><strong>{i.cardAName}</strong></a>
						<span class="meta">+</span>
						<a href="/cards/{i.cardBId}"><strong>{i.cardBName}</strong></a>
						<span class="meta">{fmtDate(i.detectedAt)}</span>
					</p>
					<p class="pre">{i.explanation}</p>
				</div>
			{/each}
		{/if}

		<!-- Wijzigingen -->
		{#if changes}
			{#each changes.items as c (c.id)}
				<div class="panel item">
					<p class="item-head">
						<span class="badge {c.severity === 'high' ? 'err' : c.severity === 'medium' ? 'warn-b' : 'ok-b'}">{c.severity}</span>
						<strong>{c.changeType}</strong>
						<span class="meta">{c.sourceName} · {fmtDate(c.detectedAt)}</span>
					</p>
					{#if c.summary}<p class="pre">{c.summary}</p>{/if}
					{#if c.meaning}<p class="meta">{c.meaning}</p>{/if}
					{#if !c.summary && !c.meaning}<p class="meta">Zonder samenvatting (zie #58).</p>{/if}
					{#if c.confirmedBy.length > 0}
						<p class="meta refs">
							<span class="badge ok-b">bevestigd</span>
							{#each c.confirmedBy as cb, i (cb.id)}
								{i > 0 ? ', ' : ' door '}<a href={cb.sourceUrl} target="_blank" rel="noopener">{cb.sourceName}</a>
							{/each}
						</p>
						{#each c.confirmedBy as cb (cb.id)}
							{#if cb.summary}<p class="meta refs">{cb.sourceName}: {cb.summary}</p>{/if}
						{/each}
					{/if}
				</div>
			{/each}
		{/if}

		<!-- Correcties (#177) — bron+link en opmerking→her-evaluatie (#184) -->
		{#if data.kind === 'correcties'}
			{#each corrections as c (c.id)}
				<div class="panel item corr">
					<div class="corr-body">
						<p class="item-head">
							<span class="badge {correctionStatus(c.status).badge}">{correctionStatus(c.status).label}</span>
							<span class="meta">{c.scope} · {c.ref === 'down' ? 'gemeld als onjuist' : c.ref === 'up' ? 'bevestigd als juist' : c.ref} · {fmtDate(c.createdAt)}</span>
						</p>
						{#if c.question}<p class="meta">{c.question}</p>{/if}
						<p class="pre">{c.text}</p>
						<!-- Reden dat het item (nog) niet verified is (#177 hybride poort). -->
						{#if c.statusReason}<p class="meta refs">{c.statusReason}</p>{/if}
						{#if c.reviewNote && c.reviewNote !== c.statusReason}
							<p class="meta refs">Opmerking beheerder: {c.reviewNote}</p>
						{/if}
						{#if c.sourceRef}
							<p class="meta refs">
								Bron: {#if c.sourceRefSafe}
									<a href={c.sourceRef} target="_blank" rel="noopener">{c.sourceName ?? c.sourceRef}</a>
								{:else}
									{c.sourceName ?? c.sourceRef}
								{/if}
							</p>
						{/if}
					</div>
					<div class="corr-actions review">
						<!-- Zelfde vorm als claims/relaties: één formulier, de opmerking
						     gaat mee met de knop die hem indient (formaction). -->
						<form method="POST" action="?/verifyCorrection" use:enhance class="review-form">
							<input type="hidden" name="id" value={c.id} />
							<textarea
								name="note"
								rows="2"
								placeholder="Opmerking — bv. een anker-correctie (mechanic:Recall) of toelichting (optioneel)"
								aria-label="Beheerder-opmerking"
								value={c.reviewNote ?? ''}
							></textarea>
							<div class="row">
								{#if c.status !== 'verified'}
									<button class="small" formaction="?/verifyCorrection" title="Maakt dit een gezaghebbende ruling voor toekomstige antwoorden">Verifieer</button>
								{/if}
								{#if c.status !== 'rejected'}
									<button class="ghost small" formaction="?/rejectCorrection" title="Zachte afwijzing — een volgende her-mine respecteert dit">Verwerp</button>
								{/if}
								{#if c.status === 'unverified'}
									<button class="ghost small" formaction="?/reevaluateCorrection" title="Bewaart de opmerking en draait de grondings-/anker-poort opnieuw voor dit item">Opnieuw evalueren</button>
								{/if}
								<button class="ghost small" formaction="?/deleteCorrection" title="Definitief verwijderen">Verwijder</button>
							</div>
							{#if form?.error && formDocId === c.id}<p class="warn">{form.error}</p>{/if}
							{#if form && 'reevaluated' in form && form.reevaluated && formDocId === c.id}
								<p class="meta">
									{#if form.outcome === 'Verified'}Her-evaluatie: geverifieerd.
									{:else}Her-evaluatie: blijft ter review{form.reason ? ` — ${form.reason}` : ''}.{/if}
								</p>
							{/if}
						</form>
					</div>
				</div>
			{/each}
			{#if !corrections.length}<p class="meta">Geen correcties.</p>{/if}
		{/if}

		<!-- Spelbegrip-primer (#70): alle docs leesbaar en bewerkbaar -->
		{#if data.kind === 'primer'}
			{#each knowledge as k (k.id)}
				<div class="panel item">
					{#if editing === k.id}
						<form
							method="POST"
							action="?/saveKnowledge"
							class="edit"
							use:enhance={() =>
								async ({ update, result }) => {
									await update();
									if (result.type === 'success') editing = null;
								}}
						>
							<input type="hidden" name="id" value={k.id} />
							<label>Titel <input name="title" value={k.title} required /></label>
							<label>Tekst <textarea name="body" rows="14" required>{k.body}</textarea></label>
							{#if form?.error && formDocId === k.id}<p class="warn">{form.error}</p>{/if}
							<div class="row">
								<button type="submit">Opslaan</button>
								<button type="button" class="ghost small" onclick={() => (editing = null)}>Annuleer</button>
								<span class="meta">Opslaan embedt de tekst opnieuw; de status ({k.status === 'approved' ? 'goedgekeurd' : 'draft'}) blijft staan.</span>
							</div>
						</form>
					{:else}
						<p class="item-head">
							<strong>{k.title}</strong>
							<span class="badge {k.status === 'approved' ? 'ok-b' : 'warn-b'}">{k.status === 'approved' ? 'goedgekeurd' : 'draft'}</span>
							<span class="meta">{k.topic} · {fmtDate(k.updatedAt)}</span>
						</p>
						<p class="pre">{k.body}</p>
						{#if k.sectionRefs}<p class="meta refs">Gebaseerd op §{k.sectionRefs}</p>{/if}
						<div class="row actions">
							<button type="button" class="ghost small" onclick={() => (editing = k.id)}>Bewerk</button>
							{#if k.status === 'approved'}
								<form method="POST" action="?/unapproveKnowledge" use:enhance>
									<input type="hidden" name="id" value={k.id} />
									<button class="ghost small" title="Doet dan niet meer mee in de vraag-context tot her-goedkeuring">Terug naar draft</button>
								</form>
							{:else}
								<form method="POST" action="?/approveKnowledge" use:enhance>
									<input type="hidden" name="id" value={k.id} />
									<button class="small" title="Goedgekeurde docs gaan mee als spelbegrip in elke vraag">Keur goed</button>
								</form>
							{/if}
							<form method="POST" action="?/deleteKnowledge" use:enhance>
								<input type="hidden" name="id" value={k.id} />
								<button class="ghost small">Verwijder</button>
							</form>
						</div>
					{/if}
				</div>
			{/each}
			{#if !knowledge.length}<p class="meta">Nog geen spelbegrip-docs — draai "Primer genereren" in het beheer.</p>{/if}
		{/if}

		<!-- Community-claims (#50/#124): bewering + bewijsvoering + review-acties
		     met beheerder-notitie, archief en notitie→ruling-promotie -->
		{#if claims}
			{#each claims.items as c (c.id)}
				<div class="panel item corr">
					<div class="corr-body">
						<p class="item-head">
							<span class="badge {claimStatus(c.status).badge}">{claimStatus(c.status).label}</span>
							{#if c.archivedAt}<span class="badge mute">gearchiveerd</span>{/if}
							{#if c.officialStatus === 'confirmed'}<span class="badge ok-b">officieel bevestigd</span>
							{:else if c.officialStatus === 'contradicted'}<span class="badge err">officieel tegengesproken</span>{/if}
							<span class="meta">{c.topicType} · {c.topicRef} · {c.corroboration} {c.corroboration === 1 ? 'bron' : 'bronnen'} · trust {c.trustScore.toFixed(2)} · {fmtDate(c.lastSeen)}</span>
						</p>
						<p class="pre">{c.statement}</p>
						{#if c.statusReason}<p class="meta refs">{c.statusReason}</p>{/if}
						<!-- Reden/notitie zichtbaar bij het item (#124); bij verwerpen-met-
						     reden is de statusReason de notitie — dan niet dubbel tonen. -->
						{#if c.reviewNote && c.reviewNote !== c.statusReason}<p class="meta refs">Notitie beheerder: {c.reviewNote}</p>{/if}
						{#each c.sources as s (s.sourceId)}
							<p class="meta refs">
								<!-- Bron+link (#184): UrlGuard-gecheckt server-side (sanitize
								     vóór {@html}) — zonder dat alleen de kale naam. -->
								{#if s.urlSafe}
									<a href={s.url} target="_blank" rel="noopener">{s.sourceName}</a>
								{:else}
									{s.sourceName}
								{/if}
								{#if s.quote}<span> — "{s.quote}"</span>{/if}
							</p>
						{/each}
					</div>
					<div class="corr-actions review">
						<!-- Eén formulier per item: de notitie gaat mee met de knop die
						     hem indient (formaction). Fouten landen bij dit item. -->
						<form method="POST" action="?/acceptClaim" use:enhance class="review-form">
							<input type="hidden" name="id" value={c.id} />
							<textarea name="note" rows="2" placeholder="Notitie — zo zit het wél (optioneel)" aria-label="Beheerder-notitie" value={c.reviewNote ?? ''}></textarea>
							<div class="row">
								{#if c.status !== 'accepted'}
									<button class="small" formaction="?/acceptClaim" title="Geaccepteerde claims worden het community-kanaal in de vraagbaak (#51); de notitie wordt bewaard">Bevestig</button>
								{/if}
								{#if c.status !== 'rejected'}
									<button class="ghost small" formaction="?/rejectClaim" title="Met notitie is de reden zichtbaar bij het item">Verwerp</button>
								{/if}
								<button class="ghost small" formaction="?/promoteClaimNote" title="Zet de notitie door als geverifieerde ruling — die stuurt voortaan de antwoorden">Notitie → ruling</button>
								{#if c.archivedAt}
									<button class="ghost small" formaction="?/unarchiveClaim" title="Terug in de gewone weergaven">Terug uit archief</button>
								{:else}
									<button class="ghost small" formaction="?/archiveClaim" title="Uit de default-weergave; terugvindbaar via de archief-chip">Archiveer</button>
								{/if}
							</div>
							{#if form?.error && formDocId === c.id}<p class="warn">{form.error}</p>{/if}
							{#if form && 'promoted' in form && form.promoted && formDocId === c.id}
								<p class="meta">Notitie doorgezet als geverifieerde ruling{form.embedded ? '' : ' — embedding volgt bij opnieuw doorzetten (Ollama niet bereikbaar)'}.</p>
							{/if}
						</form>
					</div>
				</div>
			{/each}
			{#if !claims.items.length}
				<p class="meta">
					{#if data.filter}Geen claims in deze weergave.
					{:else if claims.statusCounts.length === 0 && claims.archived === 0}Geen claims — draai "Claims minen" in het beheer.
					{:else}Niets te reviewen — afgehandelde en gearchiveerde claims zitten achter de chips.{/if}
				</p>
			{/if}
		{/if}

		<!-- Relaties (#116): kandidaat-kinds eerst (vocabulaire-poort), dan de
		     voorstellen zelf — van→naar klikbaar naar de brein-verkenner. -->
		{#if relations}
			{#if relationKindCandidates.length}
				<h2 class="gap-h">Kandidaat-kinds <span class="meta">({relationKindCandidates.length} open — relaties met een niet-geaccepteerd kind blijven buiten de graph)</span></h2>
				{#each relationKindCandidates as k (k.id)}
					<div class="panel item corr">
						<div class="corr-body">
							<p class="item-head">
								<strong>{k.kind}</strong>
								<span class="badge warn-b">kandidaat</span>
								<span class="meta">{k.occurrences} {k.occurrences === 1 ? 'voorstel' : 'voorstellen'} · gezien {fmtDate(k.firstSeen)}</span>
							</p>
							<!-- Bewijs (#123): voorbeeldvoorstellen die dit kind dragen,
							     klikbaar naar de brein-verkenner (patroon relatie-items). -->
							{#each k.examples as ex, i (i)}
								<p class="meta refs kind-example">
									bijv. <a href={graphHref(ex.fromRef)} title="Verken deze knoop in de brein-verkenner">{ex.fromName ?? ex.fromRef}</a>
									→ <a href={graphHref(ex.toRef)} title="Verken deze knoop in de brein-verkenner">{ex.toName ?? ex.toRef}</a>
								</p>
							{/each}
						</div>
						<div class="corr-actions">
							<form method="POST" action="?/acceptRelationKind" use:enhance>
								<input type="hidden" name="id" value={k.id} />
								<button class="small" title="Relaties met dit kind gaan mee bij de volgende graph-sync">Accepteer</button>
							</form>
							<form method="POST" action="?/rejectRelationKind" use:enhance>
								<input type="hidden" name="id" value={k.id} />
								<button class="ghost small" title="Komt niet opnieuw de queue in; nieuwe voorstellen met dit kind worden genegeerd">Verwerp</button>
							</form>
						</div>
					</div>
				{/each}
			{/if}
			{#if acceptedRelationKinds.length}
				<p class="meta">Kind-vocabulaire naast de seed-lijst: {acceptedRelationKinds.map((k) => k.kind).join(', ')}</p>
			{/if}

			{#each relations.items as r (r.id)}
				<div class="panel item corr">
					<div class="corr-body">
						<p class="item-head">
							<span class="badge {claimStatus(r.status).badge}">{claimStatus(r.status).label}</span>
							{#if r.archivedAt}<span class="badge mute">gearchiveerd</span>{/if}
							{#if r.recommendation}
								<span class="badge {recommendationInfo(r.recommendation).badge}" title="LLM-triage-aanbeveling — geen autoriteit, de mens beslist zelf">
									{recommendationInfo(r.recommendation).label}
								</span>
							{/if}
							<span class="badge ok-b">{r.kind}</span>
							<a href={graphHref(r.fromRef)} title="Verken deze knoop in de brein-verkenner"><strong>{r.fromName ?? r.fromRef}</strong></a>
							<span class="meta">→</span>
							<a href={graphHref(r.toRef)} title="Verken deze knoop in de brein-verkenner"><strong>{r.toName ?? r.toRef}</strong></a>
						</p>
						<p class="pre">{r.explanation}</p>
						<!-- Triage-motivering (#199 v1): naast het bestaande bewijs, vóór
						     de beheerder-notitie — machine-aanbeveling eerst, mens-oordeel
						     daaronder. -->
						{#if r.recommendationReason}
							<p class="meta refs">Triage-motivering: {r.recommendationReason}</p>
						{/if}
						<!-- Reden/notitie zichtbaar bij het item (#124): verwerpen is niet
						     langer zwijgend. -->
						{#if r.reviewNote}<p class="meta refs">Notitie beheerder: {r.reviewNote}</p>{/if}
						<p class="meta refs">bron: {r.provenance} · trust {r.trust.toFixed(2)} · {fmtDate(r.detectedAt)}{r.fromName ? ` · ${r.fromRef}` : ''}{r.toName ? ` → ${r.toRef}` : ''}</p>
					</div>
					<div class="corr-actions review">
						<form method="POST" action="?/acceptRelation" use:enhance class="review-form">
							<input type="hidden" name="id" value={r.id} />
							<textarea name="note" rows="2" placeholder="Notitie — zo zit het wél (optioneel)" aria-label="Beheerder-notitie" value={r.reviewNote ?? ''}></textarea>
							<div class="row">
								{#if r.status !== 'accepted'}
									<button class="small" formaction="?/acceptRelation" title="Mee in de graph bij de volgende sync (mits het kind geaccepteerd is); de notitie wordt bewaard">Bevestig</button>
								{/if}
								{#if r.status !== 'rejected'}
									<button class="ghost small" formaction="?/rejectRelation" title="Uit de projectie; wordt niet opnieuw voorgesteld — met notitie is de reden zichtbaar bij het item">Verwerp</button>
								{/if}
								<button class="ghost small" formaction="?/promoteRelationNote" title="Zet de notitie door als geverifieerde ruling — die stuurt voortaan de antwoorden">Notitie → ruling</button>
								{#if r.archivedAt}
									<button class="ghost small" formaction="?/unarchiveRelation" title="Terug in de gewone weergaven">Terug uit archief</button>
								{:else}
									<button class="ghost small" formaction="?/archiveRelation" title="Uit de default-weergave; terugvindbaar via de archief-chip">Archiveer</button>
								{/if}
							</div>
							{#if form?.error && formDocId === r.id}<p class="warn">{form.error}</p>{/if}
							{#if form && 'promoted' in form && form.promoted && formDocId === r.id}
								<p class="meta">Notitie doorgezet als geverifieerde ruling{form.embedded ? '' : ' — embedding volgt bij opnieuw doorzetten (Ollama niet bereikbaar)'}.</p>
							{/if}
						</form>
					</div>
				</div>
			{/each}
			{#if !relations.items.length}
				<p class="meta">
					{#if data.filter}Geen relatievoorstellen in deze weergave.
					{:else if relations.statusCounts.length === 0 && relations.archived === 0}Geen relatievoorstellen — draai "Relaties minen" in het beheer.
					{:else}Niets te reviewen — afgehandelde en gearchiveerde voorstellen zitten achter de chips.{/if}
				</p>
			{/if}
		{/if}

		<!-- Bronvoorstellen (#63): url + type-inschatting + motivatie + reviewacties -->
		{#if proposals}
			{#each proposals.items as p (p.id)}
				<div class="panel item corr">
					<div class="corr-body">
						<p class="item-head">
							<span class="badge {proposalStatus(p.status).badge}">{proposalStatus(p.status).label}</span>
							<strong>{p.name}</strong>
							<span class="meta">{p.type} · gevonden {fmtDate(p.foundAt)}{p.reviewedAt ? ` · beoordeeld ${fmtDate(p.reviewedAt)}` : ''}</span>
						</p>
						<p class="meta refs proposal-url"><a href={p.url}>{p.url}</a></p>
						{#if p.motivation}<p class="pre">{p.motivation}</p>{/if}
					</div>
					<!-- Geaccepteerd = de bron leeft nu in het register; beheer
					     hem daar. Verworpen mag heroverwogen worden. -->
					{#if p.status !== 'accepted'}
						<div class="corr-actions">
							<form method="POST" action="?/acceptProposal" use:enhance>
								<input type="hidden" name="id" value={p.id} />
								<button class="small" title="Zet de bron uitgeschakeld in het register met veilige defaults — aanzetten kan daarna via de bronnen-tabel">Accepteer</button>
							</form>
							{#if p.status === 'proposed'}
								<form method="POST" action="?/rejectProposal" use:enhance>
									<input type="hidden" name="id" value={p.id} />
									<button class="ghost small" title="Wordt niet opnieuw voorgesteld">Verwerp</button>
								</form>
							{/if}
						</div>
					{/if}
				</div>
			{/each}
			{#if !proposals.items.length}
				<p class="meta">
					{#if proposals.statusCounts.length === 0}Geen voorstellen — draai "Bronnen zoeken (web)" in het beheer.
					{:else}Geen voorstellen in deze weergave — de rest zit achter de chips.{/if}
				</p>
			{/if}
		{/if}

		<!-- Piltover Archive-decks (#15): wij spiegelen hun publieke
		     deck-pagina's — attributie prominent, elk deck deep-linkt terug. -->
		{#if decks}
			<p class="meta">
				Bron: <a href="https://piltoverarchive.com">Piltover Archive</a> — elk
				deck linkt terug naar zijn eigen pagina; "onbekend" telt kaartregels
				die (nog) niet aan onze kaarten koppelen.
			</p>
			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Deck</th><th>Domeinen</th><th>Kaarten</th><th>Views</th>
							<th>Likes</th><th>Bron</th><th>Opgehaald</th>
						</tr>
					</thead>
					<tbody>
						{#each decks.items as d (d.id)}
							<tr>
								<td>
									<strong>{d.name ?? '(naamloos)'}</strong>
									<br /><span class="meta">{d.paId}</span>
								</td>
								<td class="meta">{d.domains.join(', ') || '—'}</td>
								<td class="meta">{d.cards}{d.unknownCards ? ` (${d.unknownCards} onbekend)` : ''}</td>
								<td class="meta">{d.views}</td>
								<td class="meta">{d.likes}</td>
								<td class="meta nowrap"><a href={d.sourceUrl}>Piltover Archive</a></td>
								<td class="meta">{fmtDate(d.fetchedAt)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if !decks.items.length}
				<p class="meta">Nog geen decks — start "Decks binnenhalen" in het beheer.</p>
			{/if}
		{/if}

		<!-- Bron-feeds (#167): index-pagina's die periodiek op nieuwe
		     artikel-URL's worden afgespeurd — zelf toevoegen/bewerken. -->
		{#if data.kind === 'feeds'}
			<details class="panel add-feed">
				<summary>Nieuwe feed toevoegen</summary>
				<form method="POST" action="?/createFeed" use:enhance class="feed-form">
					<label>Id <input type="text" name="id" placeholder="riot-mijn-feed" required /></label>
					<label>Naam <input type="text" name="name" placeholder="Mijn nieuwe feed" required /></label>
					<label class="wide">URL <input type="url" name="url" placeholder="https://playriftbound.com/en-us/news/…/" required /></label>
					<label>Categoriefilter <input type="text" name="categoryFilter" placeholder="rules-and-releases (leeg = alles)" /></label>
					<label>Cadans
						<select name="cadence">
							<option value="daily">daily</option>
							<option value="weekly">weekly</option>
						</select>
					</label>
					<label class="checkbox"><input type="checkbox" name="autoApprove" value="true" /> AutoApprove (direct bron i.p.v. voorstel)</label>
					<button>Toevoegen</button>
				</form>
				<p class="meta">AutoApprove kan alleen aan op een officieel Riot-domein (playriftbound.com); op elk ander domein komen nieuwe artikelen altijd eerst in de reviewqueue (Bronvoorstellen).</p>
				{#if form?.error && formFeedId === null}<p class="warn">{form.error}</p>{/if}
			</details>

			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Feed</th><th>Categoriefilter</th><th>Aanpak</th><th>Cadans</th>
							<th>Bronnen</th><th>Laatst gecontroleerd</th><th>Actief</th><th></th>
						</tr>
					</thead>
					<tbody>
						{#each feeds as f (f.id)}
							<tr id="feed-{f.id}">
								<td><strong>{f.name}</strong><br /><a class="meta" href={f.url}>{f.id}</a></td>
								<td class="meta">{f.categoryFilter ?? 'alles'}</td>
								<td><span class="badge {f.autoApprove ? 'ok-b' : 'warn-b'}">{f.autoApprove ? 'AutoApprove' : 'reviewqueue'}</span></td>
								<td class="meta">{f.cadence}</td>
								<td class="meta">{f.sourceCount}</td>
								<td class="meta">{fmtDate(f.lastChecked)}</td>
								<td>
									<form method="POST" action="?/saveFeed" use:enhance>
										<input type="hidden" name="id" value={f.id} />
										<input type="hidden" name="enabled" value={String(!f.enabled)} />
										<button class="ghost small">{f.enabled ? 'Aan' : 'Uit'}</button>
									</form>
								</td>
								<td class="feed-actions">
									<button
										type="button"
										class="ghost small"
										onclick={() => (editingFeed = editingFeed === f.id ? null : f.id)}
									>{editingFeed === f.id ? 'Sluit' : 'Bewerk'}</button>
									<form method="POST" action="?/deleteFeed" use:enhance>
										<input type="hidden" name="id" value={f.id} />
										<button class="ghost small">Verwijder</button>
									</form>
									<!-- Toggle-/verwijder-fouten (saveFeed/deleteFeed dragen het
									     feed-id mee bij fail); de edit-save-fout hieronder toont
									     zichzelf al bij de bewerkrij, dus niet dubbel tonen. -->
									{#if form?.error && formFeedId === f.id && editingFeed !== f.id}
										<p class="warn">{form.error}</p>
									{/if}
								</td>
							</tr>
							{#if editingFeed === f.id}
								<tr class="edit-row">
									<td colspan="8">
										<form method="POST" action="?/saveFeed" use:enhance class="feed-form">
											<input type="hidden" name="id" value={f.id} />
											<label>Naam <input type="text" name="name" value={f.name} required /></label>
											<label class="wide">URL <input type="url" name="url" value={f.url} required /></label>
											<label>Categoriefilter <input type="text" name="categoryFilter" value={f.categoryFilter ?? ''} placeholder="leeg = alles" /></label>
											<label>Cadans
												<select name="cadence" value={f.cadence}>
													<option value="daily">daily</option>
													<option value="weekly">weekly</option>
												</select>
											</label>
											<label class="checkbox"><input type="checkbox" name="autoApprove" value="true" checked={f.autoApprove} /> AutoApprove</label>
											<button class="small">Opslaan</button>
										</form>
										{#if form?.error && formFeedId === f.id}<p class="warn">{form.error}</p>{/if}
									</td>
								</tr>
							{/if}
						{/each}
					</tbody>
				</table>
			</div>
			{#if !feeds.length}
				<p class="meta">Nog geen feeds — voeg er hierboven één toe, of draai "Bron-feeds afspeuren" nadat de seed-feeds er staan.</p>
			{/if}
		{/if}

		<!-- Set-dekking (#145): status = kleur + tekst; de ontbrekende nummers
		     compact als reeksen ("12, 45–47, 203"), de API levert de exacte lijst. -->
		{#if coverage}
			<p class="meta count">{coverage.sets} sets · {coverage.incomplete === 0 ? 'alles compleet' : `${coverage.incomplete} onvolledig`}</p>
			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Set</th><th>Status</th><th>Basistotaal</th><th>Aanwezig</th>
							<th>Dekking</th><th>Ontbrekende nummers</th><th>Varianten</th><th>Laatste sync</th>
						</tr>
					</thead>
					<tbody>
						{#each coverage.items as s (s.setId)}
							<tr>
								<td>
									<strong>{s.name}</strong><br />
									<span class="meta">{s.setId}{s.publishedOn ? ` · release ${fmtDate(s.publishedOn)}` : ''}</span>
								</td>
								<td>
									{#if s.baseTotal === null}
										<span class="badge mute">geen basistotaal</span>
									{:else if s.missingNumbers.length === 0}
										<span class="badge ok-b">compleet</span>
									{:else}
										<span class="badge warn-b">{s.missingNumbers.length} {s.missingNumbers.length === 1 ? 'nummer ontbreekt' : 'nummers ontbreken'}</span>
									{/if}
								</td>
								<td class="meta">{s.baseTotal ?? '—'}</td>
								<td class="meta">{s.present}</td>
								<td class="meta">{s.baseTotal ? `${Math.round((s.present / s.baseTotal) * 100)}%` : '—'}</td>
								<td class="missing">
									<span class="meta">{s.missingNumbers.length ? compactRanges(s.missingNumbers) : '—'}</span>
									{#if s.totalDeviations.length}
										<br /><span class="meta">afwijkend totaal in bron: {s.totalDeviations.map((d) => `${d.total} (${d.count} ${d.count === 1 ? 'kaart' : 'kaarten'})`).join(', ')}</span>
									{/if}
								</td>
								<td class="meta">{s.variants}</td>
								<td class="meta">{fmtDate(s.syncedAt)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if !coverage.items.length}
				<p class="meta">Nog geen kaarten in de database — draai eerst de kaarten-sync in het beheer.</p>
			{/if}
		{/if}

		<!-- Judge-benchmark (#158): score bovenaan, per vraag het antwoord naast
		     gekozen vs. juiste optie (goed/fout alleen bij een gekeyde vraag),
		     en run-historie om kwaliteit over tijd te vergelijken. Isolatie: deze
		     run voedde geen ask_trace/ask_metric/relaties (AskOptions.Benchmark,
		     zie rb-api/AskService). -->
		{#if benchmark}
			{#if !benchmark.selected}
				<p class="meta">Nog geen benchmarkrun — start "Judge-benchmark" in het beheer.</p>
			{:else}
				{@const run = benchmark.selected.run}
				<div class="panel bench-score">
					<span class="num">{run.scorePercent === null ? '—' : `${run.scorePercent}%`}</span>
					<span class="lbl">
						{run.correctCount} correct van {run.keyedCount} gekeyde vragen
						<span class="of">
							({run.questionCount} vragen totaal{run.questionCount > run.keyedCount
								? `, ${run.questionCount - run.keyedCount} nog niet gekeyd`
								: ''})
						</span>
					</span>
				</div>

				<div class="table-wrap">
					<table>
						<thead>
							<tr>
								<th>Vraag</th><th>Antwoord</th><th>Gekozen</th><th>Juist</th>
								<th>Resultaat</th><th>Duur</th>
							</tr>
						</thead>
						<tbody>
							{#each benchmark.selected.results as r (r.id)}
								<tr>
									<td class="bench-question">
										<strong>{r.question}</strong>
										<ul class="bench-options">
											{#each r.options as o, i (i)}
												<li
													class:bench-correct={r.correctIndex === i}
													class:bench-chosen={r.chosenIndex === i}
												>
													{optionLabel(i)}. {o}
												</li>
											{/each}
										</ul>
										{#if r.explanation}<p class="meta">{r.explanation}</p>{/if}
									</td>
									<td class="bench-answer"><AnswerView answer={r.answer} /></td>
									<td class="meta">{r.chosenIndex === null ? '—' : optionLabel(r.chosenIndex)}</td>
									<td class="meta">
										{r.correctIndex === null ? 'nog niet gekeyd' : optionLabel(r.correctIndex)}
									</td>
									<td>
										{#if r.correctIndex === null}
											<span class="badge mute">nog niet gekeyd</span>
										{:else if r.correct}
											<span class="badge ok-b">goed</span>
										{:else}
											<span class="badge err">fout</span>
										{/if}
									</td>
									<td class="meta nowrap">{(r.durationMs / 1000).toFixed(1)}s</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}

			{#if benchmark.runs.length > 1}
				<h2 class="gap-h">Run-historie <span class="meta">(vergelijk kwaliteit over tijd/versies)</span></h2>
				<div class="table-wrap">
					<table>
						<thead><tr><th>Gestart</th><th>Score</th><th>Gekeyed</th><th>Vragen</th></tr></thead>
						<tbody>
							{#each benchmark.runs as r (r.id)}
								<tr>
									<td>
										<a href="?run={r.id}" aria-current={benchmark.selected?.run.id === r.id ? 'page' : undefined}>
											{fmtDate(r.startedAt)}{r.label ? ` · ${r.label}` : ''}
										</a>
									</td>
									<td class="meta">{r.scorePercent === null ? '—' : `${r.scorePercent}%`}</td>
									<td class="meta">{r.keyedCount}</td>
									<td class="meta">{r.questionCount}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		{/if}

		<!-- Model-sweep (#174): elk model 2 runs naast elkaar — score, gemiddelde
		     tijd per vraag en consistentie (scoren de 2 runs gelijk?) —
		     gerangschikt op score of snelheid, plus de sweep-historie (verloop
		     van modelkwaliteit/-snelheid over tijd). Zelfde isolatie-garantie
		     als de single-run-benchmark hierboven (AskOptions.Benchmark). -->
		{#if benchmark && benchmark.sweeps.length > 0}
			<h2 class="gap-h">Model-sweep <span class="meta">(elk model 2×, score/tijd/consistentie)</span></h2>
			{#if !benchmark.selectedSweep}
				<p class="meta">Nog geen sweep-detail — kies een sweep hieronder.</p>
			{:else}
				<div class="sweep-toolbar">
					<span class="meta">Gestart: {fmtDate(benchmark.selectedSweep.startedAt)}</span>
					<div class="chips">
						<button type="button" class="chip" class:active={sweepSort === 'score'} onclick={() => (sweepSort = 'score')}>
							Op score
						</button>
						<button type="button" class="chip" class:active={sweepSort === 'speed'} onclick={() => (sweepSort = 'speed')}>
							Op snelheid
						</button>
					</div>
				</div>
				<div class="table-wrap">
					<table>
						<thead>
							<tr>
								<th>Model</th><th>Run 1 — score</th><th>Run 1 — gem. tijd</th>
								<th>Run 2 — score</th><th>Run 2 — gem. tijd</th><th>Consistent</th>
							</tr>
						</thead>
						<tbody>
							{#each sweepModelsSorted as m (m.model)}
								{@const r1 = m.runs.find((r) => r.runIndex === 1)}
								{@const r2 = m.runs.find((r) => r.runIndex === 2)}
								<tr>
									<td><strong>{m.model}</strong></td>
									<td class="meta">{r1 && r1.scorePercent !== null ? `${r1.scorePercent}%` : '—'}</td>
									<td class="meta nowrap">{r1 && r1.avgDurationMs > 0 ? `${(r1.avgDurationMs / 1000).toFixed(1)}s` : '—'}</td>
									<td class="meta">{r2 && r2.scorePercent !== null ? `${r2.scorePercent}%` : '—'}</td>
									<td class="meta nowrap">{r2 && r2.avgDurationMs > 0 ? `${(r2.avgDurationMs / 1000).toFixed(1)}s` : '—'}</td>
									<td>
										{#if m.consistent === null}
											<span class="badge mute">n.v.t.</span>
										{:else if m.consistent}
											<span class="badge ok-b">gelijk</span>
										{:else}
											<span class="badge warn-b">afwijkend</span>
										{/if}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}

			{#if benchmark.sweeps.length > 1}
				<h3 class="gap-h">Sweep-historie <span class="meta">(verloop van modelkwaliteit/-snelheid over tijd)</span></h3>
				<div class="table-wrap">
					<table>
						<thead><tr><th>Gestart</th><th>Modellen</th><th>Vragen</th></tr></thead>
						<tbody>
							{#each benchmark.sweeps as s (s.sweepId)}
								<tr>
									<td>
										<a href="?sweep={s.sweepId}" aria-current={benchmark.selectedSweep?.sweepId === s.sweepId ? 'page' : undefined}>
											{fmtDate(s.startedAt)}
										</a>
									</td>
									<td class="meta">{s.modelCount}</td>
									<td class="meta">{s.questionCount}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		{/if}

		<!-- Kennis-gaten-rapport (#52) -->
		{#if gaps}
			<h2 class="gap-h">Dekking</h2>
			<div class="gap-grid">
				<div class="panel gap-stat" class:thin={gaps.coverage.cardsWithoutEmbedding > 0}>
					<span class="num">{gaps.coverage.cardsWithoutEmbedding}</span>
					<span class="lbl">kaarten zonder embedding <span class="of">van {gaps.coverage.cards}</span></span>
					<a class="meta" href="/admin/overview/embeddings?filter=unembedded">bekijk</a>
				</div>
				<div class="panel gap-stat" class:thin={gaps.coverage.cardsWithoutMechanics > 0}>
					<span class="num">{gaps.coverage.cardsWithoutMechanics}</span>
					<span class="lbl">kaarten zonder mechanieken <span class="of">van {gaps.coverage.cards}</span></span>
					<a class="meta" href="/admin/overview/analyse?filter=unmined">bekijk</a>
				</div>
				<div class="panel gap-stat" class:thin={gaps.coverage.ruleChunksWithoutEmbedding > 0}>
					<span class="num">{gaps.coverage.ruleChunksWithoutEmbedding}</span>
					<span class="lbl">regelsecties zonder embedding <span class="of">van {gaps.coverage.ruleChunks}</span></span>
				</div>
				<div class="panel gap-stat" class:thin={gaps.coverage.primerTopicsMissing > 0 || gaps.coverage.primerDrafts > 0}>
					<span class="num">{gaps.coverage.primerTopicsMissing}</span>
					<span class="lbl">primer-concepten zonder doc <span class="of">van {gaps.coverage.primerTopics}</span>{gaps.coverage.primerDrafts ? ` · ${gaps.coverage.primerDrafts} draft` : ''}</span>
					<a class="meta" href="/admin/overview/primer">bekijk</a>
				</div>
				<div class="panel gap-stat" class:thin={gaps.coverage.openMechanicCandidates > 0}>
					<span class="num">{gaps.coverage.openMechanicCandidates}</span>
					<span class="lbl">open keyword-kandidaten</span>
					<a class="meta" href="/admin">reviewqueue</a>
				</div>
			</div>

			<h2 class="gap-h">Vraag-signalen <span class="meta">(laatste {gaps.coverage.tracesConsidered} traces + gemelde antwoorden — hier hoort de volgende harvest of primer-uitbreiding heen)</span></h2>
			{#if gapGroups.length === 0}
				<p class="meta">Geen signalen — geen lege retrieval, AI-uitval of negatieve feedback in het venster.</p>
			{/if}
			{#each gapGroups as g (g.key)}
				<div class="panel item">
					<p class="item-head">
						<span class="badge {g.key === 'ai-uitval' ? 'err' : 'warn-b'}">{g.label}</span>
						<span class="meta">{g.items.length} {g.items.length === 1 ? 'vraag' : 'vragen'} · {g.hint}</span>
					</p>
					<ul class="gap-list">
						{#each g.items as q, i (i)}
							<li>
								{q.question}
								<span class="meta">{q.questionType ? `${q.questionType} · ` : ''}{fmtDate(q.createdAt)}</span>
							</li>
						{/each}
					</ul>
				</div>
			{/each}

			<!-- Verouderingssignalen (#119): kennis die een verwerkte
			     regelwijziging heeft teruggelegd voor review -->
			<h2 class="gap-h">Verouderingssignalen <span class="meta">(kennis die een verwerkte regelwijziging hertoetste — primer-docs wachten in de <a class="meta-link" href="/admin">reviewqueue</a>, verouderde claims staan bij <a class="meta-link" href="/admin/overview/claims">community-claims</a>)</span></h2>
			{#if !gaps.aging.length}
				<p class="meta">Geen signalen — geen primer-docs of claims door een regelwijziging geraakt.</p>
			{:else}
				<div class="panel item">
					<ul class="gap-list">
						{#each gaps.aging as a, i (i)}
							<li>
								<span class="badge warn-b">{a.kind === 'primer' ? 'primer-draft' : 'claim verouderd'}</span>
								<strong>{a.title}</strong>
								<span class="meta">{a.reason} · {fmtDate(a.at)}</span>
							</li>
						{/each}
					</ul>
				</div>
			{/if}

			<!-- Bron-verwerking als signaal (#171, zelfde stijl als #119): bronnen
			     waarvan de laatste scan of een vervolgstap (classify/claims-mining)
			     mislukte, hangt, of die nog nooit gescand zijn. "Leeg" (scan ok,
			     niets opgeleverd) is bewust geen signaal — dat kan legitiem zijn. -->
			<h2 class="gap-h">Bron-verwerking <span class="meta">(bronnen die aandacht vragen — bekijk het dossier in de <a class="meta-link" href="/admin">bronnentabel</a> voor het ruwe document en de vervolgstappen)</span></h2>
			{#if !gaps.sourceProcessing.length}
				<p class="meta">Geen signalen — elke ingeschakelde bron is gescand en verwerkt (of legitiem leeg).</p>
			{:else}
				<div class="panel item">
					<ul class="gap-list">
						{#each gaps.sourceProcessing as p (p.sourceId)}
							<li>
								<span class="badge {p.status === 'nooit-gescand' ? 'warn-b' : 'err'}">
									{p.status === 'nooit-gescand' ? 'nooit gescand' : 'onvolledig'}
								</span>
								<strong>{p.name}</strong>
								<span class="meta">{p.reason}{#if p.at} · {fmtDate(p.at)}{/if}</span>
								<a class="meta-link" href="/admin#bron-{p.sourceId}">bekijk dossier</a>
							</li>
						{/each}
					</ul>
				</div>
			{/if}

			<!-- Set-dekking als signaal (#145, zelfde stijl als #119): één regel
			     per onvolledige set; de exacte nummers staan op het overzicht. -->
			<h2 class="gap-h">Set-dekking <span class="meta">(basisnummers per set uit de riftbound-id's — de exacte ontbrekende nummers staan op <a class="meta-link" href="/admin/overview/setdekking">het set-dekking-overzicht</a>)</span></h2>
			{#if !gaps.setCoverage.length}
				<p class="meta">Geen signalen — elke set met een basistotaal is compleet.</p>
			{:else}
				<div class="panel item">
					<ul class="gap-list">
						{#each gaps.setCoverage as s (s.setId)}
							<li>
								<span class="badge warn-b">onvolledig</span>
								<strong>{s.name}</strong>
								<span class="meta">set {s.setId} mist {s.missing} van {s.baseTotal} nummers</span>
								<a class="meta-link" href="/admin/overview/setdekking">bekijk</a>
							</li>
						{/each}
					</ul>
				</div>
			{/if}

			<h2 class="gap-h">Bron-versheid <span class="meta">(wanneer leverde elke bron voor het laatst iets nieuws)</span></h2>
			<div class="table-wrap">
				<table>
					<thead><tr><th>Bron</th><th>Trust</th><th>Documenten</th><th>Secties</th><th>Laatst gecontroleerd</th><th>Laatste wijziging</th></tr></thead>
					<tbody>
						{#each gaps.sources as s (s.id)}
							{@const changeDays = daysAgo(s.lastChangeAt)}
							<tr>
								<td><strong>{s.name}</strong><br /><span class="meta">{s.id}</span></td>
								<td class="meta">{s.trustTier}</td>
								<td class="meta">{s.documents}</td>
								<td class="meta">{s.chunks}</td>
								<td class="meta">{fmtDate(s.lastChecked)}</td>
								<td>
									{#if changeDays === null}
										<span class="badge warn-b">nooit</span>
									{:else if changeDays > 30}
										<span class="badge warn-b">{changeDays} dagen stil</span>
									{:else}
										<span class="meta">{fmtDate(s.lastChangeAt)}</span>
									{/if}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>

			<!-- Graph-drift (#108, docs/BRAIN.md §4): loopt de Neo4j-projectie
			     achter op Postgres? Gemeten per knooptype, niet geraden. -->
			<h2 class="gap-h">Graph-drift <span class="meta">(aantallen per knooptype — Postgres is de bron, Neo4j de projectie; negatieve delta = achterlopende graph, de graph-job haalt hem bij)</span></h2>
			{#if !gaps.drift.graphAvailable}
				<p class="panel item warn">graph niet beschikbaar{gaps.drift.detail ? ` — ${gaps.drift.detail}` : ''} · drift is nu niet te meten; de rest van dit rapport werkt gewoon</p>
			{:else}
				<div class="table-wrap">
					<table>
						<thead><tr><th>Knooptype</th><th>Postgres</th><th>Neo4j</th><th>Drift</th></tr></thead>
						<tbody>
							{#each gaps.drift.entries as e (e.label)}
								<tr>
									<td><strong>{e.label}</strong></td>
									<td class="meta">{e.postgres}</td>
									<td class="meta">{e.graph}</td>
									<td>
										{#if e.delta === 0}
											<span class="badge ok-b">in sync</span>
										{:else}
											<span class="badge warn-b">{e.delta > 0 ? `+${e.delta}` : e.delta}</span>
										{/if}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		{/if}

		<!-- Gebruikers + kosteninzicht (#42): gebruik per account in de gekozen
		     periode; quota en blokkade direct in de rij bij te stellen. -->
		{#if users}
			<p class="meta count">
				Anoniem gebruik in deze periode: {users.anonQuestions}
				{users.anonQuestions === 1 ? 'vraag' : 'vragen'}{users.anonPhotos
					? `, waarvan ${users.anonPhotos} met foto`
					: ''} — anonieme bezoekers vallen onder de per-IP-limiet.
			</p>

			<!-- Tokentotalen per antwoordpad (#121): echte tellingen uit rb-ai,
			     over álle vragen in de periode (incl. anoniem). Vragen zonder
			     gemeten usage tellen als 0 tokens — de som is een ondergrens. -->
			{#if users.paths.length}
				<div class="table-wrap">
					<table class="paths">
						<thead><tr><th>Pad</th><th>Vragen</th><th>Tokens in</th><th>Tokens uit</th></tr></thead>
						<tbody>
							{#each users.paths as p (p.path)}
								<tr>
									<td><strong>{p.path}</strong></td>
									<td class="meta">{p.questions}</td>
									<td class="meta">{fmtTokens(p.inputTokens)}</td>
									<td class="meta">{fmtTokens(p.outputTokens)}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
				<p class="meta count">Tokens per pad over de gekozen periode, inclusief anonieme vragen; vragen van vóór de tokenmeting tellen als 0 tokens.</p>
			{/if}

			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Gebruiker</th><th>Status</th><th>Vragen</th><th>Foto's</th>
							<th>Cheap / hard</th><th>Mislukt</th><th>Gem. duur</th>
							<th>Tokens in / uit</th>
							<th>Quota per dag</th><th></th>
						</tr>
					</thead>
					<tbody>
						{#each users.items as u (u.id)}
							<tr>
								<td>
									<strong>{u.email}</strong><br />
									<span class="meta">sinds {fmtDate(u.createdAt)} · laatst ingelogd {fmtDate(u.lastLoginAt)}</span>
								</td>
								<td><span class="badge {u.blocked ? 'err' : 'ok-b'}">{u.blocked ? 'geblokkeerd' : 'actief'}</span></td>
								<td class="meta">{u.questions}</td>
								<td class="meta">{u.photos}</td>
								<td class="meta">{u.cheap} / {u.hard}</td>
								<td class="meta">{u.failed}</td>
								<td class="meta">{u.questions ? `${Math.round(u.avgDurationMs / 1000)}s` : '—'}</td>
								<td class="meta nowrap">{u.questions ? `${fmtTokens(u.inputTokens)} / ${fmtTokens(u.outputTokens)}` : '—'}</td>
								<td>
									<form method="POST" action="?/saveUser" use:enhance class="quota-form">
										<input type="hidden" name="id" value={u.id} />
										<label>vragen <input type="number" name="dailyQuota" value={u.dailyQuota} min="0" max="10000" /></label>
										<label>foto's <input type="number" name="dailyPhotoQuota" value={u.dailyPhotoQuota} min="0" max="10000" /></label>
										<!-- #153: zelf geforceerde Grondig-vragen per dag -->
										<label>grondig <input type="number" name="dailyAgenticQuota" value={u.dailyAgenticQuota} min="0" max="10000" /></label>
										<button class="small">Opslaan</button>
									</form>
									{#if form?.error && formDocId === u.id}<p class="warn">{form.error}</p>{/if}
								</td>
								<td>
									<form method="POST" action="?/saveUser" use:enhance>
										<input type="hidden" name="id" value={u.id} />
										<input type="hidden" name="blocked" value={u.blocked ? 'false' : 'true'} />
										<button class="ghost small" title={u.blocked ? 'Account mag weer vragen stellen' : 'Account per direct geen vragen meer laten stellen'}>{u.blocked ? 'Deblokkeer' : 'Blokkeer'}</button>
									</form>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if !users.items.length}<p class="meta">Nog geen accounts — het eerste account ontstaat bij de eerste voltooide magic-link-login.</p>{/if}
		{/if}

		<!-- Paginering -->
		{#if paged && totalPages > 1}
			<nav class="pager">
				{#if paged.page > 1}<a href={href({ page: paged.page - 1 })}>Vorige</a>{/if}
				<span class="meta">pagina {paged.page} van {totalPages}</span>
				{#if paged.page < totalPages}<a href={href({ page: paged.page + 1 })}>Volgende</a>{/if}
			</nav>
		{/if}
	{/if}
</main>

<style>
	main { max-width: 1080px; margin: 0 auto; padding: 24px 20px; }
	.crumb { color: var(--muted); font-size: 0.85rem; margin-bottom: 6px; }
	.crumb a { color: var(--accent); text-decoration: none; }
	h1 { margin: 0 0 2px; }
	.sub { color: var(--muted); margin: 0 0 18px; }
	.count { margin: 0 0 10px; }
	.filters { display: flex; gap: 8px; margin-bottom: 14px; }
	.filters input {
		flex: 1; max-width: 340px; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; font-size: 16px;
	}
	button {
		background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 8px 14px; font-weight: 600; cursor: pointer;
	}
	button.ghost { background: transparent; color: var(--muted); border: 1px solid var(--border); }
	button.small { padding: 4px 10px; font-size: 0.82rem; }
	.chips { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 14px; }
	.chip {
		border: 1px solid var(--border); border-radius: 999px; padding: 4px 12px;
		color: var(--muted); text-decoration: none; font-size: 0.85rem;
	}
	.chip.active { border-color: var(--accent); color: var(--accent); }
	/* Model-sweep-sorteerknoppen (#174): zelfde chip-vorm als de filter-chips
	   elders, maar op een <button> (client-side sortering, geen URL-param) —
	   de generieke button-regel hierboven zet anders een accent-achtergrond. */
	button.chip { background: transparent; font-weight: 400; }
	.sweep-toolbar { display: flex; align-items: center; gap: 14px; margin-bottom: 10px; flex-wrap: wrap; }
	.table-wrap { overflow-x: auto; }
	table { width: 100%; border-collapse: collapse; margin-bottom: 20px; }
	/* Pad-totalen (#121): compacte samenvatting boven de accounttabel. */
	table.paths { max-width: 480px; margin-bottom: 6px; font-variant-numeric: tabular-nums; }
	th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
	th { color: var(--muted); font-size: 0.82rem; font-weight: 600; }
	td a { color: inherit; }
	.nowrap { white-space: nowrap; }
	.snippet { max-width: 560px; }
	.mech { max-width: 260px; }
	/* Ontbrekende-nummers-cel (#145): compacte reeksen mogen wrappen — de
	   tabel scrolt in .table-wrap, de cel zelf blijft leesbaar begrensd. */
	.missing { max-width: 420px; min-width: 200px; overflow-wrap: anywhere; }
	.item { padding: 12px 14px; margin-bottom: 8px; }
	.item-head { margin: 0 0 6px; display: flex; align-items: baseline; gap: 8px; flex-wrap: wrap; }
	.item-head a { color: inherit; }
	.pre { margin: 0; white-space: pre-wrap; line-height: 1.55; }
	/* Supersede-kandidaat (#168): signaal, geen alarm — kleur + tekst. */
	.superseded-note { margin: 0 0 8px; color: var(--warn); font-size: 0.85rem; }
	/* flex-wrap: op 390px zakt de actiekolom (met notitieveld, #124) onder de
	   tekst — nooit horizontale overflow. */
	.corr { display: flex; gap: 14px; flex-wrap: wrap; }
	.corr-body { flex: 1 1 320px; min-width: 0; }
	.corr-actions { display: flex; flex-direction: column; gap: 6px; }
	/* Review-formulier per item (#124): notitie + beslisknoppen. De breedte
	   stuurt de actie-kólom (rij-context); het formulier zelf groeit met de
	   inhoud — geen flex-basis in de kolomrichting (dat werd hoogte). */
	.corr-actions.review { flex: 1 1 240px; max-width: 340px; }
	.review-form { display: flex; flex-direction: column; gap: 6px; }
	.review-form textarea {
		width: 100%; box-sizing: border-box; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;
		font-size: 16px; /* iOS zoomt in op form-controls kleiner dan 16px */
		font-family: inherit; line-height: 1.45;
	}
	.review-form .warn, .review-form .meta { margin: 0; }
	/* Neutrale badge (archief): geen oordeel, alleen zicht-status. */
	.badge.mute { background: var(--surface-deep); color: var(--muted); border: 1px solid var(--border); }
	.archive-all { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin: 0 0 14px; }
	.refs { margin: 6px 0 0; }
	/* Lange bron-URL's mogen op 390px nooit horizontale overflow geven. */
	.proposal-url { overflow-wrap: anywhere; }
	.proposal-url a { color: var(--accent); }
	/* Voorbeeldvoorstellen bij kandidaat-kinds (#123): lange refs breken. */
	.kind-example { overflow-wrap: anywhere; }
	.kind-example a { color: var(--accent); }
	.row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
	.actions { margin-top: 10px; }
	.edit { display: flex; flex-direction: column; gap: 10px; }
	.edit label { display: flex; flex-direction: column; gap: 4px; color: var(--muted); font-size: 0.85rem; }
	.edit input, .edit textarea {
		width: 100%; box-sizing: border-box; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;
		font-size: 16px; /* iOS zoomt in op form-controls kleiner dan 16px */
		font-family: inherit; line-height: 1.55;
	}
	/* Quota-bewerking in de gebruikersrij (#42). */
	.quota-form { display: flex; flex-wrap: wrap; gap: 6px; align-items: flex-end; }
	.quota-form label { display: flex; flex-direction: column; gap: 2px; color: var(--muted); font-size: 0.75rem; }
	.quota-form input {
		width: 76px; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 6px 8px;
		font-size: 16px; /* iOS zoomt in op form-controls kleiner dan 16px */
	}
	/* Bron-feeds (#167): toevoeg-/bewerkformulier, breekt netjes op 390px. */
	.add-feed { padding: 12px 14px; margin-bottom: 14px; }
	.add-feed summary { cursor: pointer; color: var(--accent); }
	.add-feed form { margin-top: 10px; }
	.feed-form { display: flex; flex-wrap: wrap; gap: 8px; align-items: flex-end; }
	.feed-form label {
		display: flex; flex-direction: column; gap: 2px; color: var(--muted); font-size: 0.75rem;
	}
	.feed-form label.wide { flex: 1 1 260px; }
	.feed-form label.checkbox {
		flex-direction: row; align-items: center; gap: 6px; font-size: 0.85rem;
	}
	.feed-form input[type='text'],
	.feed-form input[type='url'],
	.feed-form select {
		background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 6px 8px;
		font-size: 16px; /* iOS zoomt in op form-controls kleiner dan 16px */
	}
	.edit-row td { padding-top: 0; }
	.feed-actions { display: flex; flex-direction: column; gap: 6px; }
	.gap-h { color: var(--accent); font-size: 1.02rem; margin: 22px 0 10px; }
	.gap-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: 10px; }
	.gap-stat { display: flex; flex-direction: column; gap: 2px; padding: 12px 14px; }
	.gap-stat .num { font-size: 1.35rem; font-weight: 700; font-variant-numeric: tabular-nums; color: var(--ok); }
	.gap-stat.thin .num { color: var(--warn); }
	.gap-stat .lbl { color: var(--muted); font-size: 0.82rem; }
	.gap-stat .of { opacity: 0.75; }
	.gap-stat a { color: var(--accent); text-decoration: none; }
	.gap-list { margin: 4px 0 0; padding-left: 18px; }
	.gap-list li { margin-bottom: 4px; line-height: 1.5; }
	.meta-link { color: var(--accent); text-decoration: none; }
	.meta-link:hover { text-decoration: underline; }
	.pager { display: flex; align-items: center; gap: 14px; margin: 16px 0; }
	.pager a { color: var(--accent); text-decoration: none; font-weight: 600; }
	.meta { color: var(--muted); font-size: 0.85rem; }
	/* .badge-stijlen: gedeelde bouwsteen in app.css (#59). */
	.warn { color: var(--err); }

	/* Judge-benchmark (#158): score-tegel + per-vraag opties, zelfde
	   bouwstenen als .gap-stat hierboven. */
	.bench-score { display: flex; flex-direction: column; gap: 2px; padding: 14px 16px; margin-bottom: 16px; }
	.bench-score .num { font-size: 1.8rem; font-weight: 700; font-variant-numeric: tabular-nums; }
	.bench-score .lbl { color: var(--muted); font-size: 0.9rem; }
	.bench-score .of { opacity: 0.75; }
	.bench-question { min-width: 220px; max-width: 340px; }
	.bench-options { margin: 6px 0 0; padding-left: 18px; }
	.bench-options li { margin-bottom: 2px; }
	.bench-options .bench-chosen { font-weight: 600; }
	.bench-options .bench-correct { color: var(--ok); }
	/* Het gerenderde antwoord hoort op 390px nooit horizontaal te overflowen —
	   AnswerView zelf breekt tekst af; hier alleen de kolombreedte begrenzen. */
	.bench-answer { min-width: 260px; max-width: 480px; overflow-wrap: anywhere; }
</style>
