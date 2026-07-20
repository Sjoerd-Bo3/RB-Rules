<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';

	let { data, form } = $props();

	// ── Cockpit (brein-jobs-ui) ─────────────────────────────────────────────
	// De operationele pipeline-status + trigger-knoppen. De laatste-run per job
	// komt uit het run_log-grootboek (overleeft herstart); de flag uit de env.
	interface JobRun {
		name: string;
		status: string;
		detail: string | null;
		at: string;
	}
	interface Cockpit {
		interactions: number;
		mechanicPredicates: number;
		mineInteractionsRun: JobRun | null;
		minePredicatesRun: JobRun | null;
		registerEntitiesRun: JobRun | null;
		canonicalEntities: number;
		projectionRun: JobRun | null;
		conflicts: number;
		conflictsOpen: number;
		reasonRun: JobRun | null;
		retrievalEnabled: boolean;
		nightlyRun: JobRun | null;
	}
	const cockpit = $derived(data.cockpit as Cockpit | null);

	// Live running-job (voor knop-disabled + "Bezig"): dezelfde status-feed als
	// /admin, cookie-beveiligd via het /admin/status-proxy. Eén job tegelijk.
	interface RunningState {
		running: { name: string } | null;
	}
	let live = $state<RunningState | null>(null);
	const running = $derived(live?.running ?? null);
	$effect(() => {
		let stop = false;
		const tick = async () => {
			try {
				const r = await fetch('/admin/status');
				if (r.ok) live = await r.json();
			} catch {
				/* rb-api even weg — volgende poll */
			}
			if (!stop) setTimeout(tick, live?.running ? 2000 : 6000);
		};
		tick();
		return () => {
			stop = true;
		};
	});

	function fmtAgo(iso: string | null): string {
		if (!iso) return '';
		const s = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
		return s < 60
			? `${s}s`
			: s < 3600
				? `${Math.round(s / 60)}m`
				: s < 48 * 3600
					? `${Math.round(s / 3600)}u`
					: `${Math.round(s / 86400)}d`;
	}
	// Laatste-run-regel per brein-job (grootboek): tijd + mislukt-markering.
	function runMeta(run: JobRun | null | undefined): string {
		if (!run) return 'nog niet gedraaid';
		const tail = run.status === 'error' ? ' — mislukt' : run.status === 'ok' ? '' : ` — ${run.status}`;
		return `laatste run ${fmtAgo(run.at)} geleden${tail}`;
	}

	// De extractie-jobs (stap 1), als config zodat de job-rijen niet dupliceren.
	// n=0 → "nog niet gedraaid — leeg" als teller-tekst. De entiteiten-registratie
	// staat vooraan: zij vult de canonieke laag waartegen de mining resolveert —
	// zonder die rijen vindt de predicaat-mining nul subjects (#250).
	const step1jobs = $derived(
		cockpit
			? [
					{
						name: 'breinentiteiten',
						label: 'Canonieke entiteiten registreren',
						n: cockpit.canonicalEntities,
						unit: 'entiteiten',
						run: cockpit.registerEntitiesRun
					},
					{
						name: 'breinmine-interacties',
						label: 'Interacties minen',
						n: cockpit.interactions,
						unit: 'interacties',
						run: cockpit.mineInteractionsRun
					},
					{
						name: 'breinmine-predicaten',
						label: 'Mechanic-predicaten minen',
						n: cockpit.mechanicPredicates,
						unit: 'predicaten',
						run: cockpit.minePredicatesRun
					}
				]
			: []
	);
	const step1empty = $derived(
		!!cockpit &&
			cockpit.interactions === 0 &&
			cockpit.mechanicPredicates === 0 &&
			cockpit.canonicalEntities === 0
	);

	// Pill-toon (kleur + tekst, geen emoji) per stap uit de laatste-run.
	function stepPill(run: JobRun | null | undefined, okText: string): { tone: string; text: string } {
		if (!run) return { tone: '', text: 'nog niet gedraaid' };
		if (run.status === 'error') return { tone: 'err', text: 'mislukt' };
		if (run.status === 'ok') return { tone: 'ok', text: okText };
		return { tone: 'warn', text: run.status };
	}
	const projectionPill = $derived(stepPill(cockpit?.projectionRun, 'geprojecteerd'));
	const reasonPill = $derived(stepPill(cockpit?.reasonRun, 'gedraaid'));

	const startedLabel: Record<string, string> = {
		breinentiteiten: 'Canonieke entiteiten registreren',
		'breinmine-interacties': 'Interacties minen',
		'breinmine-predicaten': 'Mechanic-predicaten minen',
		breinprojectie: 'Projectie naar Neo4j',
		reason: 'Reasoner',
		nachtrun: 'Volledige nachtrun'
	};

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

