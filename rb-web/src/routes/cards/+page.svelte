<script lang="ts">
	import { navigating } from '$app/state';
	import RbText from '$lib/RbText.svelte';

	let { data } = $props();

	const busy = $derived(navigating.to?.url.pathname === '/cards');
</script>

<svelte:head><title>Kaarten — RB Rules</title></svelte:head>

<main>
	<h1>Kaarten <span>zoeken</span></h1>
	<p class="subtitle">
		Typ wat een kaart <em>doet</em> voor semantisch zoeken, of laat leeg en filter om te bladeren.
	</p>

	<form method="GET" class="search">
		<input
			type="search"
			name="q"
			value={data.q}
			placeholder="Bijv.: cheap answers to hidden units"
		/>
		<div class="filters">
			<select name="domain" value={data.filters.domain}>
				<option value="">Domain</option>
				{#each data.facets.domains as d (d)}<option value={d}>{d}</option>{/each}
			</select>
			<select name="type" value={data.filters.type}>
				<option value="">Type</option>
				{#each data.facets.types as t (t)}<option value={t}>{t}</option>{/each}
			</select>
			<select name="set" value={data.filters.set}>
				<option value="">Set</option>
				{#each data.facets.sets as s (s.id)}<option value={s.id}>{s.label}</option>{/each}
			</select>
			<select name="rarity" value={data.filters.rarity}>
				<option value="">Rarity</option>
				{#each data.facets.rarities as r (r)}<option value={r}>{r}</option>{/each}
			</select>
			<select name="mechanic" value={data.filters.mechanic}>
				<option value="">Mechaniek</option>
				{#each data.facets.mechanics as m (m)}<option value={m}>{m}</option>{/each}
			</select>
			<input
				type="number"
				name="maxEnergy"
				value={data.filters.maxEnergy}
				placeholder="Max energy"
				min="0"
				max="12"
			/>
			<button type="submit" disabled={busy}>
				{busy ? 'Zoeken…' : 'Zoek'}
			</button>
			<a href="/cards" class="reset">Reset</a>
		</div>
	</form>

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
				<a class="card" href="/cards/{c.riftboundId}">
					{#if c.imageUrl}
						<img src={c.imageUrl} alt={c.name} loading="lazy" />
					{/if}
					<div class="body">
						<strong>{c.name}</strong>
						{#if c.variants}<span class="variants">+{c.variants} versies</span>{/if}
						<!-- Set-legaliteit (#22): in de lijst alleen de actionable
						     waarschuwing; "legaal"/"datum onbekend" zou hier ruis zijn. -->
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
	h1 span { color: #d98a4e; }
	.subtitle, .meta { color: #9fb0cc; }
	.search { margin-bottom: 20px; }
	.search input[type='search'] { width: 100%; margin-bottom: 8px; }
	.filters { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
	input, select {
		background: #0b1322; color: #e7eefc;
		border: 1px solid #243551; border-radius: 8px; padding: 9px 11px;
	}
	/* Breed genoeg voor de placeholder "Max energy" bij 16px op mobiel. */
	input[type='number'] { width: 116px; }
	button {
		background: #d98a4e; color: #1a1206; border: 0; border-radius: 8px;
		padding: 9px 16px; font-weight: 600; cursor: pointer;
	}
	button:disabled { opacity: 0.6; cursor: wait; }
	.reset { color: #9fb0cc; font-size: 0.85rem; }
	.loading {
		display: flex; align-items: center; gap: 10px;
		color: #9fb0cc; padding: 30px 0;
	}
	.spinner {
		width: 18px; height: 18px; border-radius: 50%;
		border: 2px solid #243551; border-top-color: #d98a4e;
		animation: spin 0.8s linear infinite;
	}
	@keyframes spin { to { transform: rotate(360deg); } }
	.grid {
		display: grid; gap: 14px;
		grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
	}
	.card {
		background: #16233b; border: 1px solid #243551; border-radius: 12px;
		overflow: hidden; display: flex; flex-direction: column;
		color: inherit; text-decoration: none;
	}
	.card:hover { border-color: #d98a4e; }
	.card img { width: 100%; aspect-ratio: 744 / 1039; object-fit: cover; }
	.card .body { padding: 10px 12px; }
	.text { font-size: 0.85rem; color: #cdd9ef; }
	.variants {
		display: inline-block; margin-left: 6px; font-size: 0.7rem; color: #9fb0cc;
		border: 1px solid #243551; border-radius: 999px; padding: 1px 7px;
	}
	.soon {
		display: inline-block; margin-left: 6px; font-size: 0.7rem;
		background: var(--warn-soft); color: var(--warn);
		border: 1px solid var(--warn); border-radius: 999px; padding: 1px 7px;
	}
	.warn { color: #ff8b8e; }
</style>
