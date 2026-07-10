<script lang="ts">
	let { data } = $props();
	const c = $derived(data.card);
</script>

<svelte:head><title>{c.name} — RB Rules</title></svelte:head>

<main>
	<a href="/cards" class="back">← Kaarten</a>

	<div class="detail">
		{#if c.imageUrl}
			<img class="art" src={c.imageUrl} alt={c.name} />
		{/if}
		<div class="info">
			<h1>
				{c.name}
				{#if c.banned}<span class="banned">⚠ GEBANNED</span>{/if}
			</h1>
			<p class="meta">
				{[c.supertype, c.type].filter(Boolean).join(' ')}
				· {c.rarity ?? '—'}
				· {c.setLabel ?? c.setId ?? '?'}{c.collectorNumber ? ` #${c.collectorNumber}` : ''}
			</p>

			<p class="stats">
				{#if c.domains.length}<span class="chip domain">{c.domains.join(' / ')}</span>{/if}
				{#if c.energy !== null}<span class="chip">⚡ {c.energy}</span>{/if}
				{#if c.might !== null}<span class="chip">⚔ {c.might}</span>{/if}
				{#if c.power !== null}<span class="chip">◆ {c.power}</span>{/if}
			</p>

			{#if c.errataText}
				<section>
					<h2>Actuele tekst (errata)</h2>
					<p class="oracle errata">{c.errataText}</p>
					{#if c.textPlain}
						<details>
							<summary class="meta">Gedrukte tekst (achterhaald)</summary>
							<p class="oracle printed">{c.textPlain}</p>
						</details>
					{/if}
				</section>
			{:else if c.textPlain}
				<section>
					<h2>Kaarttekst</h2>
					<p class="oracle">{c.textPlain}</p>
				</section>
			{/if}

			{#if c.mechanics?.length}
				<section>
					<h2>Mechanieken</h2>
					<p>{#each c.mechanics as m (m)}<span class="chip mech">{m}</span>{/each}</p>
				</section>
			{/if}

			{#if c.triggers?.length || c.effects?.length}
				<section>
					<h2>Triggers & effecten</h2>
					<p class="meta">
						{#if c.triggers?.length}Triggers: {c.triggers.join(' · ')}{/if}
						{#if c.triggers?.length && c.effects?.length}<br />{/if}
						{#if c.effects?.length}Effecten: {c.effects.join(' · ')}{/if}
					</p>
				</section>
			{/if}

			{#if c.tags.length}
				<section>
					<h2>Tags</h2>
					<p>{#each c.tags as t (t)}<span class="chip tag">{t}</span>{/each}</p>
				</section>
			{/if}
		</div>
	</div>

	{#if data.interactions.length}
		<section>
			<h2>Interacties (geverifieerd)</h2>
			{#each data.interactions as x (x.otherId + x.kind)}
				<div class="interaction">
					<a href="/cards/{x.otherId}"><strong>{x.otherName}</strong></a>
					<span class="chip kind-{x.kind}">{x.kind}</span>
					<p class="meta">{x.explanation}</p>
				</div>
			{/each}
		</section>
	{/if}

	{#if data.similar.length}
		<section>
			<h2>Vergelijkbare kaarten</h2>
			<div class="grid">
				{#each data.similar as s (s.riftboundId)}
					<a class="mini" href="/cards/{s.riftboundId}">
						{#if s.imageUrl}<img src={s.imageUrl} alt={s.name} loading="lazy" />{/if}
						<span>{s.name}</span>
					</a>
				{/each}
			</div>
		</section>
	{/if}
</main>

<style>
	main { max-width: 1080px; margin: 0 auto; padding: 24px 20px; }
	.back { color: #9fb0cc; text-decoration: none; }
	.detail { display: grid; grid-template-columns: minmax(220px, 320px) 1fr; gap: 24px; margin-top: 16px; }
	@media (max-width: 640px) { .detail { grid-template-columns: 1fr; } }
	.art { width: 100%; border-radius: 14px; border: 1px solid #243551; }
	h1 { margin: 0 0 4px; }
	h2 { font-size: 1rem; color: #d98a4e; margin: 18px 0 6px; }
	.meta { color: #9fb0cc; }
	.oracle { background: #16233b; border: 1px solid #243551; border-radius: 10px; padding: 12px 14px; }
	.chip {
		display: inline-block; background: #24355166; border: 1px solid #243551;
		border-radius: 999px; padding: 2px 10px; margin: 0 6px 6px 0; font-size: 0.85rem;
	}
	.chip.domain { color: #f3c469; }
	.chip.mech { color: #7fd1a8; }
	.chip.tag { color: #9fb0cc; }
	.banned {
		font-size: 0.8rem; vertical-align: middle; margin-left: 8px;
		background: #e5484d2e; color: #ff8b8e; border-radius: 999px; padding: 3px 10px;
	}
	.oracle.errata { border-color: #d98a4e; }
	.oracle.printed { opacity: 0.6; }
	.interaction { border-bottom: 1px solid #243551; padding: 8px 0; }
	.interaction a { color: #e7eefc; }
	.chip.kind-combo { color: #7fd1a8; }
	.chip.kind-counter { color: #ff8b8e; }
	.chip.kind-synergy { color: #f3c469; }
	.chip.kind-nonbo { color: #9fb0cc; }
	.grid { display: grid; gap: 12px; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); }
	.mini { color: inherit; text-decoration: none; font-size: 0.85rem; }
	.mini img { width: 100%; border-radius: 10px; border: 1px solid #243551; }
</style>
