<script lang="ts">
	let { data } = $props();

	interface ConflictItem {
		id: number;
		patternId: string;
		kind: string;
		channel: string;
		subjectRef: string;
		counterRef: string | null;
		memo: string | null;
		status: string;
		runId: string;
		detectedAt: string;
	}
	interface Paged<T> {
		total: number;
		page: number;
		pageSize: number;
		items: T[];
	}

	const paged = $derived(data.data as Paged<ConflictItem> | null);
	const totalPages = $derived(paged ? Math.max(1, Math.ceil(paged.total / paged.pageSize)) : 1);

	const STATUS_CHIPS = [
		{ v: '', label: 'Alle' },
		{ v: 'open', label: 'Open' },
		{ v: 'reviewed', label: 'Beoordeeld' },
		{ v: 'resolved', label: 'Opgelost' },
		{ v: 'dismissed', label: 'Afgewezen' }
	];

	function href(patch: Record<string, string | number>): string {
		const p = new URLSearchParams();
		const status = patch.status ?? data.status;
		const page = patch.page ?? paged?.page ?? 1;
		if (status) p.set('status', String(status));
		if (Number(page) > 1) p.set('page', String(page));
		const q = p.toString();
		return q ? `?${q}` : '?';
	}

	// Kanaal = kleur + tekst (geen emoji). Misvattingen springen eruit (accent).
	function channelClass(channel: string): string {
		if (channel === 'misconception') return 'accent';
		if (channel === 'escalation') return 'err';
		if (channel === 'reviewqueue') return 'warn';
		return '';
	}
	const channelLabel = (c: string) =>
		c === 'misconception'
			? 'misvattingen-kanaal'
			: c === 'escalation'
				? 'escalatie'
				: c === 'reviewqueue'
					? 'reviewqueue'
					: c;
	const fmtDate = (s: string) => new Date(s).toLocaleString('nl-NL');
</script>

<div class="chips">
	{#each STATUS_CHIPS as c (c.v)}
		<a href={href({ status: c.v, page: 1 })} class:on={data.status === c.v}>{c.label}</a>
	{/each}
</div>

{#if data.apiDown}
	<p class="apidown">Het brein is niet bereikbaar — is rb-api op?</p>
{:else if !paged || !paged.items.length}
	<div class="empty">
		Nog geen reasoning-conflicts{data.status ? ' met deze status' : ''} — de reasoner heeft (nog)
		geen tegenspraken gevonden. Draai de reasoner-job in het beheer.
	</div>
{:else}
	<div class="table-wrap">
		<table>
			<thead>
				<tr>
					<th>Soort</th>
					<th>Kanaal</th>
					<th>Onderwerp</th>
					<th>Tegenspraak</th>
					<th>Memo</th>
					<th>Status</th>
					<th>Gezien</th>
				</tr>
			</thead>
			<tbody>
				{#each paged.items as c (c.id)}
					<tr>
						<td class="muted">{c.kind}</td>
						<td><span class="tier {channelClass(c.channel)}">{channelLabel(c.channel)}</span></td>
						<td><span class="ref">{c.subjectRef}</span></td>
						<td>{#if c.counterRef}<span class="ref">{c.counterRef}</span>{:else}<span class="muted">—</span>{/if}</td>
						<td class="memo">{c.memo ?? '—'}</td>
						<td class="muted">{c.status}</td>
						<td class="muted tnum">{fmtDate(c.detectedAt)}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>

	<nav class="pager">
		{#if paged.page > 1}<a href={href({ page: paged.page - 1 })}>Vorige</a>{/if}
		<span class="tnum">Pagina {paged.page} / {totalPages} · {paged.total} conflicts</span>
		{#if paged.page < totalPages}<a href={href({ page: paged.page + 1 })}>Volgende</a>{/if}
	</nav>
{/if}

<style>
	.memo {
		max-width: 360px;
		font-size: 0.8rem;
		color: var(--muted);
	}
</style>
