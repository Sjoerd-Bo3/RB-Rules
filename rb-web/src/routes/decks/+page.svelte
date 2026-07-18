<script lang="ts">
	import { navigating } from '$app/state';
	import { useShell } from '$lib/shell.svelte';

	let { data } = $props();
	const shell = useShell();

	const busy = $derived(navigating.to?.url.pathname === '/decks');
	const lastPage = $derived(Math.max(1, Math.ceil(data.result.total / data.result.pageSize)));

	const SORTS = [
		{ key: 'recent', label: 'Recent bijgewerkt' },
		{ key: 'views', label: 'Meeste views' },
		{ key: 'likes', label: 'Meeste likes' }
	];

	const LEGALITY_LABEL: Record<string, string> = {
		legal: 'Legaal',
		illegal: 'Niet legaal',
		incomplete: 'Onvolledig te beoordelen'
	};

	const cardFilter = $derived(data.result.cardFilter);

	function href(params: { domain?: string; sort?: string; page?: number; card?: string }): string {
		const p = new URLSearchParams();
		const domain = params.domain ?? data.domain;
		const sort = params.sort ?? data.sort;
		// Het kaart-filter reist standaard mee met paginering en facetten;
		// een expliciete lege string (filter wissen) laat het weg.
		const card = params.card ?? data.card;
		if (domain) p.set('domain', domain);
		if (sort && sort !== 'recent') p.set('sort', sort);
		if (params.page && params.page > 1) p.set('page', String(params.page));
		if (card) p.set('card', card);
		const qs = p.toString();
		return qs ? `/decks?${qs}` : '/decks';
	}

	function formatDate(iso: string | null): string | null {
		return iso ? new Date(iso).toLocaleDateString('nl-NL') : null;
	}

	// Filters (domein + sortering) in de rechterrail (desktop) / bottom-sheet
	// (mobiel) — feed-patroon #214. Alleen het domein telt als actief filter;
	// sortering is een ordening, geen filter.
	const activeCount = $derived(data.domain ? 1 : 0);
	$effect(() => {
		shell.rail = { snippet: filters, kind: 'filters', count: activeCount, title: 'Filters' };
		return () => (shell.rail = null);
	});
</script>

<svelte:head>
	<title>Decks — Poracle</title>
	<meta
		name="description"
		content="Community-decks van Piltover Archive, doorzoekbaar met legaliteitscheck tegen de actuele sets en banlijst — met attributie en deep-link terug."
	/>
</svelte:head>

