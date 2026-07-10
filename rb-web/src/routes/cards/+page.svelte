<script lang="ts">
	let { data } = $props();

	const DOMAINS = ['', 'Fury', 'Calm', 'Mind', 'Body', 'Order', 'Chaos', 'Colorless'];
</script>

<svelte:head><title>Kaarten zoeken — RB Rules</title></svelte:head>

<main>
	<h1>Semantisch <span>kaartzoeken</span></h1>
	<p class="subtitle">
		Zoek op wat een kaart <em>doet</em> — bijv. “goedkope antwoorden op Hidden units”.
	</p>

	<form method="GET" class="search">
		<input
			type="search"
			name="q"
			value={data.q}
			placeholder="Bijv.: kaarten die units verplaatsen tussen battlefields"
		/>
		<select name="domain" value={data.domain}>
			{#each DOMAINS as d (d)}
				<option value={d}>{d || 'Alle domains'}</option>
			{/each}
		</select>
		<input
			type="number"
			name="maxEnergy"
			value={data.maxEnergy}
			placeholder="Max energy"
			min="0"
			max="12"
		/>
		<button type="submit">Zoek</button>
	</form>

	{#if data.error}
		<p class="warn">{data.error}</p>
	{:else if data.results.length === 0}
		<p class="meta">Geen resultaten.</p>
	{:else}
		{#if data.mode === 'browse'}
			<p class="meta">Bladeren (alfabetisch) — typ een zoekterm voor semantisch zoeken.</p>
		{/if}
		<div class="grid">
			{#each data.results as c (c.riftboundId)}
				<a class="card" href="/cards/{c.riftboundId}">
					{#if c.imageUrl}
						<img src={c.imageUrl} alt={c.name} loading="lazy" />
					{/if}
					<div class="body">
						<strong>{c.name}</strong>
						<p class="meta">
							{[c.supertype, c.type].filter(Boolean).join(' ')}
							· {c.domains.join('/') || '—'}
							{#if c.energy !== null}· ⚡{c.energy}{/if}
							{#if c.might !== null}· ⚔{c.might}{/if}
						</p>
						{#if c.textPlain}<p class="text">{c.textPlain}</p>{/if}
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
	.search { display: flex; gap: 8px; flex-wrap: wrap; margin-bottom: 20px; }
	.search input[type='search'] { flex: 1; min-width: 260px; }
	input, select {
		background: #0b1322; color: #e7eefc;
		border: 1px solid #243551; border-radius: 8px; padding: 9px 11px;
	}
	input[type='number'] { width: 110px; }
	button {
		background: #d98a4e; color: #1a1206; border: 0; border-radius: 8px;
		padding: 9px 16px; font-weight: 600; cursor: pointer;
	}
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
	.warn { color: #ff8b8e; }
</style>
