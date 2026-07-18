<script lang="ts">
	import { navigating } from '$app/state';
	import RbText from '$lib/RbText.svelte';
	import { domainColorVar } from '$lib/changeCard';
	import { useShell } from '$lib/shell.svelte';

	let { data } = $props();
	const shell = useShell();

	const busy = $derived(navigating.to?.url.pathname === '/cards');

	// Actieve facet-filters (los van de zoekterm q).
	const FILTER_LABELS: Record<string, string> = {
		domain: 'Domain',
		type: 'Type',
		set: 'Set',
		rarity: 'Rarity',
		mechanic: 'Mechaniek',
		maxEnergy: 'Max energy'
	};
	const activeFilters = $derived(
		Object.entries(data.filters).filter(([, v]) => v) as [string, string][]
	);

	/** URL naar dezelfde zoekopdracht met één filter (of alles) gewist. */
	function urlWithout(key?: string): string {
		const p = new URLSearchParams();
		if (data.q) p.set('q', data.q);
		for (const [k, v] of Object.entries(data.filters)) if (v && k !== key) p.set(k, v);
		const qs = p.toString();
		return qs ? `/cards?${qs}` : '/cards';
	}

	function setLabel(id: string): string {
		return data.facets.sets.find((s) => s.id === id)?.label ?? id;
	}
	function chipLabel(key: string, value: string): string {
		const l = FILTER_LABELS[key] ?? key;
		return key === 'set' ? `${l}: ${setLabel(value)}` : `${l}: ${value}`;
	}

	// Filters wonen in de rechterrail (desktop) / bottom-sheet (mobiel).
	$effect(() => {
		shell.rail = {
			snippet: filters,
			kind: 'filters',
			count: activeFilters.length,
			title: 'Filters'
		};
		return () => (shell.rail = null);
	});
</script>

<svelte:head><title>Kaarten — Poracle</title></svelte:head>

