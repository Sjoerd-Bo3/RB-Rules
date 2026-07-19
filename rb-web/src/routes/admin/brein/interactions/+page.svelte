<script lang="ts">
	let { data } = $props();

	interface Condition {
		onKind: string;
		subjectRole: string | null;
		value: string;
		operator: string | null;
	}
	interface InteractionItem {
		id: number;
		kind: string;
		agentRef: string;
		patientRef: string;
		governedByRef: string | null;
		status: string;
		statusReason: string | null;
		createdByRunId: string;
		detectedAt: string;
		promotedAt: string | null;
		conditions: Condition[];
		subjectRef: string;
		assertionRef: string | null;
	}
	interface Paged<T> {
		total: number;
		page: number;
		pageSize: number;
		items: T[];
	}
	interface MiningRun {
		id: string;
		kind: string;
		llmModel: string | null;
		promptVersion: string | null;
		embeddingModel: string | null;
		vocabSnapshot: string | null;
		gitSha: string | null;
		startedAt: string;
		completedAt: string | null;
		candidates: number;
		verified: number;
		rejected: number;
	}
	interface Assertion {
		id: string;
		subject: string;
		factKind: string;
		miningRunId: string;
		derivedFromRef: string;
		derivedFromDocumentId: number | null;
		model: string | null;
		promptVersion: string | null;
		verifier: string | null;
		verdict: string | null;
		evidenceSpan: string | null;
		validFrom: string | null;
		assertedAt: string;
		run: MiningRun | null;
	}
	interface Chain {
		subject: string;
		assertions: Assertion[];
	}

	const paged = $derived(data.data as Paged<InteractionItem> | null);
	const chain = $derived(data.chain as Chain | null);
	const totalPages = $derived(paged ? Math.max(1, Math.ceil(paged.total / paged.pageSize)) : 1);

	const STATUS_CHIPS = [
		{ v: '', label: 'Alle tiers' },
		{ v: 'promoted', label: 'Gepromoveerd' },
		{ v: 'verified', label: 'Geverifieerd' },
		{ v: 'candidate', label: 'Kandidaat' },
		{ v: 'model_hypothesized_unruled', label: 'Hypothese' },
		{ v: 'rejected', label: 'Verworpen' }
	];

	function href(patch: Record<string, string | number>): string {
		const p = new URLSearchParams();
		const status = patch.status ?? data.status;
		const page = patch.page ?? paged?.page ?? 1;
		const sel = 'sel' in patch ? patch.sel : data.sel;
		if (status) p.set('status', String(status));
		if (Number(page) > 1) p.set('page', String(page));
		if (sel) p.set('sel', String(sel));
		const q = p.toString();
		return q ? `?${q}` : '?';
	}

	function tierClass(status: string): string {
		if (status === 'promoted') return 'ok';
		if (status === 'verified') return 'accent';
		if (status === 'candidate' || status === 'model_hypothesized_unruled') return 'warn';
		if (status === 'rejected') return 'err';
		return '';
	}
	const fmtDate = (s: string | null) => (s ? new Date(s).toLocaleString('nl-NL') : '—');
	const condLabel = (c: Condition) =>
		`${c.onKind}${c.subjectRole ? ` (${c.subjectRole})` : ''}: ${c.operator ? `${c.operator} ` : ''}${c.value}`;
</script>

