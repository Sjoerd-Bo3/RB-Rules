<script lang="ts">
	let { data } = $props();

	interface Counts {
		assertions: number;
		canonicalEntities: number;
		canonicalEntitiesCandidate: number;
		canonicalEntitiesMerged: number;
		interactions: number;
		interactionsPromoted: number;
		conflicts: number;
		conflictsOpen: number;
		miningRuns: number;
		evalBaselines: number;
		answerTraces: number;
	}
	interface MiningPrecisionRow {
		kind: string;
		model: string;
		runs: number;
		candidates: number;
		verified: number;
		rejected: number;
		precision: number;
		acceptRate: number;
	}
	interface KindDrift {
		kind: string;
		live: number;
		candidates: number;
		canonical: number;
		tombstones: number;
		singletons: number;
	}
	interface CanonicalDrift {
		byKind: KindDrift[];
		duplicationDebt: number;
		totalLive: number;
		totalTombstones: number;
		totalSingletons: number;
	}
	interface TierCount {
		key: string;
		count: number;
	}
	interface Observability {
		report: {
			takenAt: string;
			graphDrift: { label: string; postgres: number; graph: number; delta: number }[];
			canonicalDrift: CanonicalDrift | null;
			miningPrecision: MiningPrecisionRow[];
			communityHealth: unknown | null;
		};
		interactionTiers: TierCount[];
		conflictChannels: TierCount[];
	}

	const counts = $derived(data.counts as Counts | null);
	const obs = $derived(data.observability as Observability | null);

	const TILE_COLORS = ['#f5c518', '#2ea36a', '#5b8def', '#e5766a', '#9b7bd4', '#3bc9c9'];
	const tiles = $derived(
		counts
			? [
					{ label: 'Assertions', href: null, n: counts.assertions, sub: 'provenance-envelop' },
					{
						label: 'Canonieke entiteiten',
						href: '/admin/brein/entities',
						n: counts.canonicalEntities,
						sub: `${counts.canonicalEntitiesCandidate} kandidaat · ${counts.canonicalEntitiesMerged} merged`
					},
					{
						label: 'Interacties',
						href: '/admin/brein/interactions',
						n: counts.interactions,
						sub: `${counts.interactionsPromoted} gepromoveerd`
					},
					{
						label: 'Conflicts',
						href: '/admin/brein/conflicts',
						n: counts.conflicts,
						sub: `${counts.conflictsOpen} open`
					},
					{ label: 'Mining-runs', href: null, n: counts.miningRuns, sub: 'PROV-O-activiteiten' },
					{ label: 'Eval-baselines', href: null, n: counts.evalBaselines, sub: 'per klasse × metriek' },
					{
						label: 'AnswerTraces',
						href: '/admin/brein/answertrace',
						n: counts.answerTraces,
						sub: 'herspeelbare antwoorden'
					}
				]
			: []
	);

	const pct = (v: number) => `${(v * 100).toFixed(0)}%`;
	const totalBrein = $derived(
		counts
			? counts.assertions +
					counts.canonicalEntities +
					counts.interactions +
					counts.conflicts +
					counts.answerTraces
			: 0
	);
</script>

