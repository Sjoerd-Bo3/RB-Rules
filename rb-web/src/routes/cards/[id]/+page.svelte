<script lang="ts">
	let { data } = $props();
	const c = $derived(data.card);

	// LLM-uitleg per vergelijkbaar paar (#30), lazy en server-side gecachet.
	let explanations = $state<Record<string, string>>({});
	let explaining = $state<string | null>(null);
	// Component wordt hergebruikt bij client-side navigatie tussen kaarten;
	// uitleg hoort bij het paar, dus resetten zodra de kaart wisselt.
	$effect(() => {
		void c.riftboundId;
		explanations = {};
		explaining = null;
	});
	async function explain(otherId: string) {
		if (explanations[otherId] || explaining) return;
		explaining = otherId;
		try {
			const r = await fetch(`/cards/${encodeURIComponent(c.riftboundId)}/explain/${encodeURIComponent(otherId)}`);
			const body = await r.json();
			explanations[otherId] = r.ok ? body.explanation : 'Uitleg tijdelijk niet beschikbaar.';
		} catch {
			explanations[otherId] = 'Uitleg tijdelijk niet beschikbaar.';
		} finally {
			explaining = null;
		}
	}
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
				{#if c.banned}<span class="banned">Verboden</span>{/if}
			</h1>
			<p class="meta">
				{[c.supertype, c.type].filter(Boolean).join(' ')}
				· {c.rarity ?? '—'}
				· <a href="/cards?set={c.setId}">{c.setLabel ?? c.setId ?? '?'}</a>{c.collectorNumber ? ` #${c.collectorNumber}` : ''}
			</p>

			<p class="stats">
				{#each c.domains as d (d)}
					<a class="chip domain" href="/cards?domain={encodeURIComponent(d)}">{d}</a>
				{/each}
				{#if c.energy !== null}<span class="chip stat"><span class="k">Energy</span>{c.energy}</span>{/if}
				{#if c.might !== null}<span class="chip stat"><span class="k">Might</span>{c.might}</span>{/if}
				{#if c.power !== null}<span class="chip stat"><span class="k">Power</span>{c.power}</span>{/if}
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
					<p>
						{#each c.mechanics as m (m)}
							<a class="chip mech" href="/cards?mechanic={encodeURIComponent(m)}">{m}</a>
						{/each}
					</p>
				</section>
			{/if}

			{#if c.triggers?.length || c.effects?.length}
				<section>
					<h2>Triggers en effecten</h2>
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

			{#if c.versions.length}
				<section>
					<h2>Andere versies <span class="meta">({c.versions.length} — alt-art, showcase of herdruk; zelfde kaart in het spel)</span></h2>
					<div class="versions">
						{#each c.versions as v (v.riftboundId)}
							<a class="version" href="/cards/{v.riftboundId}" title="{v.setLabel ?? v.setId} · {v.rarity ?? ''}">
								{#if v.imageUrl}<img src={v.imageUrl} alt="{c.name} ({v.setId})" loading="lazy" />{/if}
								<span class="meta">{v.setId}{v.collectorNumber ? ` #${v.collectorNumber}` : ''} · {v.rarity ?? '—'}</span>
							</a>
						{/each}
					</div>
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

	{#if data.rules.errata.length || data.rules.relevantRules.length}
		<section>
			<h2>Regels en errata voor deze kaart</h2>
			{#each data.rules.errata as e (e.detectedAt)}
				<div class="rulebox errata-box">
					<span class="badge">Errata</span>
					<p>{e.newText}</p>
					<p class="meta">
						{new Date(e.detectedAt).toLocaleDateString('nl-NL')}
						{#if e.sourceUrl}· <a href={e.sourceUrl} target="_blank" rel="noopener">bron</a>{/if}
					</p>
				</div>
			{/each}
			{#each data.rules.relevantRules as r (r.section)}
				<div class="rulebox">
					<a class="badge rule" href="/rules/{encodeURIComponent(r.section)}">§ {r.section}</a>
					<p>{r.snippet}…</p>
					<p class="meta">
						<a href="/rules/{encodeURIComponent(r.section)}">Lees hele sectie →</a>
						· <a href={r.url} target="_blank" rel="noopener">{r.sourceName}</a>
					</p>
				</div>
			{/each}
			<p class="meta small">Regelsecties zijn semantisch gematcht op de kaarttekst — de meest relevante paragrafen uit de officiële rules.</p>
		</section>
	{/if}

	{#if data.similar.length}
		<section>
			<h2>Vergelijkbare kaarten <a class="graph-link" href="/graph?card={c.riftboundId}">Bekijk in graph →</a></h2>
			<div class="grid">
				{#each data.similar as s (s.riftboundId)}
					<div class="mini">
						<a href="/cards/{s.riftboundId}">
							{#if s.imageUrl}<img src={s.imageUrl} alt={s.name} loading="lazy" />{/if}
							<span class="mini-name">{s.name} <span class="pct">{s.similarity}%</span></span>
						</a>
						<span class="why">
							{#each s.sharedMechanics as m (m)}<span class="chip mech sm">{m}</span>{/each}
							{#each s.sharedDomains as d (d)}<span class="chip domain sm">{d}</span>{/each}
							{#if s.sameType}<span class="chip sm">zelfde type</span>{/if}
						</span>
						{#if explanations[s.riftboundId]}
							<p class="explain">{explanations[s.riftboundId]}</p>
						{:else}
							<button class="why-btn" disabled={explaining !== null} onclick={() => explain(s.riftboundId)}>
								{explaining === s.riftboundId ? 'Analyseren…' : 'Waarom vergelijkbaar?'}
							</button>
						{/if}
					</div>
				{/each}
			</div>
			<p class="meta small">Het percentage is tekst-gelijkenis (embedding); de chips tonen wat de kaarten concreet delen.</p>
		</section>
	{/if}
</main>

<style>
	main { max-width: 1080px; margin: 0 auto; padding: 24px 20px; }
	.back { color: var(--muted); text-decoration: none; }
	.detail { display: grid; grid-template-columns: minmax(220px, 320px) 1fr; gap: 24px; margin-top: 16px; }
	@media (max-width: 640px) { .detail { grid-template-columns: 1fr; } }
	.art { width: 100%; border-radius: var(--radius-lg); border: 1px solid var(--border); }
	h1 { margin: 0 0 4px; }
	h2 { font-size: 1rem; color: var(--accent); margin: 18px 0 6px; }
	.meta { color: var(--muted); }
	.oracle { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 12px 14px; }
	.chip {
		display: inline-block; background: var(--surface); border: 1px solid var(--border);
		border-radius: 999px; padding: 2px 10px; margin: 0 6px 6px 0; font-size: 0.85rem;
		text-decoration: none;
	}
	a.chip:hover { border-color: var(--accent); }
	.chip.domain { color: var(--warn); }
	.chip.mech { color: var(--ok); }
	.chip.tag { color: var(--muted); }
	.chip.stat .k {
		color: var(--muted); font-size: 0.72rem; text-transform: uppercase;
		letter-spacing: 0.05em; margin-right: 6px;
	}
	.banned {
		font-size: 0.78rem; vertical-align: middle; margin-left: 8px;
		background: var(--err-soft); color: var(--err); border: 1px solid var(--err);
		border-radius: 999px; padding: 3px 10px; text-transform: uppercase; letter-spacing: 0.05em;
	}
	.oracle.errata { border-color: var(--accent); }
	.oracle.printed { opacity: 0.6; }
	.versions { display: flex; gap: 10px; flex-wrap: wrap; }
	.version { width: 110px; display: flex; flex-direction: column; gap: 2px; text-decoration: none; }
	.version img { width: 100%; border-radius: 8px; border: 1px solid var(--border); }
	.version:hover img { border-color: var(--accent); }
	.version .meta { font-size: 0.72rem; }
	.interaction { border-bottom: 1px solid var(--border); padding: 8px 0; }
	.interaction a { color: var(--text); }
	.chip.kind-combo { color: var(--ok); }
	.chip.kind-counter { color: var(--err); }
	.chip.kind-synergy { color: var(--warn); }
	.chip.kind-nonbo { color: var(--muted); }
	.grid { display: grid; gap: 12px; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); }
	.mini { color: inherit; font-size: 0.85rem; display: flex; flex-direction: column; gap: 3px; }
	.mini > a { color: inherit; text-decoration: none; display: flex; flex-direction: column; gap: 3px; }
	.mini img { width: 100%; border-radius: 10px; border: 1px solid var(--border); }
	.graph-link { font-size: 0.8rem; font-weight: 400; color: var(--muted); margin-left: 10px; text-decoration: none; }
	.graph-link:hover { color: var(--accent); }
	.why-btn {
		background: transparent; color: var(--muted); border: 1px solid var(--border);
		border-radius: 6px; padding: 3px 8px; font-size: 0.72rem; cursor: pointer; align-self: flex-start;
	}
	.why-btn:hover { color: var(--text); border-color: var(--border-strong); }
	.why-btn:disabled { opacity: 0.5; }
	.explain { margin: 2px 0 0; font-size: 0.78rem; color: var(--muted); font-style: italic; }
	.mini-name { font-weight: 600; }
	.pct { color: var(--accent); font-weight: 700; font-size: 0.78rem; }
	.why .chip.sm { font-size: 0.68rem; padding: 1px 7px; margin: 0 4px 4px 0; }
	.rulebox { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 10px 14px; margin-bottom: 10px; }
	.rulebox.errata-box { border-color: var(--accent); }
	.rulebox p { margin: 6px 0 2px; }
	.badge {
		font-size: 0.7rem; font-weight: 700; letter-spacing: 0.05em; text-transform: uppercase;
		background: var(--accent-soft); color: var(--warn); border-radius: 999px; padding: 2px 9px;
		text-decoration: none;
	}
	.badge.rule { background: var(--ok-soft); color: var(--ok); }
	.small { font-size: 0.78rem; }
	.meta a { color: var(--muted); }
</style>