<div class="chips">
	{#each STATUS_CHIPS as c (c.v)}
		<a href={href({ status: c.v, page: 1, sel: '' })} class:on={data.status === c.v}>{c.label}</a>
	{/each}
</div>

{#if data.apiDown}
	<p class="apidown">Het brein is niet bereikbaar — is rb-api op?</p>
{:else if !paged || !paged.items.length}
	<div class="empty">
		Nog geen gereïficeerde interacties{data.status ? ' in deze tier' : ''} — draai "Interacties minen"
		in het beheer.
	</div>
{:else}
	<div class="layout" class:split={chain}>
		<div class="list">
			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Interactie</th>
							<th>Kind</th>
							<th>Condities</th>
							<th>Tier</th>
							<th>Provenance</th>
						</tr>
					</thead>
					<tbody>
						{#each paged.items as it (it.id)}
							<tr class:selected={data.sel === it.subjectRef}>
								<td>
									<span class="ref">{it.agentRef}</span>
									<span class="arrow">→</span>
									<span class="ref">{it.patientRef}</span>
									{#if it.governedByRef}
										<div class="gov muted">verankerd: <span class="ref">{it.governedByRef}</span></div>
									{/if}
								</td>
								<td><span class="kind">{it.kind}</span></td>
								<td>
									{#if it.conditions.length}
										{#each it.conditions as c (c.onKind + c.value)}
											<div class="cond">{condLabel(c)}</div>
										{/each}
									{:else}
										<span class="muted">—</span>
									{/if}
								</td>
								<td>
									<span class="tier {tierClass(it.status)}">{it.status}</span>
									{#if it.statusReason}<div class="reason muted">{it.statusReason}</div>{/if}
								</td>
								<td>
									{#if it.assertionRef}
										<a class="chainlink" href={href({ sel: it.subjectRef })}>keten bekijken</a>
									{:else}
										<span class="muted">geen assertion</span>
									{/if}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>

			<nav class="pager">
				{#if paged.page > 1}<a href={href({ page: paged.page - 1 })}>Vorige</a>{/if}
				<span class="tnum">Pagina {paged.page} / {totalPages} · {paged.total} interacties</span>
				{#if paged.page < totalPages}<a href={href({ page: paged.page + 1 })}>Volgende</a>{/if}
			</nav>
		</div>

		{#if chain}
			<aside class="chain">
				<div class="chain-head">
					<h2>Provenance-keten</h2>
					<a class="close" href={href({ sel: '' })} aria-label="Keten sluiten">sluiten</a>
				</div>
				<p class="ref subj">{chain.subject}</p>
				{#if !chain.assertions.length}
					<p class="empty">Geen provenance vastgelegd voor dit feit.</p>
				{:else}
					{#each chain.assertions as a (a.id)}
						<div class="prov panel">
							<div class="prow">
								<span class="plabel">WAS_GENERATED_BY</span>
								{#if a.run}
									<span class="pval"
										>{a.run.kind} · {a.run.llmModel ?? 'deterministisch'}{a.run.promptVersion
											? ` · ${a.run.promptVersion}`
											: ''}</span
									>
								{:else}
									<span class="pval ref">{a.miningRunId}</span>
								{/if}
							</div>
							<div class="prow">
								<span class="plabel">DERIVED_FROM</span>
								<span class="pval ref">{a.derivedFromRef}</span>
							</div>
							<div class="prow">
								<span class="plabel">VERIFIED_BY</span>
								<span class="pval">
									{#if a.verifier}
										{a.verifier}{a.verdict ? ` · ${a.verdict}` : ''}
									{:else}
										<span class="muted">onbevestigd</span>
									{/if}
								</span>
							</div>
							{#if a.evidenceSpan}
								<div class="evidence">&ldquo;{a.evidenceSpan}&rdquo;</div>
							{/if}
							<div class="pfoot muted tnum">{fmtDate(a.assertedAt)}</div>
						</div>
					{/each}
				{/if}
			</aside>
		{/if}
	</div>
{/if}

<style>
	.layout.split {
		display: grid;
		grid-template-columns: minmax(0, 1fr) 340px;
		gap: 18px;
		align-items: start;
	}
	@media (max-width: 900px) {
		.layout.split {
			grid-template-columns: minmax(0, 1fr);
		}
	}
	.arrow {
		color: var(--muted);
		margin: 0 4px;
	}
	.kind {
		font-size: 0.72rem;
		font-weight: 700;
		letter-spacing: 0.03em;
		color: var(--muted);
	}
	.cond {
		font-size: 0.78rem;
		margin: 1px 0;
	}
	.reason,
	.gov {
		font-size: 0.72rem;
		margin-top: 4px;
	}
	tr.selected {
		background: var(--accent-soft);
	}
	.chainlink {
		color: var(--accent);
		text-decoration: none;
		font-weight: 600;
		font-size: 0.8rem;
	}
	.chainlink:hover {
		text-decoration: underline;
	}
	.chain {
		position: sticky;
		top: 12px;
	}
	.chain-head {
		display: flex;
		align-items: baseline;
		justify-content: space-between;
	}
	.chain-head h2 {
		margin: 0;
	}
	.close {
		font-size: 0.78rem;
		color: var(--muted);
		text-decoration: none;
	}
	.close:hover {
		color: var(--text);
	}
	.subj {
		margin: 4px 0 12px;
	}
	.prov {
		padding: 11px 13px;
		margin-bottom: 10px;
	}
	.prow {
		display: flex;
		flex-direction: column;
		gap: 1px;
		margin-bottom: 7px;
	}
	.plabel {
		font-size: 0.6rem;
		text-transform: uppercase;
		letter-spacing: 0.06em;
		color: var(--muted);
		font-weight: 700;
	}
	.pval {
		font-size: 0.82rem;
	}
	.evidence {
		font-size: 0.8rem;
		font-style: italic;
		color: var(--muted);
		border-left: 2px solid var(--border-strong);
		padding-left: 8px;
		margin: 6px 0;
	}
	.pfoot {
		font-size: 0.72rem;
		margin-top: 4px;
	}
</style>
