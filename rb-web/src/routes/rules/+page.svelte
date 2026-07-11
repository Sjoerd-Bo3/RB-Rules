<script lang="ts">
	import { navigating } from '$app/state';

	let { data } = $props();

	// Schrijfbaar afgeleid (Svelte 5.25+): typen filtert de boom live, en bij
	// navigatie (form GET → nieuwe ?q=) reset de invoer naar de URL-waarde —
	// het component leeft door bij client-side route-hergebruik.
	let q = $derived(data.q);

	const busy = $derived(navigating.to?.url.pathname === '/rules');

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

	function sourceName(id: string): string {
		return data.toc.find((s) => s.sourceId === id)?.sourceName ?? id;
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
		<form method="GET" class="search">
			<input
				type="search"
				name="q"
				bind:value={q}
				placeholder="Zoek op §-nummer of betekenis (bijv. 601 of 'gear kill')"
			/>
			<button type="submit" disabled={busy}>{busy ? 'Zoeken…' : 'Zoek'}</button>
		</form>

		{#if data.searchFailed}
			<p class="warn">Zoeken is even niet beschikbaar — de boom hieronder werkt gewoon.</p>
		{:else if data.results}
			<section class="results">
				<h2>Zoekresultaten <span class="meta">({data.results.length})</span></h2>
				{#if data.results.length === 0}
					<p class="meta">Geen secties gevonden voor "{data.q}" — er wordt ook op betekenis gezocht, probeer een omschrijving.</p>
				{:else}
					<ul class="hits">
						{#each data.results as hit (hit.id)}
							<li>
								<a class="hit" href="/rules/{encodeURIComponent(hit.sectionCode)}?source={hit.sourceId}">
									<span class="hit-head">
										<strong>§ {hit.sectionCode}</strong>
										<span class="src">{sourceName(hit.sourceId)}</span>
									</span>
									<span class="snippet">{hit.snippet}</span>
								</a>
								{#if hit.fileUrl}
									<a class="pdf" href="{hit.fileUrl}{hit.page ? `#page=${hit.page}` : ''}" target="_blank" rel="noopener">
										PDF{hit.page ? ` p. ${hit.page}` : ''}
									</a>
								{/if}
							</li>
						{/each}
					</ul>
				{/if}
			</section>
		{/if}

		{#each data.toc as src (src.sourceId)}
			{@const visible = src.sections.filter(matches)}
			{#if visible.length}
				<h2>{src.sourceName} <span class="meta">({visible.length} secties)</span></h2>
				{#each groups(visible) as g (g.chapter)}
					<details open={needle !== ''}>
						<summary>
							<strong>§ {g.head?.code ?? g.chapter}</strong>
							{#if g.head}
								<a class="chapter-title" href="/rules/{encodeURIComponent(g.head.code)}?source={src.sourceId}">{g.head.preview}</a>
							{/if}
							<span class="meta count">({g.children.length})</span>
						</summary>
						<ul>
							<!-- De hoofdstukkop zelf staat al in de summary — niet herhalen. -->
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
	.search { display: flex; gap: 8px; margin: 10px 0 18px; }
	input {
		flex: 1; min-width: 0; box-sizing: border-box; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 10px; padding: 10px 14px; font-size: 1rem;
	}
	button {
		background: var(--accent); color: var(--accent-ink); border: 0; border-radius: 10px;
		padding: 10px 16px; font-weight: 600; cursor: pointer; flex-shrink: 0;
	}
	button:disabled { opacity: 0.6; cursor: wait; }
	h2 { color: var(--accent); font-size: 1.05rem; margin: 22px 0 8px; }
	.results { margin-bottom: 22px; }
	.results h2 { margin-top: 0; }
	.hits { list-style: none; margin: 0; padding: 0; display: grid; gap: 8px; }
	.hits li {
		display: flex; align-items: center; gap: 10px;
		background: var(--surface); border: 1px solid var(--border);
		border-radius: 10px; padding: 10px 14px;
	}
	.hit { flex: 1; min-width: 0; display: block; }
	.hit-head { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; }
	.src { color: var(--muted); font-size: 0.8rem; font-weight: 400; }
	.snippet {
		display: block; color: var(--muted); font-size: 0.85rem; font-weight: 400;
		margin-top: 2px; overflow-wrap: anywhere;
	}
	.pdf { flex-shrink: 0; color: var(--accent); font-size: 0.85rem; }
	details {
		background: var(--surface); border: 1px solid var(--border); border-radius: 10px;
		padding: 8px 14px; margin-bottom: 8px;
	}
	summary { cursor: pointer; }
	.chapter-title { margin-left: 8px; font-weight: 600; color: var(--text); text-decoration: none; }
	.chapter-title:hover { color: var(--accent); }
	.count { margin-left: 6px; font-size: 0.8rem; }
	details ul { list-style: none; margin: 8px 0 4px; padding: 0; }
	details li { padding: 4px 0; border-top: 1px solid var(--border); }
	li.lvl-1 { padding-left: 26px; }
	li.lvl-2 { padding-left: 52px; }
	a { color: var(--text); text-decoration: none; font-weight: 600; }
	a:hover { color: var(--accent); }
	.warn { color: var(--err); }
</style>
