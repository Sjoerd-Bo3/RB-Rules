<script lang="ts">
	import type { BrainNeighbor, BrainNode } from './+page.server';

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

	// ── Brein-verkenner (#108) ─────────────────────────────────────────
	// Zelfde radiale aanpak: de gekozen knoop centraal, buren op één ring
	// (twee afwisselende stralen zodra het druk wordt). Elke buur is klikbaar
	// en wordt het nieuwe centrum — zo loop je langs refs door het brein.

	interface BrainViewNode extends BrainNeighbor { x: number; y: number; }

	const brainLayout = $derived.by(() => {
		const b = data.brain;
		if (!b) return { nodes: [] as BrainViewNode[], edges: [] as Edge[] };
		const nodes: BrainViewNode[] = [];
		const edges: Edge[] = [];
		const count = b.neighbors.length;
		b.neighbors.forEach((nb, i) => {
			const angle = -Math.PI / 2 + (i * 2 * Math.PI) / Math.max(count, 1);
			const r = count > 12 ? (i % 2 === 0 ? 185 : 265) : 225;
			const x = CX + r * Math.cos(angle);
			const y = CY + r * Math.sin(angle);
			nodes.push({ ...nb, x, y });
			edges.push({ x1: CX, y1: CY, x2: x, y2: y, dashed: nb.edge === 'INTERACTS_WITH' });
		});
		return { nodes, edges };
	});

	// Kleur per knoopsoort — ontwerptokens, geen hardcoded kleuren. De
	// kennispiramide blijft leesbaar: officieel (secties/errata) oranje/rood,
	// facetten groen, community-claims geel, kaarten grijsblauw.
	const KIND_COLORS: Record<string, string> = {
		card: 'var(--muted)',
		mechanic: 'var(--ok)',
		domain: 'var(--ok)',
		tag: 'var(--ok)',
		set: 'var(--ok)',
		section: 'var(--accent)',
		concept: 'var(--text)',
		claim: 'var(--warn)',
		source: 'var(--border-strong)',
		erratum: 'var(--err)',
		change: 'var(--err)',
		ruling: 'var(--text)'
	};

	function refKind(ref: string): string {
		const i = ref.indexOf(':');
		return i > 0 ? ref.slice(0, i) : '';
	}
	function refKey(ref: string): string {
		const i = ref.indexOf(':');
		return i > 0 ? ref.slice(i + 1) : ref;
	}
	function kindColor(ref: string): string {
		return KIND_COLORS[refKind(ref)] ?? 'var(--muted)';
	}

	function trim(s: string, n = 18): string {
		return s.length > n ? s.slice(0, n - 1) + '…' : s;
	}

	const brainCenterLabel = $derived.by(() => {
		const node = data.brain?.node;
		if (!node) return '';
		const p = node.props;
		if (node.kind === 'section' && typeof p.code === 'string') return `§${p.code}`;
		const label = [p.name, p.title, p.cardName, p.question, p.statement].find(
			(v): v is string => typeof v === 'string' && v.length > 0
		);
		return label ?? node.ref;
	});

	// ── Detailpaneel: props leesbaar maken zonder per soort een template ──
	type PropRow =
		| { key: string; type: 'text'; value: string; long: boolean }
		| { key: string; type: 'ref'; ref: string }
		| { key: string; type: 'refs'; items: { ref: string; name: string }[] }
		| { key: string; type: 'list'; items: string[] }
		| { key: string; type: 'parents'; items: { code: string; text: string }[] };

	const LONG_FIELDS = new Set(['text', 'body', 'statement', 'newText', 'summary', 'meaning']);

	function fmtDate(iso: string): string {
		return new Date(iso).toLocaleDateString('nl-NL', { day: 'numeric', month: 'short', year: 'numeric' });
	}

	function propRows(node: BrainNode): PropRow[] {
		const rows: PropRow[] = [];
		for (const [key, value] of Object.entries(node.props)) {
			if (value === null || value === undefined || value === '') continue;
			if (typeof value === 'string') {
				if ((key === 'ref' || key.endsWith('Ref')) && value.includes(':')) {
					rows.push({ key, type: 'ref', ref: value });
				} else if (/^\d{4}-\d{2}-\d{2}T/.test(value)) {
					rows.push({ key, type: 'text', value: fmtDate(value), long: false });
				} else {
					rows.push({ key, type: 'text', value, long: LONG_FIELDS.has(key) || value.length > 160 });
				}
			} else if (typeof value === 'number' || typeof value === 'boolean') {
				rows.push({ key, type: 'text', value: value === true ? 'ja' : value === false ? 'nee' : String(value), long: false });
			} else if (Array.isArray(value) && value.length) {
				if (value.every((v) => typeof v === 'string')) {
					rows.push({ key, type: 'list', items: value as string[] });
				} else if (value.every((v) => v && typeof v === 'object' && 'ref' in v && 'name' in v)) {
					rows.push({ key, type: 'refs', items: value as { ref: string; name: string }[] });
				} else if (value.every((v) => v && typeof v === 'object' && 'code' in v && 'text' in v)) {
					rows.push({ key, type: 'parents', items: value as { code: string; text: string }[] });
				}
			}
		}
		return rows;
	}

	// Doorklik naar de bestaande schermen per knoopsoort — het brein is de
	// kaart, dit zijn de wegen terug naar de inhoud.
	const brainLinks = $derived.by(() => {
		const node = data.brain?.node;
		if (!node) return [] as { href: string; label: string }[];
		const key = refKey(node.ref);
		switch (node.kind) {
			case 'card':
				return [
					{ href: `/cards/${key}`, label: 'Naar kaartpagina' },
					{ href: `/graph?card=${encodeURIComponent(key)}`, label: 'Kaart-verkenning (mechanieken en interacties)' }
				];
			case 'section': {
				const code = key.includes('/') ? key.slice(key.indexOf('/') + 1) : key;
				return [{ href: `/rules/${encodeURIComponent(code)}`, label: 'Lees in de regels-browser' }];
			}
			case 'mechanic':
				return [{ href: `/cards?mechanic=${encodeURIComponent(key)}`, label: 'Alle kaarten met deze mechaniek' }];
			case 'concept':
				return [{ href: '/primer', label: 'Naar de game-primer' }];
			default:
				return [];
		}
	});

	// Zoekresultaten uit het brein, gegroepeerd in de piramide-volgorde
	// waarin de API ze levert (officieel > rulings > primer > community).
	const LAYER_LABELS: Record<string, string> = {
		rules: 'regels',
		cards: 'kaart',
		rulings: 'ruling',
		primer: 'primer',
		claims: 'claim'
	};