{#if !data.apiDown && cockpit}
	<section class="cockpit" aria-label="Brein-pipeline">
		<div class="ckhead">
			<h2>Pipeline</h2>
			<span class="orderhint">draai in volgorde 1 &rarr; 2 &rarr; 3</span>
		</div>

		{#if form?.started}
			<p class="notice ok">Job &ldquo;{startedLabel[form.started] ?? form.started}&rdquo; gestart — de
				voortgang staat in het beheer-overzicht.</p>
		{:else if form?.error}
			<p class="notice err">{form.error}</p>
		{/if}

		<div class="pipeline">
			<!-- Stap 1 — Extractie -->
			<div class="step">
				<div class="step-rail"><span class="step-n">1</span></div>
				<div class="step-body">
					<div class="step-head">
						<h3 class="step-title">Extractie</h3>
						<span class="tier {step1empty ? '' : 'ok'}">{step1empty ? 'nog niet gedraaid — leeg' : 'gevuld'}</span>
					</div>
					<p class="step-desc">Eerst de canonieke entiteiten registreren (deterministisch, uit
						het mechanic-vocabulaire en de regeltekst), daarna tool-forced, ontologie-begrensde
						mining via rb-ai: gereïficeerde interacties en mechanic-predicaten.</p>
					<div class="jobrows">
						{#each step1jobs as j (j.name)}
							<div class="jobrow">
								<div class="jr-info">
									<strong>{j.label}</strong>
									<span class="jr-stat">
										{#if j.n === 0}
											<span class="muted">nog niet gedraaid — leeg</span>
										{:else}
											<span class="tnum">{j.n.toLocaleString('nl-NL')}</span> {j.unit}
										{/if}
									</span>
									<span class="run-meta">{runMeta(j.run)}</span>
								</div>
								<form
									method="POST"
									action="?/job"
									use:enhance={() => async ({ update }) => {
										await update();
										await invalidateAll();
									}}
								>
									<input type="hidden" name="name" value={j.name} />
									<button class="cta" disabled={running !== null}>
										{running?.name === j.name ? 'Bezig…' : 'Start'}
									</button>
								</form>
							</div>
						{/each}
					</div>
				</div>
			</div>

			<!-- Stap 2 — Projectie -->
			<div class="step">
				<div class="step-rail"><span class="step-n">2</span></div>
				<div class="step-body">
					<div class="step-head">
						<h3 class="step-title">Projectie</h3>
						<span class="tier {projectionPill.tone}">{projectionPill.text}</span>
					</div>
					<p class="step-desc">Canonieke entiteiten, mechanic-predicaten en ontologie-versies
						idempotent naar Neo4j projecteren (na de extractie).</p>
					<div class="jobrows">
						<div class="jobrow">
							<div class="jr-info">
								<span class="jr-stat">
									<span class="tnum">{cockpit.canonicalEntities.toLocaleString('nl-NL')}</span> canonieke
									entiteiten klaar om te projecteren
								</span>
								<span class="run-meta">{runMeta(cockpit.projectionRun)}</span>
							</div>
							<form
								method="POST"
								action="?/job"
								use:enhance={() => async ({ update }) => {
									await update();
									await invalidateAll();
								}}
							>
								<input type="hidden" name="name" value="breinprojectie" />
								<button class="cta" disabled={running !== null}>
									{running?.name === 'breinprojectie' ? 'Bezig…' : 'Start'}
								</button>
							</form>
						</div>
					</div>
				</div>
			</div>

			<!-- Stap 3 — Reasoner -->
			<div class="step">
				<div class="step-rail"><span class="step-n">3</span></div>
				<div class="step-body">
					<div class="step-head">
						<h3 class="step-title">Reasoner</h3>
						<span class="tier {reasonPill.tone}">{reasonPill.text}</span>
					</div>
					<p class="step-desc">Monotone inferentie (afgeleide edges) + bounded contradictie-detectie
						over de geprojecteerde graaf (na de projectie).</p>
					<div class="jobrows">
						<div class="jobrow">
							<div class="jr-info">
								<span class="jr-stat">
									<span class="tnum">{cockpit.conflicts.toLocaleString('nl-NL')}</span> conflicts
									<span class="muted">({cockpit.conflictsOpen.toLocaleString('nl-NL')} open)</span>
								</span>
								<span class="run-meta">{runMeta(cockpit.reasonRun)}</span>
							</div>
							<form
								method="POST"
								action="?/job"
								use:enhance={() => async ({ update }) => {
									await update();
									await invalidateAll();
								}}
							>
								<input type="hidden" name="name" value="reason" />
								<button class="cta" disabled={running !== null}>
									{running?.name === 'reason' ? 'Bezig…' : 'Start'}
								</button>
							</form>
						</div>
					</div>
				</div>
			</div>

			<!-- Consument — /ask-retrieval (env-flag, geen knop) -->
			<div class="step consumer">
				<div class="step-rail"><span class="step-n ask">ask</span></div>
				<div class="step-body">
					<div class="step-head">
						<h3 class="step-title">/ask-retrieval</h3>
						<span class="tier {cockpit.retrievalEnabled ? 'ok' : 'warn'}"
							>{cockpit.retrievalEnabled ? 'AAN' : 'UIT'}</span
						>
					</div>
					<p class="step-desc">Gebruikt de brein-graaf (GraphRAG) in /ask-antwoorden — de consument
						van de pipeline hierboven.</p>
					{#if !cockpit.retrievalEnabled}
						<p class="flaghint">
							uit — zet <code>BREIN_RETRIEVAL_ENABLED=true</code> op de VM om de retrieval aan te zetten.
						</p>
					{/if}
				</div>
			</div>
		</div>

		<!-- Nachtrun — de volledige ongecapte keten (#245) -->
		<div class="nightly">
			<div class="nightly-info">
				<h3>Volledige nachtrun</h3>
				<p>
					De hele keten <strong>ongecapt</strong> in één run: alles bijwerken &rarr; interacties
					&rarr; predicaten &rarr; projectie &rarr; reason. Draait automatisch elke nacht
					(00:00&ndash;11:00) tot het venster-einde en pakt de rest de volgende nacht op; overdag
					blijven de losse jobs hierboven gecapt.
				</p>
				<span class="run-meta">{runMeta(cockpit.nightlyRun)}</span>
			</div>
			<form
				method="POST"
				action="?/job"
				use:enhance={() => async ({ update }) => {
					await update();
					await invalidateAll();
				}}
			>
				<input type="hidden" name="name" value="nachtrun" />
				<button class="cta" disabled={running !== null}>
					{running?.name === 'nachtrun' ? 'Bezig…' : 'Nu draaien'}
				</button>
			</form>
		</div>
	</section>
{/if}

{#if data.apiDown}
	<p class="apidown">Het brein is niet bereikbaar — is rb-api op? Probeer het later opnieuw.</p>
{:else if !counts || totalBrein === 0}
	<div class="empty">
		Nog geen brein-data — draai de brein-jobs via de pipeline hierboven (1 &rarr; 2 &rarr; 3). Zodra
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
	/* ── Cockpit (brein-jobs-ui) ─────────────────────────────────────────── */
	.cockpit {
		margin-bottom: 26px;
	}
	.ckhead {
		display: flex;
		align-items: baseline;
		gap: 12px;
		flex-wrap: wrap;
	}
	.ckhead h2 {
		margin: 0;
	}
	.orderhint {
		font-size: 0.74rem;
		color: var(--muted);
		text-transform: uppercase;
		letter-spacing: 0.04em;
		font-weight: 600;
	}
	.notice {
		margin: 12px 0 0;
		padding: 10px 13px;
		border-radius: var(--radius-lg);
		font-size: 0.85rem;
		border: 1px solid var(--border);
	}
	.notice.ok {
		background: var(--ok-soft);
		border-color: transparent;
		color: var(--ok);
	}
	.notice.err {
		background: var(--err-soft);
		border-color: transparent;
		color: var(--err);
	}
	.pipeline {
		margin-top: 14px;
		display: flex;
		flex-direction: column;
	}
	.step {
		display: grid;
		grid-template-columns: auto minmax(0, 1fr);
		gap: 14px;
	}
	.step-rail {
		display: flex;
		flex-direction: column;
		align-items: center;
	}
	/* Verbindingslijn tussen de stap-nummers (1 → 2 → 3 → ask). */
	.step-rail::after {
		content: '';
		width: 2px;
		flex: 1 1 auto;
		min-height: 8px;
		background: var(--border);
		margin: 6px 0 0;
	}
	.step:last-child .step-rail::after {
		display: none;
	}
	.step-n {
		flex: none;
		width: 28px;
		height: 28px;
		border-radius: 50%;
		display: flex;
		align-items: center;
		justify-content: center;
		font-size: 0.9rem;
		font-weight: 700;
		font-variant-numeric: tabular-nums;
		background: var(--accent);
		color: var(--accent-ink);
	}
	.step-n.ask {
		font-size: 0.62rem;
		text-transform: uppercase;
		letter-spacing: 0.03em;
		background: var(--surface-deep);
		color: var(--muted);
		border: 1px solid var(--border);
	}
	.step-body {
		background: var(--surface);
		border: 1px solid var(--border);
		border-radius: var(--radius-lg);
		box-shadow: var(--shadow-card);
		padding: 13px 15px;
		margin-bottom: 12px;
		min-width: 0;
	}
	.step.consumer .step-body {
		background: transparent;
		box-shadow: none;
		border-style: dashed;
	}
	.step-head {
		display: flex;
		align-items: center;
		gap: 10px;
		flex-wrap: wrap;
	}
	.step-title {
		/* Overschrijft de generieke, uppercase h3-regel hieronder. */
		margin: 0;
		font-size: 0.98rem;
		font-weight: 650;
		text-transform: none;
		letter-spacing: 0;
		color: var(--text);
	}
	.step-desc {
		margin: 6px 0 10px;
		font-size: 0.82rem;
		color: var(--muted);
		line-height: 1.45;
		max-width: 68ch;
	}
	.jobrows {
		display: flex;
		flex-direction: column;
		gap: 8px;
	}
	.jobrow {
		display: flex;
		align-items: center;
		gap: 12px;
		flex-wrap: wrap;
		padding: 9px 11px;
		border: 1px solid var(--border);
		border-radius: var(--radius-md, 8px);
		background: var(--surface-deep);
	}
	.jr-info {
		display: flex;
		flex-direction: column;
		gap: 2px;
		min-width: 0;
		flex: 1 1 200px;
	}
	.jr-info strong {
		font-size: 0.86rem;
		font-weight: 650;
	}
	.jr-stat {
		font-size: 0.82rem;
		color: var(--text);
	}
	.run-meta {
		font-size: 0.72rem;
		color: var(--muted);
	}
	.jobrow form {
		margin: 0;
		flex: none;
	}
	.nightly {
		display: flex;
		align-items: center;
		gap: 14px;
		flex-wrap: wrap;
		margin-top: 16px;
		padding: 14px 16px;
		border: 1px solid var(--border-strong);
		border-radius: var(--radius-lg, 13px);
		background: var(--surface-deep);
	}
	.nightly-info {
		flex: 1 1 260px;
		min-width: 0;
		display: flex;
		flex-direction: column;
		gap: 5px;
	}
	.nightly-info h3 {
		margin: 0;
		font-size: 0.95rem;
	}
	.nightly-info p {
		margin: 0;
		font-size: 0.82rem;
		line-height: 1.45;
		color: var(--muted);
	}
	.nightly form {
		margin: 0;
		flex: none;
	}
	.cta {
		font: inherit;
		font-size: 0.8rem;
		font-weight: 600;
		padding: 7px 16px;
		border-radius: 999px;
		border: 1px solid transparent;
		background: var(--accent);
		color: var(--accent-ink);
		cursor: pointer;
		white-space: nowrap;
	}
	.cta:hover:not(:disabled) {
		filter: brightness(0.96);
	}
	.cta:disabled {
		opacity: 0.5;
		cursor: not-allowed;
	}
	.flaghint {
		margin: 4px 0 0;
		font-size: 0.8rem;
		color: var(--muted);
		line-height: 1.45;
	}
	.flaghint code {
		font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
		font-size: 0.78rem;
		padding: 1px 5px;
		border-radius: 5px;
		background: var(--surface-deep);
		border: 1px solid var(--border);
		color: var(--text);
	}

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
