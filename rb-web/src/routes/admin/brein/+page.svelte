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
	interface ProviderUsage {
		provider: string;
		unit: string;
		runs: number;
		calls: number;
		inputTokens: number;
		outputTokens: number;
		costUsd: number | null;
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
		interactionAudits: number;
		auditRun: JobRun | null;
		providerUsage: ProviderUsage[] | null;
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
					},
					// Steekproef-audit (#255): 1 op de N gepromoveerde interacties langs een
					// sterker model. Meting + provenance; het oordeel verandert nooit zelf
					// een tier — een negatief oordeel landt in de conflicts-reviewqueue.
					{
						name: 'breinaudit-interacties',
						label: 'Steekproef-audit (sterker model)',
						n: cockpit.interactionAudits,
						unit: 'audit-oordelen',
						run: cockpit.auditRun
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

	// ── Beheerde instellingen (#254) ────────────────────────────────────────
	// De feature-vlaggen die vroeger alleen via de VM-.env + een herstart te
	// zetten waren. rb-api leest ze op het gebruiksmoment, dus een toggle hier
	// werkt meteen. "default" is de env-/codewaarde die geldt zonder override —
	// die tonen we erbij zodat zichtbaar blijft waar je vandaan komt.
	interface ManagedSetting {
		key: string;
		kind: string;
		group: string;
		label: string;
		description: string;
		effective: string;
		default: string;
		overridden: boolean;
		updatedAt: string | null;
		updatedBy: string | null;
		options: string[] | null;
	}
	const settings = $derived((data.settings ?? []) as ManagedSetting[]);
	const settingOf = (key: string) => settings.find((s) => s.key === key) ?? null;

	const retrievalSetting = $derived(settingOf('brein.retrieval.enabled'));
	const auditSampleSetting = $derived(settingOf('brein.audit.sample_n'));
	const extractModelSetting = $derived(settingOf('brein.extract.model'));
	const nightlySetting = $derived(settingOf('nightly.enabled'));
	// Het venster in het formulier: uit de effectieve waarden, met een terugval op
	// de bestaande defaults zolang de instellingen-lijst er nog niet is.
	const nightlyStart = $derived(settingOf('nightly.start_hour')?.effective ?? '0');
	const nightlyEnd = $derived(settingOf('nightly.end_hour')?.effective ?? '11');
	const nightlyTz = $derived(settingOf('nightly.timezone')?.effective ?? 'Europe/Amsterdam');
	const HOURS = Array.from({ length: 24 }, (_, i) => String(i));
	const fmtNumber = (n: number) => n.toLocaleString('nl-NL');
	const fmtUsd = (n: number) => `$${n.toFixed(6)}`;
	function modelAliasLabel(alias: string): string {
		switch (alias) {
			case 'sonnet': return 'Claude Sonnet';
			case 'opus': return 'Claude Opus';
			case 'fable': return 'Claude Fable';
			case 'codex': return 'Codex';
			default: return alias;
		}
	}

	// "gewijzigd 3u geleden door beheer" — herkomst van een schakelaar hoort
	// zichtbaar te zijn (rode draad #236); het volledige spoor staat in run_log.
	function settingMeta(s: ManagedSetting | null): string {
		if (!s) return '';
		if (!s.overridden) return `standaard (${s.default})`;
		const who = s.updatedBy ? ` door ${s.updatedBy}` : '';
		const when = s.updatedAt ? ` ${fmtAgo(s.updatedAt)} geleden` : '';
		return `beheerd${when}${who} · standaard ${s.default}`;
	}

	// Het venster is één ding uit drie sleutels — één regel eronder in plaats van
	// drie losse "standaard (0)"-fragmenten.
	const windowMeta = $derived.by(() => {
		const parts = ['nightly.start_hour', 'nightly.end_hour', 'nightly.timezone'].map(settingOf);
		if (parts.some((p) => !p)) return '';
		const [s, e, tz] = parts as ManagedSetting[];
		const standaard = `standaard ${s.default}:00–${e.default}:00 ${tz.default}`;
		const changed = parts
			.filter((p) => p!.overridden && p!.updatedAt)
			.sort((a, b) => (a!.updatedAt! < b!.updatedAt! ? 1 : -1))[0];
		return changed
			? `beheerd ${fmtAgo(changed.updatedAt)} geleden${
					changed.updatedBy ? ` door ${changed.updatedBy}` : ''
				} · ${standaard}`
			: standaard;
	});

	const startedLabel: Record<string, string> = {
		breinentiteiten: 'Canonieke entiteiten registreren',
		'breinmine-interacties': 'Interacties minen',
		'breinmine-predicaten': 'Mechanic-predicaten minen',
		'breinaudit-interacties': 'Steekproef-audit (sterker model)',
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
	// Gemeten precisie uit de steekproef-audit (#255) — los van de accept-ratio
	// van onze eigen promotie-poort hierboven.
	interface AuditPrecisionRow {
		model: string;
		promptVersion: string;
		audited: number;
		sound: number;
		incorrect: number;
		unsupported: number;
		precision: number;
	}
	interface Observability {
		report: {
			takenAt: string;
			graphDrift: { label: string; postgres: number; graph: number; delta: number }[];
			canonicalDrift: CanonicalDrift | null;
			miningPrecision: MiningPrecisionRow[];
			communityHealth: unknown | null;
			auditPrecision: AuditPrecisionRow[];
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
		{:else if form?.settingSaved}
			<p class="notice ok">Instelling opgeslagen — hij geldt meteen, zonder herstart. De
				wijziging staat als auditregel in het run-grootboek.</p>
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
						<!--
							Gerichte mining-reset (#263): een verwerkte kaart draagt een watermark en
							wordt bij een volgende run overgeslagen, dus een verbeterde extractie raakt
							de bestaande pool niet. De reset zelf staat bewust in de Gevarenzone
							(destructief, met bevestiging) — hier alleen de wegwijzer, zodat hij
							vindbaar is vanaf de plek waar je hem nodig hebt.
						-->
						<p class="flaghint">
							Een kaart die al een interactie opleverde, wordt bij een volgende run
							overgeslagen. Wil je na een verbeterde extractie dezelfde pool opnieuw laten
							minen, gebruik dan <a href="/admin#gevarenzone">Brein-mining resetten</a> in de
							Gevarenzone.
						</p>
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
					{#if auditSampleSetting}
						<!-- Steekproefdichtheid (#255, beheerd via #254): 1 op de N gepromoveerde
						     interacties gaat langs het sterkere model. Direct effect, geen herstart. -->
						<form
							class="auditdensity"
							method="POST"
							action="?/setting"
							use:enhance={() => async ({ update }) => {
								await update();
								await invalidateAll();
							}}
						>
							<label>
								<span>Steekproefdichtheid: 1 op</span>
								<input type="hidden" name="key" value="brein.audit.sample_n" />
								<input
									name="value"
									type="number"
									min="1"
									max="100"
									value={auditSampleSetting.effective}
								/>
							</label>
							<button class="cta">Opslaan</button>
							<span class="run-meta">{settingMeta(auditSampleSetting)}</span>
						</form>
					{/if}
					{#if extractModelSetting}
						<form
							class="auditdensity"
							method="POST"
							action="?/setting"
							use:enhance={() => async ({ update }) => {
								await update();
								await invalidateAll();
							}}
						>
							<label>
								<span>Extractiemodel</span>
								<input type="hidden" name="key" value="brein.extract.model" />
								<select name="value" aria-label="Extractiemodel">
									{#each extractModelSetting.options ?? [] as alias (alias)}
										<option value={alias} selected={alias === extractModelSetting.effective}>{modelAliasLabel(alias)}</option>
									{/each}
								</select>
							</label>
							<button class="cta">Opslaan</button>
							<span class="run-meta">{settingMeta(extractModelSetting)}</span>
						</form>
					{/if}

					{#if cockpit.providerUsage?.length}
						<div class="provider-usage">
							<h4>Providergebruik</h4>
							<div class="table-wrap">
								<table>
									<thead>
										<tr><th>Provider</th><th>Runs</th><th>Calls</th><th>Eenheid</th><th>In / uit</th><th>Kosten</th></tr>
									</thead>
									<tbody>
										{#each cockpit.providerUsage as usage (`${usage.provider}:${usage.unit}`)}
											<tr>
												<td><strong>{usage.provider}</strong></td>
												<td class="tnum">{fmtNumber(usage.runs)}</td>
												<td class="tnum">{fmtNumber(usage.calls)}</td>
												<td>{usage.unit}</td>
												<td class="tnum">{fmtNumber(usage.inputTokens)} / {fmtNumber(usage.outputTokens)}</td>
												<td class="tnum">{usage.costUsd === null ? '—' : fmtUsd(usage.costUsd)}</td>
											</tr>
										{/each}
									</tbody>
								</table>
							</div>
							<p class="run-meta">Cumulatief over de bewaarde brein-miningruns; kosten zijn alleen ingevuld als de provider echte USD-kosten rapporteert.</p>
						</div>
					{/if}
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

			<!-- Consument — /ask-retrieval (beheerde vlag, #254: echte knop) -->
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
					{#if retrievalSetting}
						<div class="jobrow">
							<div class="jr-info">
								<span class="jr-stat"
									>Retrieval staat <strong>{cockpit.retrievalEnabled ? 'aan' : 'uit'}</strong> voor
									alle /ask-vragen</span
								>
								<span class="run-meta">{settingMeta(retrievalSetting)}</span>
							</div>
							<form
								method="POST"
								action="?/setting"
								use:enhance={() => async ({ update }) => {
									await update();
									await invalidateAll();
								}}
							>
								<input type="hidden" name="key" value="brein.retrieval.enabled" />
								<input
									type="hidden"
									name="value"
									value={cockpit.retrievalEnabled ? 'false' : 'true'}
								/>
								<button class="cta">{cockpit.retrievalEnabled ? 'Uitzetten' : 'Aanzetten'}</button>
							</form>
						</div>
					{:else}
						<p class="flaghint">De instellingen zijn even niet op te halen — probeer te
							herladen.</p>
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
					&rarr; predicaten &rarr; projectie &rarr; reason. Draait automatisch binnen het venster
					hieronder tot het venster-einde en pakt de rest de volgende nacht op; overdag blijven
					de losse jobs hierboven gecapt.
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

		<!-- Nachtrun-instellingen (#254): noodrem + venster, zonder SSH of herstart -->
		{#if nightlySetting}
			<div class="nightly settings">
				<div class="nightly-info">
					<h3>
						Automatische nachtrun
						<span class="tier {nightlySetting.effective === 'true' ? 'ok' : 'warn'}"
							>{nightlySetting.effective === 'true' ? 'AAN' : 'GEPAUZEERD'}</span
						>
					</h3>
					<p>
						De noodrem op de nachtelijke keten. Uit = de scheduler start de nachtrun niet;
						handmatig starten met &ldquo;Nu draaien&rdquo; blijft altijd werken.
					</p>
					<span class="run-meta">{settingMeta(nightlySetting)}</span>
				</div>
				<form
					method="POST"
					action="?/setting"
					use:enhance={() => async ({ update }) => {
						await update();
						await invalidateAll();
					}}
				>
					<input type="hidden" name="key" value="nightly.enabled" />
					<input
						type="hidden"
						name="value"
						value={nightlySetting.effective === 'true' ? 'false' : 'true'}
					/>
					<button class="cta">
						{nightlySetting.effective === 'true' ? 'Pauzeren' : 'Hervatten'}
					</button>
				</form>
			</div>

			<form
				class="nightly window"
				method="POST"
				action="?/setting"
				use:enhance={() => async ({ update }) => {
					await update();
					await invalidateAll();
				}}
			>
				<div class="nightly-info">
					<h3>Nachtvenster</h3>
					<p>
						Wanneer de nachtrun mag draaien (lokale klok, eind-uur telt niet mee en is ook de
						deadline van de run). Het venster moet binnen één kalenderdag vallen &mdash;
						start v&oacute;&oacute;r eind.
					</p>
					<span class="run-meta">{windowMeta}</span>
				</div>
				<div class="wfields">
					<label>
						<span>Start</span>
						<input type="hidden" name="key" value="nightly.start_hour" />
						<select name="value" value={nightlyStart}>
							{#each HOURS as h (h)}
								<option value={h}>{h}:00</option>
							{/each}
						</select>
					</label>
					<label>
						<span>Eind</span>
						<input type="hidden" name="key" value="nightly.end_hour" />
						<select name="value" value={nightlyEnd}>
							{#each HOURS as h (h)}
								<option value={h}>{h}:00</option>
							{/each}
						</select>
					</label>
					<label class="tzfield">
						<span>Tijdzone</span>
						<input type="hidden" name="key" value="nightly.timezone" />
						<input name="value" value={nightlyTz} spellcheck="false" autocapitalize="off" />
					</label>
					<button class="cta">Opslaan</button>
				</div>
			</form>
		{/if}
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
		<p class="muted small">
			Let op: de kolom &ldquo;Precisie&rdquo; hierboven is de accept-ratio van onze eigen
			promotie-poort (geverifieerd &divide; beoordeeld) &mdash; zelfreferentieel. De gemeten
			precisie hieronder komt uit een onafhankelijke steekproef.
		</p>

		<h3>Gemeten precisie (steekproef-audit)</h3>
		{#if obs.report.auditPrecision.length}
			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Steekproef</th>
							<th class="num">n</th>
							<th class="num">Bevestigd</th>
							<th class="num">Onjuist</th>
							<th class="num">Niet gedragen</th>
							<th class="num">Gemeten precisie</th>
						</tr>
					</thead>
					<tbody>
						{#each obs.report.auditPrecision as r (r.model + r.promptVersion)}
							<tr>
								<td>steekproef door {r.model}, n={r.audited}
									<span class="muted">({r.promptVersion})</span></td>
								<td class="num tnum">{r.audited}</td>
								<td class="num tnum">{r.sound}</td>
								<td class="num tnum">{r.incorrect}</td>
								<td class="num tnum">{r.unsupported}</td>
								<td class="num tnum">{pct(r.precision)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			<p class="muted small">
				Bevestigd = correct &eacute;n gedragen door het bewijs. Een betwiste interactie
				verandert nooit vanzelf van tier &mdash; ze staat als open conflict in de
				<a href="/admin/brein/conflicts">reviewqueue</a>.
			</p>
		{:else}
			<p class="empty">
				Nog geen audit-oordelen. De steekproef pakt een vast deel van de gepromoveerde
				interacties (1 op N, op basis van het interactie-id) &mdash; bij een kleine pool
				kan de job &ldquo;Steekproef-audit&rdquo; dus niets te doen hebben. Zet de
				steekproefdichtheid op 1 op 1 voor volledige dekking, of draai eerst de
				interactie-mining.
			</p>
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
	/* Steekproefdichtheid-knop (#255) onder de audit-jobrij. */
	.auditdensity {
		display: flex;
		align-items: center;
		gap: 10px;
		flex-wrap: wrap;
		margin: 10px 0 0;
	}
	.auditdensity label {
		display: flex;
		align-items: center;
		gap: 8px;
		font-size: 0.82rem;
		color: var(--text);
	}
	.auditdensity input[type='number'] {
		width: 78px;
		padding: 7px 9px;
		font-size: 16px; /* iOS zoomt op form-controls < 16px */
		border: 1px solid var(--border);
		border-radius: var(--radius-md, 8px);
		background: var(--surface-deep);
		color: var(--text);
	}
	.auditdensity select {
		padding: 7px 28px 7px 9px;
		font-size: 16px;
		border: 1px solid var(--border);
		border-radius: var(--radius-md, 8px);
		background: var(--surface-deep);
		color: var(--text);
	}
	.auditdensity .run-meta {
		flex-basis: 100%;
	}
	.provider-usage {
		margin-top: 16px;
	}
	.provider-usage h4 {
		margin: 0 0 8px;
		font-size: 0.84rem;
	}
	.provider-usage table {
		font-size: 0.78rem;
	}
	.provider-usage .run-meta {
		display: block;
		margin-top: 7px;
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
	/* Beheerde instellingen (#254): zelfde blokvorm als de nachtrun-kaart, maar
	   secundair — dit zijn schakelaars, geen acties. */
	.nightly.settings,
	.nightly.window {
		margin-top: 10px;
		border-color: var(--border);
		background: var(--surface);
	}
	.nightly.window {
		align-items: flex-end;
	}
	.nightly-info h3 .tier {
		margin-left: 8px;
		vertical-align: middle;
	}
	.wfields {
		display: flex;
		align-items: flex-end;
		gap: 10px;
		flex-wrap: wrap;
	}
	.wfields label {
		display: flex;
		flex-direction: column;
		gap: 4px;
		font-size: 0.72rem;
		color: var(--muted);
		text-transform: uppercase;
		letter-spacing: 0.04em;
		font-weight: 600;
	}
	.wfields select,
	.wfields input {
		font: inherit;
		/* 16px: kleiner laat iOS op focus inzoomen (app.css-regel). */
		font-size: 16px;
		padding: 6px 9px;
		border-radius: var(--radius-md, 8px);
		border: 1px solid var(--border);
		background: var(--surface-deep);
		color: var(--text);
		max-width: 100%;
	}
	.wfields .tzfield input {
		/* Ruim genoeg voor "Europe/Amsterdam" zonder te klippen. */
		width: 18ch;
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