</script>

<svelte:head><title>Brein-verkenner — RB Rules</title></svelte:head>

<main>
	<h1>Brein-<span>verkenner</span></h1>
	<p class="subtitle">
		Het brein achter de vraagbaak: kaarten, regelsecties, concepten en community-claims als één
		verkenbaar netwerk — klik een knoop om verder te lopen.
	</p>

	<form method="GET" class="search">
		<input type="search" name="q" value={data.q} placeholder="Zoek een kaart, regel, concept of claim…" />
		<button type="submit">Zoek</button>
	</form>

	{#if data.error}<p class="warn">{data.error}</p>{/if}
	{#if data.brainError}<p class="warn">{data.brainError}</p>{/if}

	{#if !data.graph && !data.brain && (data.candidates.length || data.brainResults.length)}
		{#if data.candidates.length}
			<h2 class="block-h">Kaarten</h2>
			<div class="candidates">
				{#each data.candidates.slice(0, 12) as c (c.riftboundId)}
					<a class="candidate panel" href="/graph?card={c.riftboundId}">
						{#if c.imageUrl}<img src={c.imageUrl} alt={c.name} loading="lazy" />{/if}
						<span>{c.name}</span>
					</a>
				{/each}
			</div>
		{/if}
		{#if data.brainResults.length}
			<h2 class="block-h">
				In het brein
				{#if data.brainDegraded}<span class="meta">(semantisch zoeken viel uit — alleen tekst-match)</span>{/if}
			</h2>
			<div class="brain-results">
				{#each data.brainResults as r, i (i)}
					<a class="panel brain-hit" href="/graph?ref={encodeURIComponent(r.ref)}">
						<span class="layer" style:color={kindColor(r.ref)} style:border-color={kindColor(r.ref)}>
							{LAYER_LABELS[r.layer] ?? r.layer}
						</span>
						<span class="hit-body">
							<strong>{r.title ?? r.ref}</strong>
							{#if r.snippet}<span class="meta">{r.snippet}</span>{/if}
							<span class="meta trust">{r.trustLabel}</span>
						</span>
					</a>
				{/each}
			</div>
		{/if}
	{:else if !data.graph && !data.brain && !data.brainError && !data.error}
		<p class="meta">
			Zoek om het brein in te stappen, of open een kaartpagina en kies "Bekijk in graph".
			{#if data.q}Geen resultaten voor "{data.q}".{/if}
		</p>
	{/if}

	{#if data.graph}
		{@const g = data.graph}
		<div class="panel viz-wrap">
			<svg viewBox="0 0 {W} {H}" role="img" aria-label="Netwerk rond {g.center.name}">
				{#each layout.edges as e, i (i)}
					<line x1={e.x1} y1={e.y1} x2={e.x2} y2={e.y2}
						style="stroke: var(--border-strong)" stroke-width="1.4"
						stroke-dasharray={e.dashed ? '5 5' : undefined} />
				{/each}
				<!-- Index-key: dezelfde kaart mag in meerdere groepen voorkomen -->
				{#each layout.nodes as n, i (i)}
					{#if n.kind === 'mechanic'}
						<a href="/cards?mechanic={encodeURIComponent(n.label)}">
							<rect x={n.x - 54} y={n.y - 15} width="108" height="30" rx="15"
								style="fill: var(--surface); stroke: var(--ok)" />
							<text x={n.x} y={n.y + 4} text-anchor="middle" style="fill: var(--ok)" font-size="12">{trim(n.label, 15)}</text>
						</a>
					{:else}
						<a href="/graph?card={n.id}">
							<circle cx={n.x} cy={n.y} r="8" style="fill: {n.kind === 'interaction' ? 'var(--warn)' : 'var(--muted)'}" />
							<text x={n.x} y={n.y + 22} text-anchor="middle" style="fill: var(--muted)" font-size="11">{trim(n.label)}</text>
							{#if n.sub}
								<text x={n.x} y={n.y + 35} text-anchor="middle" style="fill: var(--warn)" font-size="9">{n.sub}</text>
							{/if}
						</a>
					{/if}
				{/each}
				<!-- Centrum bovenop -->
				<circle cx={CX} cy={CY} r="14" style="fill: var(--accent)" />
				<text x={CX} y={CY - 24} text-anchor="middle" style="fill: var(--text)" font-size="14" font-weight="700">{g.center.name}</text>
			</svg>
		</div>
		<p class="meta legend">
			<span class="dot accent"></span> gekozen kaart
			<span class="dot green"></span> mechaniek (klik = alle kaarten ermee)
			<span class="dot grey"></span> deelt mechaniek
			<span class="dot yellow"></span> geverifieerde interactie
			· <a href="/cards/{g.center.riftboundId}">Naar kaartpagina</a>
			· <a href="/graph?ref={encodeURIComponent(`card:${g.center.riftboundId}`)}">Verken in het brein</a>
		</p>
	{/if}

	{#if data.brain}
		{@const b = data.brain}
		{#if b.graphError}<p class="warn">{b.graphError}</p>{/if}
		{#if b.neighbors.length}
			<div class="panel viz-wrap">
				<svg viewBox="0 0 {W} {H}" role="img" aria-label="Brein-buren rond {brainCenterLabel}">
					{#each brainLayout.edges as e, i (i)}
						<line x1={e.x1} y1={e.y1} x2={e.x2} y2={e.y2}
							stroke="var(--border)" stroke-width="1.4"
							stroke-dasharray={e.dashed ? '5 5' : undefined} />
					{/each}
					{#each brainLayout.nodes as n, i (i)}
						<a href="/graph?ref={encodeURIComponent(n.ref)}">
							<circle cx={n.x} cy={n.y} r="8" fill={kindColor(n.ref)} />
							<text x={n.x} y={n.y + 22} text-anchor="middle" fill="var(--muted)" font-size="11">
								{trim(n.name ?? refKey(n.ref))}
							</text>
							<text x={n.x} y={n.y + 35} text-anchor="middle" fill="var(--border-strong)" font-size="9">
								{n.richting === 'in' ? '←' : '→'} {n.edge.toLowerCase()}
							</text>
						</a>
					{/each}
					<circle cx={CX} cy={CY} r="14" fill="var(--accent)" />
					<text x={CX} y={CY - 24} text-anchor="middle" fill="var(--text)" font-size="14" font-weight="700">
						{trim(brainCenterLabel, 40)}
					</text>
				</svg>
			</div>
			<p class="meta legend">
				<span class="key"><span class="dot" style:background="var(--accent)"></span> regelsectie</span>
				<span class="key"><span class="dot" style:background="var(--ok)"></span> facet (mechaniek/domein/tag/set)</span>
				<span class="key"><span class="dot" style:background="var(--warn)"></span> community-claim</span>
				<span class="key"><span class="dot" style:background="var(--text)"></span> concept</span>
				<span class="key"><span class="dot" style:background="var(--err)"></span> erratum/wijziging</span>
				<span class="key"><span class="dot" style:background="var(--muted)"></span> kaart</span>
			</p>
		{:else if !b.graphError}
			<p class="meta">Geen buren in de kennisgraaf voor deze knoop.</p>
		{/if}

		<!-- Detail uit Postgres: wat de knoop wéét, met laag- en trust-label. -->
		<div class="panel node-detail">
			<p class="node-head">
				<span class="layer" style:color={kindColor(b.node.ref)} style:border-color={kindColor(b.node.ref)}>{b.node.kind}</span>
				<strong>{brainCenterLabel}</strong>
				<span class="meta mono">{b.node.ref}</span>
			</p>
			<p class="meta trust">{b.node.trustLabel}</p>
			<dl>
				{#each propRows(b.node) as row (row.key)}
					<div class="prop">
						<dt>{row.key}</dt>
						<dd>
							{#if row.type === 'text'}
								<span class:long-text={row.long}>{row.value}</span>
							{:else if row.type === 'ref'}
								<a href="/graph?ref={encodeURIComponent(row.ref)}" class="mono">{row.ref}</a>
							{:else if row.type === 'refs'}
								{#each row.items as item, i (i)}
									<a href="/graph?ref={encodeURIComponent(item.ref)}" class="chip">{item.name}</a>
								{/each}
							{:else if row.type === 'list'}
								{row.items.join(', ')}
							{:else if row.type === 'parents'}
								{#each row.items as p, i (i)}
									<p class="parent"><strong>§{p.code}</strong> <span class="meta">{p.text}</span></p>
								{/each}
							{/if}
						</dd>
					</div>
				{/each}
			</dl>
			{#if brainLinks.length}
				<p class="meta links">
					{#each brainLinks as l, i (i)}
						{#if i > 0}<span class="sep">·</span>{/if}
						<a href={l.href}>{l.label}</a>
					{/each}
				</p>
			{/if}
		</div>
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
	.block-h { font-size: 1rem; margin: 18px 0 10px; }
	.block-h .meta { font-weight: 400; font-size: 0.82rem; }
	.candidates { display: grid; gap: 10px; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); }
	.candidate { padding: 8px; text-decoration: none; color: var(--text); display: flex; flex-direction: column; gap: 6px; font-size: 0.85rem; }
	.candidate img { width: 100%; border-radius: 8px; }
	.candidate:hover { border-color: var(--accent); }
	.brain-results { display: flex; flex-direction: column; gap: 8px; }
	.brain-hit {
		display: flex; gap: 10px; padding: 10px 12px; text-decoration: none;
		color: var(--text); align-items: flex-start;
	}
	.brain-hit:hover { border-color: var(--accent); }
	.layer {
		flex-shrink: 0; font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.04em;
		border: 1px solid; border-radius: 999px; padding: 2px 9px; margin-top: 2px;
	}
	.hit-body { display: flex; flex-direction: column; gap: 3px; font-size: 0.9rem; min-width: 0; }
	.hit-body .meta { font-size: 0.82rem; }
	.hit-body .trust { font-size: 0.75rem; }
	.viz-wrap { padding: 8px; overflow-x: auto; -webkit-overflow-scrolling: touch; margin-top: 14px; }
	/* Mobile-first (#38): op smal niet meeschalen (labels worden dan ~5px),
	   maar op natuurlijke grootte horizontaal scrollen binnen .viz-wrap. */
	svg { width: 100%; min-width: 900px; height: auto; display: block; }
	svg a { cursor: pointer; }
	.legend { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-size: 0.82rem; }
	.legend a { color: var(--muted); }
	.legend .key { white-space: nowrap; display: inline-flex; align-items: center; gap: 5px; }
	.legend .key .dot { margin-left: 0; }
	.dot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-left: 10px; flex-shrink: 0; }
	.dot.accent { background: var(--accent); }
	.dot.green { background: var(--ok); }
	.dot.grey { background: var(--muted); }
	.dot.yellow { background: var(--warn); }
	.warn { color: var(--err); }
	.node-detail { padding: 14px 16px; margin-top: 14px; }
	.node-head { display: flex; gap: 10px; align-items: baseline; flex-wrap: wrap; margin: 0 0 4px; }
	.node-head .mono { overflow-wrap: anywhere; }
	.mono { font-family: ui-monospace, monospace; font-size: 0.82rem; }
	.trust { font-size: 0.82rem; margin: 0 0 10px; }
	dl { margin: 0; display: flex; flex-direction: column; gap: 8px; }
	.prop { display: grid; grid-template-columns: 130px 1fr; gap: 10px; font-size: 0.88rem; }
	.prop dt { color: var(--muted); overflow-wrap: anywhere; }
	.prop dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
	.long-text { white-space: pre-wrap; }
	.chip {
		display: inline-block; border: 1px solid var(--border); border-radius: 999px;
		padding: 2px 10px; margin: 0 6px 6px 0; color: var(--text); text-decoration: none;
		font-size: 0.82rem;
	}
	.chip:hover { border-color: var(--accent); }
	.parent { margin: 0 0 6px; }
	.links { margin: 12px 0 0; }
	.links a { color: var(--accent); }
	.links .sep { margin: 0 6px; }
	@media (max-width: 640px) {
		.prop { grid-template-columns: 1fr; gap: 2px; }
	}
</style>
