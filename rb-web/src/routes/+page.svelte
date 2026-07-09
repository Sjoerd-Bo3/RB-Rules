<script lang="ts">
	let { data } = $props();
</script>

<svelte:head>
	<title>Riftbound Rules Companion</title>
</svelte:head>

<main>
	<h1>Riftbound <span>Rules Companion</span></h1>
	<p class="subtitle">v2 — semantic engine (.NET + SvelteKit)</p>

	{#if data.apiDown}
		<p class="warn">rb-api is niet bereikbaar.</p>
	{:else if data.changes.length === 0}
		<p class="empty">Nog geen wijzigingen geregistreerd.</p>
	{:else}
		{#each data.changes as c (c.id)}
			<article class="card">
				<header>
					<span class="badge {c.severity}">{c.severity}</span>
					<strong>{c.changeType}</strong>
					<span class="meta">{new Date(c.detectedAt).toLocaleString('nl-NL')}</span>
				</header>
				{#if c.summary}<p>{c.summary}</p>{/if}
				{#if c.meaning}<p class="meaning">{c.meaning}</p>{/if}
			</article>
		{/each}
	{/if}
</main>

<style>
	:global(body) {
		margin: 0;
		background: #0e1726;
		color: #e7eefc;
		font: 16px/1.5 system-ui, sans-serif;
	}
	main { max-width: 860px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: #d98a4e; }
	.subtitle, .meta, .empty { color: #9fb0cc; }
	.card {
		background: #16233b;
		border: 1px solid #243551;
		border-radius: 12px;
		padding: 14px 16px;
		margin-bottom: 12px;
	}
	.card header { display: flex; gap: 10px; align-items: center; }
	.badge {
		font-size: 0.72rem;
		text-transform: uppercase;
		padding: 2px 8px;
		border-radius: 999px;
		font-weight: 700;
		background: #24355133;
	}
	.badge.high { background: #e5484d2e; color: #ff8b8e; }
	.badge.medium { background: #e0a32e2e; color: #f3c469; }
	.warn { color: #ff8b8e; }
	.meaning { color: #d98a4e; }
</style>
