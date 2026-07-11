<script lang="ts">
	let { data } = $props();

	let q = $state('');

	interface Sec { code: string; preview: string; }
	interface Group {
		chapter: string;
		head: Sec | null;
		children: Sec[];
	}

	// Hoofdstukken zoals in het document: honderdtallen (000, 100, 200 …)
	// zijn hoofdstukkoppen; 001–056 en 103.1 vallen eronder.
	function groups(sections: Sec[]): Group[] {
		const map = new Map<number, Group>();
		for (const s of sections) {
			const top = parseInt(s.code, 10);
			if (Number.isNaN(top)) continue;
			const chap = Math.floor(top / 100) * 100;
			let g = map.get(chap);
			if (!g) {
				g = { chapter: String(chap).padStart(3, '0'), head: null, children: [] };
				map.set(chap, g);
			}
			if (top === chap && !s.code.includes('.')) g.head = s;
			else g.children.push(s);
		}
		return [...map.values()];
	}

	// Insprongniveau: 051 = sectie, 053.1 = subsectie, 601.2.d = sub-sub.
	function level(code: string): number {
		return code.split('.').length - 1;
	}

	const needle = $derived(q.trim().toLowerCase());
	function matches(s: Sec): boolean {
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
		<p class="meta">Nog geen regels geïndexeerd — draai de "Regels indexeren"-actie in het beheer.</p>
	{:else}
		<input type="search" placeholder="Zoek op §-nummer of tekst (bijv. 601 of 'deflect')" bind:value={q} />

		{#each data.toc as src (src.sourceId)}
			{@const visible = src.sections.filter(matches)}
			{#if visible.length}
				<h2>{src.sourceName} <span class="meta">({visible.length} secties)</span></h2>
				{#each groups(visible) as g (g.chapter)}
					<details open={needle !== ''}>
						<summary>
							<strong>§ {g.head?.code ?? g.chapter}</strong>
							{#if g.head}<span class="chapter-title">{g.head.preview}</span>{/if}
							<span class="meta count">({g.children.length})</span>
						</summary>
						<ul>
							{#if g.head}
								<li class="lvl-0">
									<a href="/rules/{encodeURIComponent(g.head.code)}?source={src.sourceId}">§ {g.head.code}</a>
									<span class="preview">{g.head.preview}…</span>
								</li>
							{/if}
							{#each g.children as s (s.code)}
								<li class="lvl-{Math.min(level(s.code), 2)}">
									<a href="/rules/{encodeURIComponent(s.code)}?source={src.sourceId}">§ {s.code}</a>
									<span class="preview">{s.preview}…</span>
								</li>
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
	h1 span { color: var(--accent); }
	.subtitle, .meta, .preview { color: var(--muted); }
	.preview { font-size: 0.85rem; }
	input {
		width: 100%; box-sizing: border-box; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 10px; padding: 10px 14px; margin: 10px 0 18px;
	}
	h2 { color: var(--accent); font-size: 1.05rem; margin: 22px 0 8px; }
	details {
		background: var(--surface); border: 1px solid var(--border); border-radius: 10px;
		padding: 8px 14px; margin-bottom: 8px;
	}
	summary { cursor: pointer; }
	.chapter-title { margin-left: 8px; font-weight: 600; }
	.count { margin-left: 6px; font-size: 0.8rem; }
	ul { list-style: none; margin: 8px 0 4px; padding: 0; }
	li { padding: 4px 0; border-top: 1px solid var(--border); }
	li.lvl-1 { padding-left: 26px; }
	li.lvl-2 { padding-left: 52px; }
	a { color: var(--text); text-decoration: none; font-weight: 600; }
	a:hover { color: var(--accent); }
	.warn { color: var(--err); }
</style>
