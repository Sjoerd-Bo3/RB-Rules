<script lang="ts">
	let { data } = $props();

	interface EntityItem {
		id: number;
		kind: string;
		canonicalLabel: string;
		altLabels: string[];
		definition: string | null;
		status: string;
		mergedIntoId: number | null;
		mergedIntoLabel: string | null;
		createdByRunId: string;
		createdAt: string;
	}
	interface Paged<T> {
		total: number;
		page: number;
		pageSize: number;
		items: T[];
	}

	const paged = $derived(data.data as Paged<EntityItem> | null);
	const totalPages = $derived(paged ? Math.max(1, Math.ceil(paged.total / paged.pageSize)) : 1);

	const KIND_CHIPS = [
		{ v: '', label: 'Alle kinds' },
		{ v: 'mechanic', label: 'Mechanic' },
		{ v: 'keyword', label: 'Keyword' },
		{ v: 'concept', label: 'Concept' }
	];
	const STATUS_CHIPS = [
		{ v: '', label: 'Alle statussen' },
		{ v: 'canonical', label: 'Canoniek' },
		{ v: 'candidate', label: 'Kandidaat' },
		{ v: 'merged', label: 'Merged (tombstone)' }
	];

	function href(patch: Record<string, string | number>): string {
		const p = new URLSearchParams();
		const kind = patch.kind ?? data.kind;
		const status = patch.status ?? data.status;
		const page = patch.page ?? paged?.page ?? 1;
		if (kind) p.set('kind', String(kind));
		if (status) p.set('status', String(status));
		if (Number(page) > 1) p.set('page', String(page));
		const q = p.toString();
		return q ? `?${q}` : '?';
	}

	function tierClass(status: string): string {
		if (status === 'canonical') return 'ok';
		if (status === 'candidate') return 'warn';
		if (status === 'merged') return 'err';
		return '';
	}
	const fmtDate = (s: string) => new Date(s).toLocaleDateString('nl-NL');
</script>

<div class="chips">
	{#each KIND_CHIPS as c (c.v)}
		<a href={href({ kind: c.v, page: 1 })} class:on={data.kind === c.v}>{c.label}</a>
	{/each}
</div>
<div class="chips">
	{#each STATUS_CHIPS as c (c.v)}
		<a href={href({ status: c.v, page: 1 })} class:on={data.status === c.v}>{c.label}</a>
	{/each}
</div>

{#if data.apiDown}
	<p class="apidown">Het brein is niet bereikbaar — is rb-api op?</p>
{:else if !paged || !paged.items.length}
	<div class="empty">
		Nog geen canonieke entiteiten{data.kind || data.status ? ' voor deze selectie' : ''} — draai de
		mechanic-/keyword-mining in het beheer.
	</div>
{:else}
	<div class="table-wrap">
		<table>
			<thead>
				<tr>
					<th>Kind</th>
					<th>Canoniek label</th>
					<th>Alias-lexicon</th>
					<th>Status</th>
					<th>Definitie</th>
					<th>Aangemaakt</th>
				</tr>
			</thead>
			<tbody>
				{#each paged.items as e (e.id)}
					<tr>
						<td class="muted">{e.kind}</td>
						<td><strong>{e.canonicalLabel}</strong></td>
						<td>
							{#if e.altLabels.length}
								<span class="alts">
									{#each e.altLabels as a (a)}<span class="alt">{a}</span>{/each}
								</span>
							{:else}
								<span class="muted">—</span>
							{/if}
						</td>
						<td>
							<span class="tier {tierClass(e.status)}">{e.status}</span>
							{#if e.mergedIntoId !== null}
								<div class="mergeinfo muted">
									→ {e.mergedIntoLabel ?? `#${e.mergedIntoId}`}
								</div>
							{/if}
						</td>
						<td class="def">{e.definition ?? '—'}</td>
						<td class="muted tnum">{fmtDate(e.createdAt)}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>

	<nav class="pager">
		{#if paged.page > 1}<a href={href({ page: paged.page - 1 })}>Vorige</a>{/if}
		<span class="tnum">Pagina {paged.page} / {totalPages} · {paged.total} entiteiten</span>
		{#if paged.page < totalPages}<a href={href({ page: paged.page + 1 })}>Volgende</a>{/if}
	</nav>
{/if}

<style>
	.alts {
		display: flex;
		flex-wrap: wrap;
		gap: 4px;
	}
	.alt {
		font-size: 0.74rem;
		padding: 1px 7px;
		border-radius: 999px;
		background: var(--surface-deep);
		border: 1px solid var(--border);
		color: var(--muted);
	}
	.mergeinfo {
		font-size: 0.74rem;
		margin-top: 4px;
	}
	.def {
		max-width: 320px;
		color: var(--muted);
		font-size: 0.8rem;
	}
</style>
