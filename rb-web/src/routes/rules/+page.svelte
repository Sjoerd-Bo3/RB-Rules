<script lang="ts">
	let { data } = $props();

	let q = $state('');

	interface Group {
		top: string;
		head: { code: string; preview: string } | null;
		children: { code: string; preview: string }[];
	}

	// Groepeer per top-level nummer (601.2.d → 601); de top-sectie is de kop.
	function groups(sections: { code: string; preview: string }[]): Group[] {
		const map = new Map<string, Group>();
		for (const s of sections) {
			const top = s.code.split('.')[0];
			let g = map.get(top);
			if (!g) {
				g = { top, head: null, children: [] };
				map.set(top, g);
			}
			if (s.code === top) g.head = s;
			else g.children.push(s);
		}
		return [...map.values()];
	}

	const needle = $derived(q.trim().toLowerCase());
	function matches(s: { code: string; preview: string }): boolean {
		return !needle || s.code.toLowerCase().includes(needle) || s.preview.toLowerCase().includes(needle);
	}
</script>

<svelte:head><title>Regels — RB Rules</title></svelte:head>

<main>
	<h1>Officiële <span>regels</span></h1>
	<p class="subtitle">Blader door alle secties van de Core en Tournament Rules — elke § heeft een deelbare link.</p>

	{#if data.apiDown}
		<p class="warn">rb-api is niet bereikbaar.</p>
	{:else if data.toc.length === 0}
		<p class="meta">Nog geen regels geïndexeerd — draai de "Regels-index"-job in het beheer.</p>
	{:else}
		<input type="search" placeholder="Zoek op §-nummer of tekst (bijv. 601 of 'deflect')" bind:value={q} />

		{#each data.toc as src (src.sourceId)}
			{@const visible = src.sections.filter(matches)}
			{#if visible.length}
				<h2>{src.sourceName} <span class="meta">({visible.length} secties)</span></h2>
				{#each groups(visible) as g (g.top)}
					<details open={needle !== ''}>
						<summary>
							<strong>§ {g.top}</strong>
							{#if g.head}<span class="preview">{g.head.preview}…</span>{/if}
						</summary>
						<ul>
							{#if g.head}
								<li><a href="/rules/{encodeURIComponent(g.head.code)}?source={src.sourceId}">§ {g.head.code}</a> <span class="preview">{g.head.preview}…</span></li>
							{/if}
							{#each g.children as s (s.code)}
								<li><a href="/rules/{encodeURIComponent(s.code)}?source={src.sourceId}">§ {s.code}</a> <span class="preview">{s.preview}…</span></li>
							{/each}
						</ul>
					</details>
				{/each}
			{/if}
		{/each}
	{/if}
</main>

<style>
	main { max-width: 860px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: #d98a4e; }
	.subtitle, .meta, .preview { color: #9fb0cc; }
	.preview { font-size: 0.85rem; }
	input {
		width: 100%; box-sizing: border-box; background: #0b1322; color: #e7eefc;
		border: 1px solid #243551; border-radius: 10px; padding: 10px 14px; margin: 10px 0 18px;
	}
	h2 { color: #d98a4e; font-size: 1.05rem; margin: 22px 0 8px; }
	details {
		background: #16233b; border: 1px solid #243551; border-radius: 10px;
		padding: 8px 14px; margin-bottom: 8px;
	}
	summary { cursor: pointer; }
	summary .preview { margin-left: 8px; }
	ul { list-style: none; margin: 8px 0 4px; padding: 0; }
	li { padding: 4px 0; border-top: 1px solid #24355166; }
	a { color: #e7eefc; text-decoration: none; font-weight: 600; }
	a:hover { color: #d98a4e; }
	.warn { color: #ff8b8e; }
</style>
