<script lang="ts">
	let { data } = $props();
	const s = $derived(data.section);

	// Broodkruimel: 601.2.d → [601, 601.2, 601.2.d]
	const crumbs = $derived.by(() => {
		const parts = s.code.split('.');
		return parts.map((_, i) => parts.slice(0, i + 1).join('.'));
	});

	let copied = $state(false);
	async function copyLink() {
		try {
			await navigator.clipboard.writeText(location.href);
			copied = true;
			setTimeout(() => (copied = false), 1500);
		} catch {
			/* clipboard niet beschikbaar */
		}
	}
</script>

<svelte:head><title>§ {s.code} — {s.sourceName} — RB Rules</title></svelte:head>

<main>
	<nav class="crumbs">
		<a href="/rules">Regels</a>
		{#each crumbs as c, i (c)}
			<span>›</span>
			{#if i === crumbs.length - 1}
				<strong>§ {c}</strong>
			{:else}
				<a href="/rules/{encodeURIComponent(c)}?source={s.sourceId}">§ {c}</a>
			{/if}
		{/each}
	</nav>

	<h1>§ {s.code} <span class="src">{s.sourceName}</span></h1>

	{#if s.parents.length}
		<!-- Bovenliggende regels als context boven de subregel -->
		<div class="parents">
			{#each s.parents as par (par.code)}
				<p class="parent">
					<a href="/rules/{encodeURIComponent(par.code)}?source={s.sourceId}">§ {par.code}</a>
					{par.text}
				</p>
			{/each}
		</div>
	{/if}

	<article class="card">
		<p class="text">{s.text}</p>
	</article>

	<div class="actions">
		<button onclick={copyLink}>{copied ? 'Gekopieerd' : 'Kopieer permalink'}</button>
		{#if s.pdfUrl}
			<a class="ext" href="{s.pdfUrl}{s.page ? `#page=${s.page}` : ''}" target="_blank" rel="noopener">
				Officiële PDF{s.page ? ` — pagina ${s.page}` : ''}
			</a>
		{:else}
			<a class="ext" href={s.sourceUrl} target="_blank" rel="noopener">Officiële bron</a>
		{/if}
	</div>

	<div class="pager">
		{#if s.prev}
			<a href="/rules/{encodeURIComponent(s.prev)}?source={s.sourceId}">← § {s.prev}</a>
		{:else}<span></span>{/if}
		{#if s.next}
			<a href="/rules/{encodeURIComponent(s.next)}?source={s.sourceId}">§ {s.next} →</a>
		{/if}
	</div>
</main>

<style>
	main { max-width: 860px; margin: 0 auto; padding: 24px 20px; }
	.crumbs { color: #9fb0cc; display: flex; gap: 8px; flex-wrap: wrap; font-size: 0.9rem; }
	.crumbs a { color: #9fb0cc; text-decoration: none; }
	.crumbs a:hover { color: #d98a4e; }
	h1 { margin: 10px 0 14px; }
	.src { color: #d98a4e; font-size: 1rem; font-weight: 600; margin-left: 10px; }
	.parents {
		border-left: 2px solid var(--border);
		padding-left: 12px; margin-bottom: 12px;
	}
	.parent { margin: 6px 0; color: var(--muted); font-size: 0.9rem; }
	.parent a { color: var(--muted); font-weight: 700; text-decoration: none; }
	.parent a:hover { color: var(--accent); }
	.card {
		background: var(--surface); border: 1px solid var(--border);
		border-radius: 12px; padding: 18px 20px;
	}
	.text { white-space: pre-wrap; margin: 0; line-height: 1.65; }
	/* Wrap op smal: knop + lange PDF-link passen niet altijd naast elkaar. */
	.actions { display: flex; gap: 8px 12px; align-items: center; flex-wrap: wrap; margin: 14px 0; }
	button {
		background: #d98a4e; color: #1a1206; border: 0; border-radius: 8px;
		padding: 8px 14px; font-weight: 600; cursor: pointer;
	}
	.ext { color: #9fb0cc; }
	.pager { display: flex; justify-content: space-between; margin-top: 18px; }
	.pager a { color: #e7eefc; text-decoration: none; font-weight: 600; }
	.pager a:hover { color: #d98a4e; }
</style>