{#snippet filters()}
	<!-- GET-form: het kaart-filter reist mee als verborgen veld zodat filteren
	     een lopende kaart-selectie behoudt. -->
	<form method="GET" class="filter-form">
		{#if data.card}<input type="hidden" name="card" value={data.card} />{/if}
		<label>
			<span>Domein</span>
			<select name="domain" value={data.domain}>
				<option value="">Alle domeinen</option>
				{#each data.facets.domains as d (d)}<option value={d}>{d}</option>{/each}
			</select>
		</label>
		<label>
			<span>Sortering</span>
			<select name="sort" value={data.sort}>
				{#each SORTS as s (s.key)}<option value={s.key}>{s.label}</option>{/each}
			</select>
		</label>
		<div class="filter-actions">
			<a href={data.card ? `/decks?card=${encodeURIComponent(data.card)}` : '/decks'} class="link-btn">Reset</a>
			<button type="submit" onclick={() => (shell.sheetOpen = false)}>Toon decks</button>
		</div>
	</form>
{/snippet}

<main>
	<h1>Decks <span>van Piltover Archive</span></h1>
	<p class="subtitle">
		Wij bouwen geen eigen deckbuilder — dit spiegelt de publieke community-decks van
		<a href="https://piltoverarchive.com" target="_blank" rel="noopener">Piltover Archive</a>, met per
		deck een legaliteitscheck tegen de actuele sets en banlijst en een link terug naar de bron.
	</p>

	{#if cardFilter}
		<p class="card-filter">
			Gefilterd op kaart:
			<a href="/cards/{cardFilter.canonicalId}">{cardFilter.name ?? cardFilter.canonicalId}</a>
			<a class="clear" href={href({ page: 1, card: '' })}>filter wissen</a>
		</p>
	{/if}

	{#if data.domain}
		<div class="active-chips">
			<a class="active-chip" href={href({ domain: '', page: 1 })}>domein: {data.domain} ✕</a>
		</div>
	{/if}

	{#if data.error}
		<p class="warn">{data.error}</p>
	{:else if busy}
		<div class="loading"><span class="spin"></span> Decks laden…</div>
	{:else if data.result.items.length === 0}
		<p class="meta">
			Geen decks gevonden{cardFilter
				? ' met deze kaart'
				: data.domain
					? ' voor dit domein'
					: ''}.
		</p>
	{:else}
		<p class="meta count tnum">{data.result.total} decks · pagina {data.result.page} van {lastPage}</p>
		<div class="grid">
			{#each data.result.items as deck (deck.id)}
				<a class="card panel" href="/decks/{deck.id}">
					<header>
						<strong>{deck.name ?? '(naamloos deck)'}</strong>
						<span class="badge {deck.legality.status === 'legal'
							? 'ok-b'
							: deck.legality.status === 'illegal'
								? 'err'
								: 'warn-b'}">
							{LEGALITY_LABEL[deck.legality.status]}
						</span>
					</header>
					<p class="meta domains">{deck.domains.join(' / ') || '—'}</p>
					<p class="meta stats tnum">
						{deck.cardCount} kaarten · {deck.views} views · {deck.likes} likes
						{#if formatDate(deck.paUpdatedAt)}· bijgewerkt {formatDate(deck.paUpdatedAt)}{/if}
					</p>
				</a>
			{/each}
		</div>

		{#if lastPage > 1}
			<div class="pager">
				{#if data.page > 1}
					<a href={href({ page: data.page - 1 })}>← Vorige</a>
				{:else}<span></span>{/if}
				{#if data.page < lastPage}
					<a href={href({ page: data.page + 1 })}>Volgende →</a>
				{/if}
			</div>
		{/if}
	{/if}
</main>

<style>
	main {
		max-width: 1080px;
		margin: 0 auto;
		padding: 24px 20px;
	}
	h1 span {
		color: var(--accent);
		font-weight: 400;
		font-size: 1.05rem;
		margin-left: 4px;
	}
	.subtitle {
		color: var(--muted);
	}
	.subtitle a {
		color: var(--accent);
	}
	.card-filter {
		background: var(--surface);
		border: 1px solid var(--border);
		border-radius: 8px;
		padding: 8px 12px;
		margin: 14px 0 0;
		color: var(--muted);
		font-size: 0.9rem;
	}
	.card-filter a {
		color: var(--accent);
		text-decoration: none;
		font-weight: 600;
	}
	.card-filter a:hover {
		text-decoration: underline;
	}
	.card-filter .clear {
		color: var(--muted);
		font-weight: 400;
		margin-left: 8px;
	}
	.loading {
		display: flex;
		align-items: center;
		gap: 10px;
		color: var(--muted);
		padding: 30px 0;
	}
	.meta {
		color: var(--muted);
	}
	.count {
		font-size: 0.85rem;
		margin: 14px 0 10px;
	}
	.warn {
		color: var(--err);
	}
	.grid {
		display: grid;
		gap: 12px;
		grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
	}
	.card {
		padding: 14px 16px;
		display: flex;
		flex-direction: column;
		gap: 6px;
		color: inherit;
		text-decoration: none;
	}
	.card:hover {
		border-color: var(--border-strong);
	}
	.card header {
		display: flex;
		align-items: baseline;
		justify-content: space-between;
		gap: 8px;
	}
	.card header strong {
		overflow-wrap: anywhere;
	}
	.badge {
		flex-shrink: 0;
	}
	.domains {
		font-size: 0.9rem;
	}
	.stats {
		font-size: 0.8rem;
	}
	.pager {
		display: flex;
		justify-content: space-between;
		margin-top: 18px;
	}
	.pager a {
		color: var(--text);
		text-decoration: none;
		font-weight: 600;
	}
	.pager a:hover {
		color: var(--accent);
	}

	/* Actieve filters als verwijderbare chips (feed-patroon). */
	.active-chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 14px 0 0; }
	.active-chip {
		background: var(--surface-deep); color: var(--text); border: 1px solid var(--border);
		border-radius: 999px; padding: 4px 12px; font-size: 0.8rem; text-decoration: none;
	}
	.active-chip:hover { border-color: var(--border-strong); }

	/* Filter-form (rail + sheet) — zelfde vormtaal als /cards. */
	.filter-form { display: flex; flex-direction: column; gap: 12px; }
	.filter-form label { display: flex; flex-direction: column; gap: 4px; }
	.filter-form label span {
		font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em;
		color: var(--muted);
	}
	.filter-form select {
		width: 100%; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 9px 11px;
	}
	.filter-actions { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-top: 4px; }
	.filter-actions .link-btn { color: var(--muted); text-decoration: none; font-size: 0.85rem; padding: 6px 4px; }
	.filter-actions button {
		background: var(--accent); color: var(--accent-ink); border: 0; border-radius: 8px;
		padding: 9px 16px; font-weight: 700; cursor: pointer;
	}
</style>