{#snippet filters()}
	<!-- GET-form: de zoekterm reist mee als verborgen veld zodat filteren de
	     semantische zoekopdracht behoudt. -->
	<form method="GET" class="filter-form">
		<input type="hidden" name="q" value={data.q} />
		<label>
			<span>Domain</span>
			<select name="domain" value={data.filters.domain}>
				<option value="">Alle domeinen</option>
				{#each data.facets.domains as d (d)}<option value={d}>{d}</option>{/each}
			</select>
		</label>
		<label>
			<span>Type</span>
			<select name="type" value={data.filters.type}>
				<option value="">Alle types</option>
				{#each data.facets.types as t (t)}<option value={t}>{t}</option>{/each}
			</select>
		</label>
		<label>
			<span>Set</span>
			<select name="set" value={data.filters.set}>
				<option value="">Alle sets</option>
				{#each data.facets.sets as s (s.id)}<option value={s.id}>{s.label}</option>{/each}
			</select>
		</label>
		<label>
			<span>Rarity</span>
			<select name="rarity" value={data.filters.rarity}>
				<option value="">Alle rarities</option>
				{#each data.facets.rarities as r (r)}<option value={r}>{r}</option>{/each}
			</select>
		</label>
		<label>
			<span>Mechaniek</span>
			<select name="mechanic" value={data.filters.mechanic}>
				<option value="">Alle mechanieken</option>
				{#each data.facets.mechanics as m (m)}<option value={m}>{m}</option>{/each}
			</select>
		</label>
		<label>
			<span>Max energy</span>
			<input type="number" name="maxEnergy" value={data.filters.maxEnergy} min="0" max="12" placeholder="—" />
		</label>
		<div class="filter-actions">
			<a href={urlWithout()} class="link-btn">Reset</a>
			<button type="submit" onclick={() => (shell.sheetOpen = false)}>Toon kaarten</button>
		</div>
	</form>
{/snippet}

<main>
	<h1>Kaarten <span>zoeken</span></h1>
	<p class="subtitle">
		Typ wat een kaart <em>doet</em> voor semantisch zoeken, of laat leeg en filter om te bladeren.
	</p>

	<form method="GET" class="search">
		<input type="search" name="q" value={data.q} placeholder="Bijv.: cheap answers to hidden units" />
		<!-- Actieve facetten reizen mee met een nieuwe zoekterm. -->
		{#each activeFilters as [k, v] (k)}<input type="hidden" name={k} value={v} />{/each}
		<button type="submit" disabled={busy}>{busy ? 'Zoeken…' : 'Zoek'}</button>
	</form>

	{#if activeFilters.length}
		<div class="active-chips">
			{#each activeFilters as [k, v] (k)}
				<a class="active-chip" href={urlWithout(k)}>{chipLabel(k, v)} ✕</a>
			{/each}
			<a class="active-chip clear" href={urlWithout()}>Wis alles</a>
		</div>
	{/if}

	{#if busy}
		<div class="loading">
			<span class="spinner"></span>
			{data.q ? 'Semantisch zoeken (embeddings)…' : 'Kaarten laden…'}
		</div>
	{:else if data.error}
		<p class="warn">{data.error}</p>
	{:else if data.results.length === 0}
		<p class="meta">Geen resultaten.</p>
	{:else}
		<p class="meta">
			{data.results.length} kaarten
			{data.mode === 'semantic' ? '· gesorteerd op relevantie' : '· alfabetisch'}
		</p>
		<div class="grid">
			{#each data.results as c (c.riftboundId)}
				<a class="card" href="/cards/{c.riftboundId}" style="--card-dom: {domainColorVar(c.domains[0])}">
					{#if c.imageUrl}
						<img src={c.imageUrl} alt={c.name} loading="lazy" />
					{/if}
					<div class="body">
						<strong>{c.name}</strong>
						{#if c.variants}<span class="variants">+{c.variants} versies</span>{/if}
						{#if c.legality === 'upcoming'}<span class="soon">Nog niet legaal</span>{/if}
						<p class="meta">
							{[c.supertype, c.type].filter(Boolean).join(' ')}
							· {c.domains.join('/') || '—'}
							{#if c.energy !== null}· E{c.energy}{/if}
							{#if c.might !== null}· M{c.might}{/if}
						</p>
						{#if c.textPlain}<p class="text"><RbText text={c.textPlain} /></p>{/if}
					</div>
				</a>
			{/each}
		</div>
	{/if}
</main>

<style>
	main { max-width: 1080px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: var(--accent); }
	.subtitle, .meta { color: var(--muted); }
	.search { display: flex; gap: 8px; margin-bottom: 14px; }
	.search input[type='search'] {
		flex: 1; min-width: 0; background: var(--surface); color: var(--text);
		border: 1px solid var(--border); border-radius: 9px; padding: 10px 12px;
	}
	.search button {
		background: var(--accent); color: var(--accent-ink); border: 0; border-radius: 9px;
		padding: 0 18px; font-weight: 700; cursor: pointer;
	}
	.search button:disabled { opacity: 0.6; cursor: wait; }

	.active-chips { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 14px; }
	.active-chip {
		background: var(--surface-deep); color: var(--text); border: 1px solid var(--border);
		border-radius: 999px; padding: 4px 12px; font-size: 0.8rem; text-decoration: none;
	}
	.active-chip:hover { border-color: var(--border-strong); }
	.active-chip.clear { color: var(--muted); border-style: dashed; }

	.loading { display: flex; align-items: center; gap: 10px; color: var(--muted); padding: 30px 0; }
	.spinner {
		width: 18px; height: 18px; border-radius: 50%;
		border: 2px solid var(--border); border-top-color: var(--accent);
		animation: spin 0.8s linear infinite;
	}
	@keyframes spin { to { transform: rotate(360deg); } }

	.grid {
		display: grid; gap: 14px;
		grid-template-columns: repeat(auto-fill, minmax(230px, 1fr));
	}
	.card {
		background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius-lg);
		overflow: hidden; display: flex; flex-direction: column;
		color: inherit; text-decoration: none; box-shadow: var(--shadow-card);
		border-top: 3px solid var(--card-dom);
	}
	.card:hover { border-color: var(--border-strong); }
	.card img { width: 100%; aspect-ratio: 744 / 1039; object-fit: cover; }
	.card .body { padding: 10px 12px; }
	.text { font-size: 0.85rem; color: var(--muted); }
	.variants {
		display: inline-block; margin-left: 6px; font-size: 0.7rem; color: var(--muted);
		border: 1px solid var(--border); border-radius: 999px; padding: 1px 7px;
	}
	.soon {
		display: inline-block; margin-left: 6px; font-size: 0.7rem;
		background: var(--warn-soft); color: var(--warn);
		border: 1px solid var(--warn); border-radius: 999px; padding: 1px 7px;
	}
	.warn { color: var(--err); }

	/* Filter-form (rail + sheet) */
	.filter-form { display: flex; flex-direction: column; gap: 12px; }
	.filter-form label { display: flex; flex-direction: column; gap: 4px; }
	.filter-form label span {
		font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em;
		color: var(--muted);
	}
	.filter-form select,
	.filter-form input {
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
