<script lang="ts">
	let { data } = $props();

	// Radiale layout: kaart centraal, mechanieken op ring 1, gedeelde kaarten
	// op ring 2, geverifieerde interacties onderaan ring 2. Deterministisch en
	// leesbaar — geen physics nodig.
	const W = 900;
	const H = 640;
	const CX = W / 2;
	const CY = H / 2;
	const R1 = 150;
	const R2 = 265;

	interface Node {
		id: string;
		label: string;
		kind: 'mechanic' | 'card' | 'interaction';
		sub?: string;
		x: number;
		y: number;
	}
	interface Edge { x1: number; y1: number; x2: number; y2: number; dashed?: boolean; }

	const layout = $derived.by(() => {
		const g = data.graph;
		if (!g) return { nodes: [] as Node[], edges: [] as Edge[] };
		const nodes: Node[] = [];
		const edges: Edge[] = [];

		const mechCount = g.mechanics.length;
		g.mechanics.forEach((m, i) => {
			// Mechanieken over de bovenste 2/3 van de cirkel
			const angle = -Math.PI / 2 + (i - (mechCount - 1) / 2) * (Math.PI / Math.max(mechCount, 3));
			const mx = CX + R1 * Math.cos(angle);
			const my = CY + R1 * Math.sin(angle);
			nodes.push({ id: `m:${m.mechanic}`, label: m.mechanic, kind: 'mechanic', x: mx, y: my });
			edges.push({ x1: CX, y1: CY, x2: mx, y2: my });

			const spread = Math.PI / Math.max(mechCount, 3) * 0.85;
			m.cards.forEach((c, j) => {
				const a2 = angle + (j - (m.cards.length - 1) / 2) * (spread / Math.max(m.cards.length, 1));
				const x = CX + R2 * Math.cos(a2);
				const y = CY + R2 * Math.sin(a2);
				nodes.push({ id: c.riftboundId, label: c.name, kind: 'card', x, y });
				edges.push({ x1: mx, y1: my, x2: x, y2: y });
			});
		});

		g.interactions.forEach((x, i) => {
			const angle = Math.PI / 2 + (i - (g.interactions.length - 1) / 2) * 0.32;
			const px = CX + R2 * 0.8 * Math.cos(angle);
			const py = CY + R2 * 0.8 * Math.sin(angle);
			nodes.push({ id: x.otherId, label: x.otherName, kind: 'interaction', sub: x.kind, x: px, y: py });
			edges.push({ x1: CX, y1: CY, x2: px, y2: py, dashed: true });
		});
		return { nodes, edges };
	});

	function trim(s: string, n = 18): string {
		return s.length > n ? s.slice(0, n - 1) + '…' : s;
	}
</script>

<svelte:head><title>Graph-verkenner — RB Rules</title></svelte:head>

<main>
	<h1>Graph-<span>verkenner</span></h1>
	<p class="subtitle">
		Het semantische web achter de kaarten: mechanieken verbinden kaarten, stippellijnen zijn
		geverifieerde interacties.
	</p>

	<form method="GET" class="search">
		<input type="search" name="q" value={data.q} placeholder="Zoek een kaart op naam om te verkennen…" />
		<button type="submit">Zoek</button>
	</form>

	{#if data.error}<p class="warn">{data.error}</p>{/if}

	{#if !data.graph && data.candidates.length}
		<div class="candidates">
			{#each data.candidates.slice(0, 12) as c (c.riftboundId)}
				<a class="candidate panel" href="/graph?card={c.riftboundId}">
					{#if c.imageUrl}<img src={c.imageUrl} alt={c.name} loading="lazy" />{/if}
					<span>{c.name}</span>
				</a>
			{/each}
		</div>
	{:else if !data.graph}
		<p class="meta">Zoek een kaart om zijn netwerk te zien, of open een kaartpagina en kies "Bekijk in graph".</p>
	{/if}

	{#if data.graph}
		{@const g = data.graph}
		<div class="panel viz-wrap">
			<svg viewBox="0 0 {W} {H}" role="img" aria-label="Netwerk rond {g.center.name}">
				{#each layout.edges as e, i (i)}
					<line x1={e.x1} y1={e.y1} x2={e.x2} y2={e.y2}
						stroke="#263650" stroke-width="1.4"
						stroke-dasharray={e.dashed ? '5 5' : undefined} />
				{/each}
				<!-- Index-key: dezelfde kaart mag in meerdere groepen voorkomen -->
				{#each layout.nodes as n, i (i)}
					{#if n.kind === 'mechanic'}
						<a href="/cards?mechanic={encodeURIComponent(n.label)}">
							<rect x={n.x - 54} y={n.y - 15} width="108" height="30" rx="15"
								fill="#151f31" stroke="#4fbf8b" />
							<text x={n.x} y={n.y + 4} text-anchor="middle" fill="#4fbf8b" font-size="12">{trim(n.label, 15)}</text>
						</a>
					{:else}
						<a href="/graph?card={n.id}">
							<circle cx={n.x} cy={n.y} r="8" fill={n.kind === 'interaction' ? '#e0a32e' : '#93a5c4'} />
							<text x={n.x} y={n.y + 22} text-anchor="middle" fill="#93a5c4" font-size="11">{trim(n.label)}</text>
							{#if n.sub}
								<text x={n.x} y={n.y + 35} text-anchor="middle" fill="#e0a32e" font-size="9">{n.sub}</text>
							{/if}
						</a>
					{/if}
				{/each}
				<!-- Centrum bovenop -->
				<circle cx={CX} cy={CY} r="14" fill="#d98a4e" />
				<text x={CX} y={CY - 24} text-anchor="middle" fill="#e8eefb" font-size="14" font-weight="700">{g.center.name}</text>
			</svg>
		</div>
		<p class="meta legend">
			<span class="dot accent"></span> gekozen kaart
			<span class="dot green"></span> mechaniek (klik = alle kaarten ermee)
			<span class="dot grey"></span> deelt mechaniek
			<span class="dot yellow"></span> geverifieerde interactie
			· <a href="/cards/{g.center.riftboundId}">Naar kaartpagina</a>
		</p>
	{/if}
</main>

<style>
	main { max-width: 1000px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: var(--accent); }
	.subtitle, .meta { color: var(--muted); }
	.search { display: flex; gap: 8px; margin: 14px 0; }
	.search input {
		flex: 1; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 10px; padding: 10px 14px;
	}
	.search button {
		background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 9px 18px; font-weight: 600; cursor: pointer;
	}
	.candidates { display: grid; gap: 10px; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); }
	.candidate { padding: 8px; text-decoration: none; color: var(--text); display: flex; flex-direction: column; gap: 6px; font-size: 0.85rem; }
	.candidate img { width: 100%; border-radius: 8px; }
	.candidate:hover { border-color: var(--accent); }
	.viz-wrap { padding: 8px; overflow-x: auto; -webkit-overflow-scrolling: touch; }
	/* Mobile-first (#38): op smal niet meeschalen (labels worden dan ~5px),
	   maar op natuurlijke grootte horizontaal scrollen binnen .viz-wrap. */
	svg { width: 100%; min-width: 900px; height: auto; display: block; }
	svg a { cursor: pointer; }
	.legend { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-size: 0.82rem; }
	.legend a { color: var(--muted); }
	.dot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-left: 10px; }
	.dot.accent { background: var(--accent); }
	.dot.green { background: var(--ok); }
	.dot.grey { background: var(--muted); }
	.dot.yellow { background: var(--warn); }
	.warn { color: var(--err); }
</style>
