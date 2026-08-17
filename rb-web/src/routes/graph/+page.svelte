<script lang="ts">
	import RbText from '$lib/RbText.svelte';

	let { data } = $props();

	// Radiale layout: kaart centraal, mechanieken op de bovenste boog met hun
	// gedeelde kaarten daarbuiten, regels linksonder, interacties rechtsonder.
	// Deterministisch en leesbaar — geen physics nodig.
	const W = 940;
	const H = 510;
	const CX = W / 2;
	const CY = 300;
	const R1 = 140;   // mechanieken
	const R2 = 280;   // kaarten die een mechaniek delen
	const R3 = 200;   // regels en interacties

	interface Node {
		id: string;
		label: string;
		kind: 'mechanic' | 'card' | 'interaction' | 'rule';
		sub?: string;
		href: string;
		x: number;
		y: number;
		/** Labels van naburige knopen wisselen van hoogte, anders overlappen ze. */
		stagger?: boolean;
	}
	interface Edge { x1: number; y1: number; x2: number; y2: number; dashed?: boolean }

	const layout = $derived.by(() => {
		const g = data.graph;
		if (!g) return { nodes: [] as Node[], edges: [] as Edge[] };
		const nodes: Node[] = [];
		const edges: Edge[] = [];

		const mechCount = g.mechanics.length;
		g.mechanics.forEach((m, i) => {
			// Mechanieken over de bovenste helft van de cirkel
			const angle = -Math.PI / 2 + (i - (mechCount - 1) / 2) * (Math.PI / Math.max(mechCount, 3));
			const mx = CX + R1 * Math.cos(angle);
			const my = CY + R1 * Math.sin(angle);
			nodes.push({
				id: `m:${m.mechanic}`, label: m.mechanic, kind: 'mechanic',
				href: `/cards?mechanic=${encodeURIComponent(m.mechanic)}`, x: mx, y: my
			});
			edges.push({ x1: CX, y1: CY, x2: mx, y2: my });

			const cards = m.cards.slice(0, 3);
			const spread = (Math.PI / Math.max(mechCount, 3)) * 0.92;
			cards.forEach((c, j) => {
				const a2 = angle + (j - (cards.length - 1) / 2) * (spread / Math.max(cards.length - 1, 1));
				const x = CX + R2 * Math.cos(a2);
				const y = CY + R2 * Math.sin(a2);
				nodes.push({
					id: c.id, label: c.label, kind: 'card',
					href: `/graph?card=${c.id}`, x, y, stagger: j % 2 === 1
				});
				edges.push({ x1: mx, y1: my, x2: x, y2: y });
			});
		});

		// Regelsecties: linksonder. Dit zijn afgeleide relaties — de graaf
		// leidde ze af uit "kaart heeft mechaniek" + "sectie definieert
		// mechaniek", ze staan nergens als feit opgeslagen.
		g.rules.slice(0, 4).forEach((r, i, arr) => {
			const angle = Math.PI * 0.80 + (i - (arr.length - 1) / 2) * 0.30;
			const x = CX + R3 * Math.cos(angle);
			const y = CY + R3 * Math.sin(angle);
			nodes.push({
				id: `r:${r.code}`, label: `§ ${r.code}`, kind: 'rule',
				sub: r.via ? `via ${r.via}` : undefined,
				href: `/rules/${encodeURIComponent(r.code)}`, x, y
			});
			edges.push({ x1: CX, y1: CY, x2: x, y2: y, dashed: true });
		});

		// Geverifieerde interacties: rechtsonder.
		g.interactions.slice(0, 4).forEach((x, i, arr) => {
			const angle = Math.PI * 0.20 - (i - (arr.length - 1) / 2) * 0.30;
			const px = CX + R3 * Math.cos(angle);
			const py = CY + R3 * Math.sin(angle);
			nodes.push({
				id: x.otherId, label: x.otherName, kind: 'interaction', sub: x.kind,
				href: `/graph?card=${x.otherId}`, x: px, y: py, stagger: i % 2 === 1
			});
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
		afgeleide regels en geverifieerde interacties.
	</p>

	<form method="GET" class="search">
		<label class="sr-only" for="graph-q">Zoek een kaart</label>
		<input id="graph-q" type="search" name="q" value={data.q} placeholder="Zoek een kaart op naam om te verkennen…" />
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
		{#if g.facts.length}
			<div class="facts">
				{#each g.facts as f (f.kind + f.label)}
					<p class="fact {f.kind === 'Ban' ? 'ban' : 'errata'}">
						<span class="fact-kind">{f.kind}</span><RbText text={f.label} />
					</p>
				{/each}
			</div>
		{/if}

		<div class="panel viz-wrap">
			<svg viewBox="0 0 {W} {H}" role="img" aria-label="Netwerk rond {g.center.label}">
				{#each layout.edges as e, i (i)}
					<line x1={e.x1} y1={e.y1} x2={e.x2} y2={e.y2}
						stroke="#263650" stroke-width="1.4"
						stroke-dasharray={e.dashed ? '5 5' : undefined} />
				{/each}
				{#each layout.nodes as n, i (i)}
					{#if n.kind === 'mechanic'}
						<a href={n.href}>
							<rect x={n.x - 54} y={n.y - 15} width="108" height="30" rx="15"
								fill="#151f31" stroke="#4fbf8b" />
							<text x={n.x} y={n.y + 4} text-anchor="middle" fill="#4fbf8b" font-size="12">{trim(n.label, 15)}</text>
						</a>
					{:else if n.kind === 'rule'}
						<a href={n.href}>
							<rect x={n.x - 46} y={n.y - 14} width="92" height="28" rx="6"
								fill="#151f31" stroke="#5b9dd9" />
							<text x={n.x} y={n.y + 4} text-anchor="middle" fill="#5b9dd9" font-size="12">{trim(n.label, 12)}</text>
							{#if n.sub}
								<text x={n.x} y={n.y + 27} text-anchor="middle" fill="#93a5c4" font-size="9">{n.sub}</text>
							{/if}
						</a>
					{:else}
						{@const dy = n.stagger ? 34 : 20}
						<a href={n.href}>
							<circle cx={n.x} cy={n.y} r="8" fill={n.kind === 'interaction' ? '#e0a32e' : '#93a5c4'} />
							<text x={n.x} y={n.y + dy} text-anchor="middle" fill="#93a5c4" font-size="11">{trim(n.label, 15)}</text>
							{#if n.sub}
								<text x={n.x} y={n.y + dy + 13} text-anchor="middle" fill="#e0a32e" font-size="9">{n.sub}</text>
							{/if}
						</a>
					{/if}
				{/each}
				<!-- Centrum bovenop -->
				<circle cx={CX} cy={CY} r="14" fill="#d98a4e" />
				<text x={CX} y={CY - 24} text-anchor="middle" fill="#e8eefb" font-size="14" font-weight="700">{g.center.label}</text>
			</svg>
		</div>

		<p class="meta scroll-hint">Sleep de graaf opzij om alles te zien.</p>
		<p class="meta legend">
			<span class="dot accent"></span> gekozen kaart
			<span class="dot green"></span> mechaniek
			<span class="dot blue"></span> regel die deze kaart beheerst
			<span class="dot grey"></span> deelt mechaniek
			<span class="dot yellow"></span> geverifieerde interactie
		</p>
		<p class="meta legend">
			<a href="/cards/{g.center.id}">Naar kaartpagina</a>
			· <a href="/hoe-het-werkt">Hoe deze graaf werkt</a>
			· <span class="engine">{g.source === 'neo4j' ? 'beantwoord uit de kennisgraaf' : 'kennisgraaf niet beschikbaar — uit de database'}</span>
		</p>
	{/if}
</main>

<style>
	main { max-width: 1000px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: var(--accent); }
	.subtitle, .meta { color: var(--muted); }
	.sr-only {
		position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px;
		overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; border: 0;
	}
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
	.facts { margin-bottom: 10px; }
	.fact {
		margin: 0 0 6px; padding: 8px 12px; border-radius: 8px;
		border: 1px solid var(--border); background: var(--surface);
	}
	.fact.ban { border-color: var(--err); }
	.fact.errata { border-color: var(--accent); }
	.fact-kind {
		font-size: 0.7rem; font-weight: 700; text-transform: uppercase;
		letter-spacing: 0.05em; margin-right: 8px; color: var(--muted);
	}
	.viz-wrap { padding: 8px; overflow-x: auto; -webkit-overflow-scrolling: touch; }
	svg { width: 100%; height: auto; display: block; }
	/* Onder ~720px wordt de graaf anders onleesbaar klein: liever scrollen
	   binnen het kader dan alles wegschalen. */
	@media (max-width: 720px) {
		svg { min-width: 620px; }
	}
	.scroll-hint { display: none; }
	@media (max-width: 720px) {
		.scroll-hint { display: block; font-size: 0.8rem; margin: 4px 0 0; }
	}
	svg a { cursor: pointer; }
	.legend { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-size: 0.82rem; }
	.legend a { color: var(--muted); }
	.engine { font-style: italic; }
	.dot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-left: 10px; }
	.dot.accent { background: var(--accent); }
	.dot.green { background: var(--ok); }
	.dot.blue { background: #5b9dd9; }
	.dot.grey { background: var(--muted); }
	.dot.yellow { background: var(--warn); }
	.warn { color: var(--err); }
</style>
