<script lang="ts">
	import type { BrainNeighbor } from './+page.server';
	import RbText from '$lib/RbText.svelte';
	import {
		cardMeta,
		cardRef,
		mechanicRef,
		nodeLinks,
		refKey,
		refKind,
		type GraphNodeDetail
	} from '$lib/graphNode';

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
		/** Brein-ref van deze knoop (#252) — voert de hover-preview en het
		 *  detailpaneel; de kaart-graaf kent kaarten als kaal riftboundId. */
		ref: string;
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
			nodes.push({
				id: `m:${m.mechanic}`,
				ref: mechanicRef(m.mechanic),
				label: m.mechanic,
				kind: 'mechanic',
				x: mx,
				y: my
			});
			edges.push({ x1: CX, y1: CY, x2: mx, y2: my });

			const spread = Math.PI / Math.max(mechCount, 3) * 0.85;
			m.cards.forEach((c, j) => {
				const a2 = angle + (j - (m.cards.length - 1) / 2) * (spread / Math.max(m.cards.length, 1));
				const x = CX + R2 * Math.cos(a2);
				const y = CY + R2 * Math.sin(a2);
				nodes.push({
					id: c.riftboundId,
					ref: cardRef(c.riftboundId),
					label: c.name,
					kind: 'card',
					x,
					y
				});
				edges.push({ x1: mx, y1: my, x2: x, y2: y });
			});
		});

		g.interactions.forEach((x, i) => {
			const angle = Math.PI / 2 + (i - (g.interactions.length - 1) / 2) * 0.32;
			const px = CX + R2 * 0.8 * Math.cos(angle);
			const py = CY + R2 * 0.8 * Math.sin(angle);
			nodes.push({
				id: x.otherId,
				ref: cardRef(x.otherId),
				label: x.otherName,
				kind: 'interaction',
				sub: x.kind,
				x: px,
				y: py
			});
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

	function kindColor(ref: string): string {
		return KIND_COLORS[refKind(ref)] ?? 'var(--muted)';
	}

	function trim(s: string, n = 18): string {
		return s.length > n ? s.slice(0, n - 1) + '…' : s;
	}

	// ── Hover-preview en knoop-selectie (#252) ─────────────────────────
	// Eén vaste hovercard op paginaniveau (patroon uit #243/#244): .viz-wrap
	// heeft overflow-x:auto en zou een popover binnenin clippen. Klik navigeert
	// niet weg maar selecteert — de verkende graaf-positie blijft staan en het
	// detail verschijnt eronder. Ctrl/cmd-klik houdt "open in nieuw tabblad".
	const HOVER_W = 216;
	type HoverState = { ref: string; label: string; x: number; y: number; above: boolean };

	let hover = $state<HoverState | null>(null);
	let selectedRef = $state<string | null>(null);
	let detailEl = $state<HTMLElement | null>(null);
	let nodeCache = $state<Record<string, GraphNodeDetail>>({});
	let nodeError = $state<Record<string, string>>({});
	// Dubbele fetches bij snel heen-en-weer hoveren voorkomen.
	const inflight = new Map<string, Promise<void>>();

	const hoverDetail = $derived(hover ? (nodeCache[hover.ref] ?? null) : null);
	const selectedDetail = $derived(selectedRef ? (nodeCache[selectedRef] ?? null) : null);
	const selectedError = $derived(selectedRef ? (nodeError[selectedRef] ?? null) : null);

	async function loadNode(ref: string): Promise<void> {
		if (nodeCache[ref] || nodeError[ref]) return;
		let p = inflight.get(ref);
		if (!p) {
			p = (async () => {
				try {
					const res = await fetch(`/graph/node?ref=${encodeURIComponent(ref)}`);
					if (!res.ok) throw new Error(String(res.status));
					nodeCache[ref] = (await res.json()) as GraphNodeDetail;
				} catch {
					nodeError[ref] = 'Deze knoop kon niet geladen worden.';
				} finally {
					inflight.delete(ref);
				}
			})();
			inflight.set(ref, p);
		}
		await p;
	}

	function showHover(e: Event, ref: string, label: string) {
		const el = e.currentTarget as Element | null;
		if (!el) return;
		const r = el.getBoundingClientRect();
		// Kaartknopen tonen een afbeelding en vragen dus fors meer hoogte.
		const estH = refKind(ref) === 'card' ? 330 : 150;
		const below = r.bottom + estH + 12 < window.innerHeight || r.top < estH;
		hover = {
			ref,
			label,
			x: Math.max(8, Math.min(r.left, window.innerWidth - HOVER_W - 8)),
			y: below ? r.bottom + 6 : r.top - 6,
			above: !below
		};
		void loadNode(ref);
	}
	const hideHover = () => (hover = null);

	function selectNode(ref: string) {
		selectedRef = ref;
		hover = null;
		delete nodeError[ref]; // opnieuw proberen na een eerdere hover-fout
		void loadNode(ref);
		// block:'nearest' scrollt alleen als het paneel buiten beeld valt — de
		// graaf zelf blijft staan waar hij stond.
		queueMicrotask(() => detailEl?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }));
	}

	function pick(e: MouseEvent, ref: string) {
		if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
		e.preventDefault();
		selectNode(ref);
	}

	// Route-hergebruik: bij een nieuw centrum (andere ?ref/?card) is de oude
	// selectie betekenisloos.
	$effect(() => {
		data.ref;
		data.card;
		selectedRef = null;
		hover = null;
	});

	/** Ref van het centrum in de kaart-verkenning — ook dát is een knoop die
	 *  je kunt selecteren. */
	const graphCenterRef = $derived(data.graph ? cardRef(data.graph.center.riftboundId) : '');

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

	function propRows(props: Record<string, unknown>): PropRow[] {
		const rows: PropRow[] = [];
		for (const [key, value] of Object.entries(props)) {
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
	// kaart, dit zijn de wegen terug naar de inhoud ($lib/graphNode.ts).
	const brainLinks = $derived(
		data.brain ? nodeLinks(data.brain.node.ref, data.brain.node.kind) : []
	);

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

<svelte:head><title>Brein-verkenner — Poracle</title></svelte:head>
<svelte:window onscroll={hideHover} onresize={hideHover} />

<!-- Brein-projectie als leesbare rijen — gedeeld door het centrum-paneel en
     het selectiepaneel. -->
{#snippet propList(props: Record<string, unknown>)}
	<dl>
		{#each propRows(props) as row (row.key)}
			<div class="prop">
				<dt>{row.key}</dt>
				<dd>
					{#if row.type === 'text'}
						<span class:long-text={row.long}><RbText text={row.value} /></span>
					{:else if row.type === 'ref'}
						<a href="/graph?ref={encodeURIComponent(row.ref)}" class="mono">{row.ref}</a>
					{:else if row.type === 'refs'}
						{#each row.items as item, i (i)}
							<a
								href="/graph?ref={encodeURIComponent(item.ref)}"
								class="chip"
								onclick={(e) => pick(e, item.ref)}>{item.name}</a
							>
						{/each}
					{:else if row.type === 'list'}
						{row.items.join(', ')}
					{:else if row.type === 'parents'}
						{#each row.items as p, i (i)}
							<p class="parent"><strong>§{p.code}</strong> <span class="meta"><RbText text={p.text} /></span></p>
						{/each}
					{/if}
				</dd>
			</div>
		{/each}
	</dl>
{/snippet}

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
		<div class="panel viz-wrap" onscroll={hideHover}>
			<svg viewBox="0 0 {W} {H}" role="img" aria-label="Netwerk rond {g.center.name}">
				{#each layout.edges as e, i (i)}
					<line x1={e.x1} y1={e.y1} x2={e.x2} y2={e.y2}
						style="stroke: var(--border-strong)" stroke-width="1.4"
						stroke-dasharray={e.dashed ? '5 5' : undefined} />
				{/each}
				<!-- Index-key: dezelfde kaart mag in meerdere groepen voorkomen -->
				{#each layout.nodes as n, i (i)}
					<a
						href="/graph?ref={encodeURIComponent(n.ref)}"
						class:sel={selectedRef === n.ref}
						onclick={(e) => pick(e, n.ref)}
						onpointerenter={(e) => showHover(e, n.ref, n.label)}
						onpointerleave={hideHover}
						onfocus={(e) => showHover(e, n.ref, n.label)}
						onblur={hideHover}
					>
						{#if n.kind === 'mechanic'}
							<rect x={n.x - 54} y={n.y - 15} width="108" height="30" rx="15"
								style="fill: var(--surface); stroke: {selectedRef === n.ref ? 'var(--accent)' : 'var(--ok)'}"
								stroke-width={selectedRef === n.ref ? 2.5 : 1} />
							<text x={n.x} y={n.y + 4} text-anchor="middle" style="fill: var(--ok)" font-size="12">{trim(n.label, 15)}</text>
						{:else}
							<!-- Ruime, onzichtbare trefzone: een bol van 8px is met de muis
							     (en zeker met een vinger) niet te raken. -->
							<circle cx={n.x} cy={n.y} r="24" fill="transparent" />
							{#if selectedRef === n.ref}
								<circle cx={n.x} cy={n.y} r="13" fill="none" stroke="var(--accent)" stroke-width="2.5" />
							{/if}
							<circle cx={n.x} cy={n.y} r="8" style="fill: {n.kind === 'interaction' ? 'var(--warn)' : 'var(--muted)'}" />
							<text x={n.x} y={n.y + 22} text-anchor="middle" style="fill: var(--muted)" font-size="11">{trim(n.label)}</text>
							{#if n.sub}
								<text x={n.x} y={n.y + 35} text-anchor="middle" style="fill: var(--warn)" font-size="9">{n.sub}</text>
							{/if}
						{/if}
					</a>
				{/each}
				<!-- Centrum bovenop -->
				<a
					href="/graph?ref={encodeURIComponent(graphCenterRef)}"
					onclick={(e) => pick(e, graphCenterRef)}
					onpointerenter={(e) => showHover(e, graphCenterRef, g.center.name)}
					onpointerleave={hideHover}
					onfocus={(e) => showHover(e, graphCenterRef, g.center.name)}
					onblur={hideHover}
				>
					<circle cx={CX} cy={CY} r="30" fill="transparent" />
					{#if selectedRef === graphCenterRef}
						<circle cx={CX} cy={CY} r="19" fill="none" stroke="var(--accent)" stroke-width="2.5" />
					{/if}
					<circle cx={CX} cy={CY} r="14" style="fill: var(--accent)" />
					<text x={CX} y={CY - 24} text-anchor="middle" style="fill: var(--text)" font-size="14" font-weight="700">{g.center.name}</text>
				</a>
			</svg>
		</div>
		<p class="meta legend">
			<span class="dot accent"></span> gekozen kaart
			<span class="dot green"></span> mechaniek
			<span class="dot grey"></span> deelt mechaniek
			<span class="dot yellow"></span> geverifieerde interactie
			· <a href="/cards/{g.center.riftboundId}">Naar kaartpagina</a>
			· <a href="/graph?ref={encodeURIComponent(`card:${g.center.riftboundId}`)}">Verken in het brein</a>
		</p>
		<p class="meta hint">Hover een knoop voor een preview, klik voor het volledige detail hieronder.</p>
	{/if}

	{#if data.brain}
		{@const b = data.brain}
		{#if b.graphError}<p class="warn">{b.graphError}</p>{/if}
		{#if b.neighbors.length}
			<div class="panel viz-wrap" onscroll={hideHover}>
				<svg viewBox="0 0 {W} {H}" role="img" aria-label="Brein-buren rond {brainCenterLabel}">
					{#each brainLayout.edges as e, i (i)}
						<line x1={e.x1} y1={e.y1} x2={e.x2} y2={e.y2}
							stroke="var(--border)" stroke-width="1.4"
							stroke-dasharray={e.dashed ? '5 5' : undefined} />
					{/each}
					{#each brainLayout.nodes as n, i (i)}
						<a
							href="/graph?ref={encodeURIComponent(n.ref)}"
							onclick={(e) => pick(e, n.ref)}
							onpointerenter={(e) => showHover(e, n.ref, n.name ?? refKey(n.ref))}
							onpointerleave={hideHover}
							onfocus={(e) => showHover(e, n.ref, n.name ?? refKey(n.ref))}
							onblur={hideHover}
						>
							<!-- Ruime, onzichtbare trefzone (zie kaart-graaf hierboven). -->
							<circle cx={n.x} cy={n.y} r="24" fill="transparent" />
							{#if selectedRef === n.ref}
								<circle cx={n.x} cy={n.y} r="13" fill="none" stroke="var(--accent)" stroke-width="2.5" />
							{/if}
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
			<p class="meta hint">
				Hover een knoop voor een preview, klik voor het volledige detail hieronder — de graaf blijft
				staan.
			</p>
		{:else if !b.graphError}
			<p class="meta">Geen buren in de kennisgraaf voor deze knoop.</p>
		{/if}

		<!-- Detail uit Postgres: wat de knoop wéét, met laag- en trust-label.
		     Zodra een buur is aangeklikt neemt het selectiepaneel hieronder het
		     over — één detailweergave onder de graaf, geen dubbele panelen. -->
		{#if !selectedRef}
			<div class="panel node-detail">
				<p class="node-head">
					<span class="layer" style:color={kindColor(b.node.ref)} style:border-color={kindColor(b.node.ref)}>{b.node.kind}</span>
					<strong>{brainCenterLabel}</strong>
					<span class="meta mono">{b.node.ref}</span>
				</p>
				<p class="meta trust">{b.node.trustLabel}</p>
				{@render propList(b.node.props)}
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
	{/if}

	<!-- Geselecteerde knoop (#252): volledige info ónder de graaf, zonder
	     wegnavigeren. Kaarten krijgen de kaartweergave met dossier-blok,
	     overige soorten de compacte brein-projectie. -->
	{#if selectedRef}
		<div class="panel node-detail selected-detail" bind:this={detailEl}>
			<div class="node-head">
				<span class="layer" style:color={kindColor(selectedRef)} style:border-color={kindColor(selectedRef)}>
					{selectedDetail?.kind ?? refKind(selectedRef)}
				</span>
				<strong>{trim(selectedDetail?.label ?? refKey(selectedRef), 70)}</strong>
				<span class="meta mono">{selectedDetail?.ref ?? selectedRef}</span>
				<button type="button" class="close" onclick={() => (selectedRef = null)}>Sluiten</button>
			</div>
			{#if selectedDetail}
				{#if selectedDetail.trustLabel}<p class="meta trust">{selectedDetail.trustLabel}</p>{/if}
				{#if selectedDetail.card}
					{@const c = selectedDetail.card}
					<div class="card-detail">
						{#if c.imageUrl}
							<img class="cd-img" src={c.imageUrl} alt={c.name} loading="lazy" />
						{/if}
						<div class="cd-body">
							<p class="cd-meta">
								{[cardMeta(c), c.rarity, c.setLabel].filter(Boolean).join(' · ')}
							</p>
							{#if c.banned || c.legality !== 'legal'}
								<p class="cd-badges">
									{#if c.banned}<span class="badge err">Verboden</span>{/if}
									{#if c.legality === 'upcoming'}<span class="badge warn">Nog niet legaal</span>{/if}
									{#if c.legality === 'announced'}<span class="badge warn">Aangekondigd</span>{/if}
								</p>
							{/if}
							{#if c.errataText}
								<p class="cd-label">Kaarttekst (erratum)</p>
								<p class="oracle"><RbText text={c.errataText} /></p>
								{#if c.textPlain}
									<p class="cd-label">Gedrukte tekst</p>
									<p class="oracle printed"><RbText text={c.textPlain} /></p>
								{/if}
							{:else if c.textPlain}
								<p class="oracle"><RbText text={c.textPlain} /></p>
							{:else}
								<p class="meta">Geen kaarttekst vastgelegd.</p>
							{/if}
							{#if c.mechanics?.length || c.tags.length}
								<p class="chips">
									{#each c.mechanics ?? [] as m (m)}
										<a
											class="chip"
											href="/graph?ref={encodeURIComponent(mechanicRef(m))}"
											onclick={(e) => pick(e, mechanicRef(m))}>{m}</a
										>
									{/each}
									{#each c.tags as t (t)}<span class="chip plain">{t}</span>{/each}
								</p>
							{/if}
						</div>
					</div>
					{#if selectedDetail.dossier}
						{@const dos = selectedDetail.dossier}
						<div class="dossier">
							{#if dos.rulings.length}
								<p class="cd-label">
									Rulings
									{#if dos.rulingTotal > dos.rulings.length}
										<span class="meta">({dos.rulings.length} van {dos.rulingTotal})</span>
									{/if}
								</p>
								{#each dos.rulings as r (r.id)}
									<p class="dos-item">
										{#if r.question}<strong>{r.question}</strong>{/if}
										{r.text}
									</p>
								{/each}
							{/if}
							{#if dos.claims.length}
								<p class="cd-label">
									Community-claims
									{#if dos.claimTotal > dos.claims.length}
										<span class="meta">({dos.claims.length} van {dos.claimTotal})</span>
									{/if}
								</p>
								{#each dos.claims as cl (cl.id)}
									<p class="dos-item">{cl.statement} <span class="meta">· {cl.trustLabel}</span></p>
								{/each}
							{/if}
							{#if dos.banHistory.length}
								<p class="cd-label">Ban-historie</p>
								{#each dos.banHistory as bh, i (i)}
									<p class="dos-item">
										{bh.kind} · {bh.format}{#if bh.effectiveFrom} · vanaf {fmtDate(bh.effectiveFrom)}{/if}
									</p>
								{/each}
							{/if}
						</div>
					{/if}
				{:else if selectedDetail.props}
					{@render propList(selectedDetail.props)}
				{/if}
				<p class="meta links">
					{#each nodeLinks(selectedDetail.ref, selectedDetail.kind) as l (l.href)}
						<a href={l.href}>{l.label}</a><span class="sep">·</span>
					{/each}
					<a href="/graph?ref={encodeURIComponent(selectedDetail.ref)}">Verken vanaf deze knoop</a>
				</p>
			{:else if selectedError}
				<p class="warn">{selectedError}</p>
			{:else}
				<p class="meta">Laden…</p>
			{/if}
		</div>
	{/if}
</main>

<!-- Vaste hovercard op paginaniveau (#243/#244-patroon): buiten .viz-wrap,
     want die scroll-container zou hem clippen. pointer-events:none. -->
{#if hover}
	<div
		class="hovercard"
		class:above={hover.above}
		style="left: {hover.x}px; top: {hover.y}px;"
		role="tooltip"
	>
		{#if hoverDetail?.imageUrl}
			<img class="hc-img" src={hoverDetail.imageUrl} alt="" loading="lazy" />
		{/if}
		<div class="hc-body">
			<div class="hc-name">{trim(hoverDetail?.label ?? hover.label, 60)}</div>
			{#if hoverDetail?.summary}
				<div class="hc-desc">{hoverDetail.summary}</div>
			{:else if nodeError[hover.ref]}
				<div class="hc-desc meta">Kon deze knoop niet laden.</div>
			{:else if !hoverDetail}
				<div class="hc-desc meta">Laden…</div>
			{/if}
			<div class="hc-ref meta mono">{hover.ref}</div>
			<div class="hc-hint meta">Klik voor het detail onder de graaf</div>
		</div>
	</div>
{/if}

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
	.hint { font-size: 0.82rem; margin: 6px 0 0; }

	/* ── Selectiepaneel (#252) ─────────────────────────────────────── */
	.selected-detail { border-color: var(--accent); }
	.selected-detail .node-head { margin-bottom: 6px; }
	.close {
		margin-left: auto; background: none; border: 1px solid var(--border);
		border-radius: 999px; padding: 2px 12px; color: var(--muted);
		font: inherit; font-size: 0.78rem; cursor: pointer;
	}
	.close:hover { color: var(--text); border-color: var(--border-strong); }
	.card-detail { display: flex; gap: 16px; align-items: flex-start; flex-wrap: wrap; }
	.cd-img {
		width: 190px; max-width: 100%; border-radius: var(--radius);
		border: 1px solid var(--border); flex-shrink: 0;
	}
	.cd-body { flex: 1 1 260px; min-width: 0; display: flex; flex-direction: column; gap: 6px; }
	.cd-meta { color: var(--muted); font-size: 0.85rem; margin: 0; }
	.cd-badges { display: flex; gap: 6px; flex-wrap: wrap; margin: 0; }
	.badge {
		font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.04em;
		border-radius: 999px; padding: 2px 9px;
	}
	.badge.err { background: var(--err-soft); color: var(--err); }
	.badge.warn { background: var(--warn-soft); color: var(--warn); }
	.cd-label {
		font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.06em;
		color: var(--muted); font-weight: 700; margin: 6px 0 0;
	}
	.oracle { margin: 0; font-size: 0.9rem; line-height: 1.55; white-space: pre-wrap; }
	.oracle.printed { color: var(--muted); }
	.chips { margin: 4px 0 0; }
	.chip.plain { color: var(--muted); }
	.dossier { margin-top: 14px; border-top: 1px solid var(--border); padding-top: 10px; }
	.dos-item { margin: 4px 0 0; font-size: 0.86rem; line-height: 1.5; }
	.dos-item .meta { font-size: 0.78rem; }

	/* ── Hovercard (#243/#244-patroon) ─────────────────────────────── */
	.hovercard {
		position: fixed; z-index: 50; width: 216px;
		background: var(--surface); border: 1px solid var(--border-strong);
		border-radius: var(--radius-lg); box-shadow: var(--shadow-panel-lg);
		padding: 10px; pointer-events: none;
		display: flex; flex-direction: column; gap: 7px;
	}
	.hovercard.above { transform: translateY(-100%); }
	.hc-img {
		display: block; width: 100%; max-height: 260px;
		object-fit: contain; border-radius: var(--radius);
	}
	.hc-body { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
	.hc-name { font-weight: 700; font-size: 0.86rem; overflow-wrap: anywhere; }
	.hc-desc { font-size: 0.8rem; line-height: 1.4; }
	.hc-ref { font-size: 0.68rem; overflow-wrap: anywhere; }
	.hc-hint { font-size: 0.68rem; }
	@media (prefers-reduced-motion: no-preference) {
		.hovercard { animation: hc-in 90ms ease-out; }
		@keyframes hc-in {
			from { opacity: 0; }
			to { opacity: 1; }
		}
	}
	@media (max-width: 640px) {
		.prop { grid-template-columns: 1fr; gap: 2px; }
		/* Smaller: de afbeelding mag de kaarttekst niet onder de vouw duwen. */
		.cd-img { width: 150px; }
	}
</style>