{#if data.apiDown}
	<p class="apidown">Het brein is niet bereikbaar — is rb-api op? Probeer het later opnieuw.</p>
{:else if !counts || totalBrein === 0}
	<div class="empty">
		Nog geen brein-data — draai de brein-jobs (mining, reasoner, interacties) in het beheer. Zodra
		er feiten, entiteiten of interacties zijn, verschijnen ze hier.
	</div>
{:else}
	<div class="tiles">
		{#each tiles as t, i (t.label)}
			{#if t.href}
				<a class="tile" href={t.href}>
					<span class="tb" style="background: {TILE_COLORS[i % TILE_COLORS.length]}"></span>
					<span class="tn tnum">{t.n.toLocaleString('nl-NL')}</span>
					<span class="tl">{t.label}</span>
					<span class="ts">{t.sub}</span>
				</a>
			{:else}
				<div class="tile static">
					<span class="tb" style="background: {TILE_COLORS[i % TILE_COLORS.length]}"></span>
					<span class="tn tnum">{t.n.toLocaleString('nl-NL')}</span>
					<span class="tl">{t.label}</span>
					<span class="ts">{t.sub}</span>
				</div>
			{/if}
		{/each}
	</div>

	{#if obs}
		<h2>Observability</h2>
		<p class="muted small">
			Deterministische Postgres-rollups (fase 7). De graaf-drift en community-stabiliteit vergen
			een gedraaide graph-job — die blijven leeg tot de reasoner/GDS-jobs lopen.
		</p>

		<h3>Mining-precisie</h3>
		{#if obs.report.miningPrecision.length}
			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Soort</th>
							<th>Model</th>
							<th class="num">Runs</th>
							<th class="num">Kandidaten</th>
							<th class="num">Geverifieerd</th>
							<th class="num">Verworpen</th>
							<th class="num">Precisie</th>
							<th class="num">Accept-rate</th>
						</tr>
					</thead>
					<tbody>
						{#each obs.report.miningPrecision as r (r.kind + r.model)}
							<tr>
								<td>{r.kind}</td>
								<td class="muted">{r.model}</td>
								<td class="num tnum">{r.runs}</td>
								<td class="num tnum">{r.candidates.toLocaleString('nl-NL')}</td>
								<td class="num tnum">{r.verified}</td>
								<td class="num tnum">{r.rejected}</td>
								<td class="num tnum">{pct(r.precision)}</td>
								<td class="num tnum">{pct(r.acceptRate)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{:else}
			<p class="empty">Nog geen afgeronde mining-runs.</p>
		{/if}

		<h3>Canonieke drift &amp; duplicatie-schuld</h3>
		{#if obs.report.canonicalDrift && obs.report.canonicalDrift.byKind.length}
			<p class="muted small">
				Open merge-kandidaten (duplicatie-schuld):
				<strong class="tnum">{obs.report.canonicalDrift.duplicationDebt}</strong>
			</p>
			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Kind</th>
							<th class="num">Levend</th>
							<th class="num">Kandidaat</th>
							<th class="num">Canoniek</th>
							<th class="num">Tombstones</th>
							<th class="num">Singletons</th>
						</tr>
					</thead>
					<tbody>
						{#each obs.report.canonicalDrift.byKind as k (k.kind)}
							<tr>
								<td>{k.kind}</td>
								<td class="num tnum">{k.live}</td>
								<td class="num tnum">{k.candidates}</td>
								<td class="num tnum">{k.canonical}</td>
								<td class="num tnum">{k.tombstones}</td>
								<td class="num tnum">{k.singletons}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{:else}
			<p class="empty">Nog geen canonieke entiteiten.</p>
		{/if}

		<div class="two">
			<div>
				<h3>Interactie-tiers</h3>
				{#if obs.interactionTiers.length}
					<ul class="dist">
						{#each obs.interactionTiers as t (t.key)}
							<li><span class="k">{t.key}</span><span class="v tnum">{t.count}</span></li>
						{/each}
					</ul>
				{:else}
					<p class="empty">Geen interacties.</p>
				{/if}
			</div>
			<div>
				<h3>Conflict-kanalen</h3>
				{#if obs.conflictChannels.length}
					<ul class="dist">
						{#each obs.conflictChannels as t (t.key)}
							<li><span class="k">{t.key}</span><span class="v tnum">{t.count}</span></li>
						{/each}
					</ul>
				{:else}
					<p class="empty">Geen conflicts.</p>
				{/if}
			</div>
		</div>

		<h3>Graaf-drift</h3>
		{#if obs.report.graphDrift.length}
			<div class="table-wrap">
				<table>
					<thead>
						<tr><th>Label</th><th class="num">Postgres</th><th class="num">Graaf</th><th class="num">Delta</th></tr>
					</thead>
					<tbody>
						{#each obs.report.graphDrift as d (d.label)}
							<tr>
								<td>{d.label}</td>
								<td class="num tnum">{d.postgres}</td>
								<td class="num tnum">{d.graph}</td>
								<td class="num tnum">{d.delta}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{:else}
			<p class="empty">
				Nog geen graaf-drift-meting — die vergt een gedraaide graph-sync (Neo4j-projectie).
			</p>
		{/if}
	{/if}
{/if}

<style>
	.tiles {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
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
	a.tile:hover {
		border-color: var(--border-strong);
	}
	.tile.static {
		opacity: 0.92;
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
		font-size: 0.74rem;
		font-weight: 600;
	}
	.tile .ts {
		font-size: 0.68rem;
		color: var(--muted);
	}
	h3 {
		font-size: 0.82rem;
		text-transform: uppercase;
		letter-spacing: 0.05em;
		color: var(--muted);
		margin: 20px 0 8px;
	}
	.small {
		font-size: 0.8rem;
	}
	.two {
		display: grid;
		grid-template-columns: 1fr 1fr;
		gap: 22px;
	}
	@media (max-width: 640px) {
		.two {
			grid-template-columns: 1fr;
		}
	}
	ul.dist {
		list-style: none;
		margin: 0;
		padding: 0;
		border: 1px solid var(--border);
		border-radius: var(--radius-lg);
		overflow: hidden;
	}
	ul.dist li {
		display: flex;
		justify-content: space-between;
		padding: 8px 12px;
		font-size: 0.85rem;
		border-bottom: 1px solid var(--border);
	}
	ul.dist li:last-child {
		border-bottom: 0;
	}
	ul.dist .k {
		color: var(--text);
	}
	ul.dist .v {
		color: var(--muted);
		font-weight: 650;
	}
</style>
