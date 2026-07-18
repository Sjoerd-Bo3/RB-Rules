<script lang="ts">
	import RbText from '$lib/RbText.svelte';
	import { domainColorVar } from '$lib/changeCard';
	import { useShell } from '$lib/shell.svelte';

	let { data } = $props();
	const c = $derived(data.card);
	const shell = useShell();
	// Domein-tint (#214): de kaart krijgt de kleur van haar eerste domein
	// (--dom-*), zoals de tegels in de kaartbrowser — één bron voor de mapping.
	const cardDom = $derived(domainColorVar(c.domains[0]));
	// Bronverwijzing (#166) is URL of vrije citatie — alleen linken als het
	// er echt een is, anders gewoon (geëscapete) tekst.
	const isHttp = (url: string) => /^https?:\/\//.test(url);

	// "Op deze pagina" (leesrail): alleen de dossier-secties die echt inhoud
	// hebben — zelfde patroon als de regel-leespagina.
	const onThisPage = $derived(
		[
			c.errataText || c.textPlain ? { id: 'tekst', label: 'Kaarttekst' } : null,
			c.mechanics?.length ? { id: 'mechanieken', label: 'Mechanieken' } : null,
			{ id: 'in-decks', label: 'In decks' },
			data.interactions.length ? { id: 'interacties', label: 'Interacties' } : null,
			data.rules.errata.length || data.rules.relevantRules.length
				? { id: 'regels', label: 'Regels en errata' }
				: null,
			data.dossier.rulings.length ? { id: 'rulings', label: 'Rulings' } : null,
			data.dossier.claims.length ? { id: 'claims', label: 'Community-inzichten' } : null,
			data.dossier.relations.length ? { id: 'relaties', label: 'Relaties in het brein' } : null,
			data.dossier.banHistory.length ? { id: 'ban-historie', label: 'Ban-historie' } : null,
			data.similar.length ? { id: 'vergelijkbaar', label: 'Vergelijkbare kaarten' } : null
		].filter((x) => x !== null)
	);

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

	// Contextuele rechterrail (desktop) / onder de content (mobiel).
	$effect(() => {
		shell.rail = { snippet: rail, kind: 'context', title: 'Op deze pagina' };
		return () => (shell.rail = null);
	});
</script>

<svelte:head><title>{c.name} — Poracle</title></svelte:head>

{#snippet rail()}
	{#if onThisPage.length}
		<nav class="rail-nav">
			{#each onThisPage as item (item.id)}
				<a href="#{item.id}">{item.label}</a>
			{/each}
		</nav>
	{/if}
	<div class="rail-block">
		<p class="rail-h">Domein</p>
		<div class="rail-doms">
			{#each c.domains as d (d)}
				<span class="rail-dom" style="--dc: {domainColorVar(d)}">{d}</span>
			{/each}
			{#if c.domains.length === 0}<span class="rail-dom" style="--dc: var(--dom-colorless)">Colorless</span>{/if}
		</div>
	</div>
{/snippet}

<main style="--card-dom: {cardDom}">
	<a href="/cards" class="back">← Kaarten</a>

	<div class="detail">
		{#if c.imageUrl}
			<img class="art" src={c.imageUrl} alt={c.name} />
		{/if}
		<div class="info">
			<h1>
				{c.name}
				{#if c.banned}<span class="banned">Verboden</span>{/if}
				<!-- Set-legaliteit (#22). "Verboden" domineert: een gebande kaart
				     óók "Legaal" noemen zou tegenstrijdig lezen. Bij een onbekende
				     releasedatum geen "niet legaal"-claim — dat kan net zo goed een
				     allang verschenen set zijn waarvan de sync geen datum heeft. -->
				{#if !c.banned}
					{#if c.legality === 'upcoming'}
						<span class="status upcoming">Nog niet legaal — komt {c.legalFrom ? new Date(c.legalFrom).toLocaleDateString('nl-NL') : 'binnenkort'}</span>
					{:else if c.legality === 'announced'}
						<span class="status announced">Releasedatum onbekend</span>
					{:else}
						<span class="status legal">Legaal</span>
					{/if}
				{/if}
			</h1>
			<p class="meta">
				{[c.supertype, c.type].filter(Boolean).join(' ')}
				· {c.rarity ?? '—'}
				· <a href="/cards?set={c.setId}">{c.setLabel ?? c.setId ?? '?'}</a>{c.collectorNumber ? ` #${c.collectorNumber}` : ''}
			</p>

			<p class="stats">
				{#each c.domains as d (d)}
					<a class="chip domain" style="--dc: {domainColorVar(d)}" href="/cards?domain={encodeURIComponent(d)}">{d}</a>
				{/each}
				{#if c.energy !== null}<span class="chip stat"><span class="k">Energy</span>{c.energy}</span>{/if}
				{#if c.might !== null}<span class="chip stat"><span class="k">Might</span>{c.might}</span>{/if}
				{#if c.power !== null}<span class="chip stat"><span class="k">Power</span>{c.power}</span>{/if}
			</p>

			{#if c.errataText}
				<section id="tekst">
					<h2>Actuele tekst (errata)</h2>
					<p class="oracle errata"><RbText text={c.errataText} /></p>
					{#if c.textPlain}
						<details>
							<summary class="meta">Gedrukte tekst (achterhaald)</summary>
							<p class="oracle printed"><RbText text={c.textPlain} /></p>
						</details>
					{/if}
				</section>
			{:else if c.textPlain}
				<section id="tekst">
					<h2>Kaarttekst</h2>
					<p class="oracle"><RbText text={c.textPlain} /></p>
				</section>
			{/if}

			{#if c.mechanics?.length}
				<section id="mechanieken">
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

	<!-- Deck-gebruikssignaal (#15 golf 1 spoor B): altijd zichtbaar, ook als
	     de bank nog (bijna) leeg is — de eerlijke lege/dunne staat is zelf
	     de informatie, geen reden om het blok te verbergen. -->
	<section id="in-decks">
		<h2>
			In decks
			<a class="graph-link" href="/decks?card={encodeURIComponent(c.riftboundId)}">Bekijk in de deck-browser →</a>
		</h2>
		{#if data.dossier.deckPopularity.recentDeckCount === 0}
			<p class="meta">Nog geen deckdata beschikbaar — de deck-backfill (Piltover Archive) loopt nog.</p>
		{:else if data.dossier.deckPopularity.thinData}
			<p>
				{data.dossier.deckPopularity.deckCount} van {data.dossier.deckPopularity.recentDeckCount} recente decks bevatten deze kaart.
			</p>
			<p class="meta small">Nog te weinig deckdata voor een betrouwbaar percentage.</p>
		{:else}
			<p class="deckpct"><strong>{data.dossier.deckPopularity.percentage}%</strong> van recente decks speelt deze kaart</p>
			<p class="meta small">
				{data.dossier.deckPopularity.deckCount} van {data.dossier.deckPopularity.recentDeckCount} recente decks (Piltover Archive)
			</p>
		{/if}
		{#if data.dossier.deckPopularity.deckCount > 0}
			{#if data.dossier.deckPopularity.averageCopiesWhenPlayed !== null}
				<p class="meta small">
					Gemiddeld {data.dossier.deckPopularity.averageCopiesWhenPlayed}
					{data.dossier.deckPopularity.averageCopiesWhenPlayed === 1 ? 'exemplaar' : 'exemplaren'} wanneer gespeeld.
				</p>
			{/if}
			{#if data.dossier.deckPopularity.topCoPlayed.length}
				<p class="meta small">Vaak samen gespeeld met:</p>
				<p>
					{#each data.dossier.deckPopularity.topCoPlayed as co (co.riftboundId)}
						<a class="chip" href="/cards/{co.riftboundId}">{co.name}</a>
					{/each}
				</p>
			{/if}
		{/if}
	</section>

	{#if data.interactions.length}
		<section id="interacties">
			<h2>Interacties (geverifieerd)</h2>
			{#each data.interactions as x (x.otherId + x.kind)}
				<div class="interaction">
					<a href="/cards/{x.otherId}"><strong>{x.otherName}</strong></a>
					<span class="chip kind-{x.kind}">{x.kind}</span>
					<p class="meta"><RbText text={x.explanation} /></p>
				</div>
			{/each}
		</section>
	{/if}

	{#if data.rules.errata.length || data.rules.relevantRules.length}
		<section id="regels">
			<h2>Regels en errata voor deze kaart</h2>
			{#each data.rules.errata as e, i (e.detectedAt)}
				<div class="rulebox errata-box">
					<span class="badge">Errata</span>
					{#if i > 0}<span class="badge superseded">eerdere versie</span>{/if}
					<p><RbText text={e.newText} /></p>
					<p class="meta">
						{#if e.effectiveFrom}Geldig sinds {new Date(e.effectiveFrom).toLocaleDateString('nl-NL')} · {/if}
						waargenomen {new Date(e.detectedAt).toLocaleDateString('nl-NL')}
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

	{#if data.dossier.rulings.length}
		<section id="rulings">
			<h2>Rulings over deze kaart <a class="graph-link" href="/rulings?q={encodeURIComponent(c.name)}">Alle rulings →</a></h2>
			{#each data.dossier.rulings as r (r.id)}
				<div class="rulebox">
					<span class="badge verified">Geverifieerd</span>
					{#if r.question}<p class="q">{r.question}</p>{/if}
					<p><RbText text={r.text} /></p>
					{#if r.sections.length}
						<p class="secrefs">
							{#each r.sections as s (s.sourceId + s.code)}
								<a class="badge rule" href="/rules/{encodeURIComponent(s.code)}?source={encodeURIComponent(s.sourceId)}">§ {s.code}</a>
							{/each}
						</p>
					{/if}
					<p class="meta">
						{new Date(r.date).toLocaleDateString('nl-NL')}
						{#if r.provenance}· bron: {r.provenance}{/if}
					</p>
					{#if r.sourceRef}
						<p class="meta small">
							Bron van deze ruling:
							{#if isHttp(r.sourceRef)}
								<a href={r.sourceRef} target="_blank" rel="noopener">{r.sourceRef}</a>
							{:else}
								{r.sourceRef}
							{/if}
						</p>
					{/if}
				</div>
			{/each}
		</section>
	{/if}

	{#if data.dossier.claims.length}
		<section id="claims">
			<h2>Community-inzichten <span class="meta">(geaccepteerd, met trust)</span></h2>
			{#each data.dossier.claims as cl (cl.id)}
				<div class="rulebox">
					<p><RbText text={cl.statement} /></p>
					<p class="meta trust">{cl.trustLabel}</p>
					{#each cl.sources as src (src.name + (src.url ?? ''))}
						<p class="meta small source">
							{#if src.quote}“{src.quote}” — {/if}
							{#if src.url}<a href={src.url} target="_blank" rel="noopener">{src.name}</a>{:else}{src.name}{/if}
						</p>
					{/each}
				</div>
			{/each}
			<p class="meta small">Community-interpretatie — de officiële regels en geverifieerde rulings gaan altijd voor.</p>
		</section>
	{/if}

	{#if data.dossier.relations.length}
		<section id="relaties">
			<h2>Relaties in het brein <a class="graph-link" href="/graph?ref={encodeURIComponent(`card:${c.variantOf ?? c.riftboundId}`)}">Verken in het brein →</a></h2>
			{#each data.dossier.relations as rel (rel.otherRef + rel.kind)}
				<div class="interaction">
					<a href="/graph?ref={encodeURIComponent(rel.otherRef)}"><strong>{rel.otherName ?? rel.otherRef}</strong></a>
					<span class="chip relkind">{rel.richting === 'in' ? `is doelwit van "${rel.kind}"` : rel.kind}</span>
					{#if rel.status !== 'accepted'}<span class="chip unreviewed">nog niet gereviewd</span>{/if}
					<p class="meta"><RbText text={rel.explanation} /></p>
				</div>
			{/each}
		</section>
	{/if}

	{#if data.dossier.banHistory.length}
		<section id="ban-historie">
			<h2>Ban-historie</h2>
			{#each data.dossier.banHistory as b (b.sourceUrl + b.detectedAt)}
				<div class="rulebox ban-box">
					<span class="badge banned-badge">Ban · {b.format}</span>
					<p class="meta">
						{#if b.effectiveFrom}Van kracht sinds {new Date(b.effectiveFrom).toLocaleDateString('nl-NL')} · {/if}
						waargenomen {new Date(b.detectedAt).toLocaleDateString('nl-NL')}
						· <a href={b.sourceUrl} target="_blank" rel="noopener">officiële bron</a>
					</p>
				</div>
			{/each}
		</section>
	{/if}

	{#if data.similar.length}
		<section id="vergelijkbaar">
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
							{#each s.sharedDomains as d (d)}<span class="chip domain sm" style="--dc: {domainColorVar(d)}">{d}</span>{/each}
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
	/* Domein-getint: 3px domein-rand boven de kaartafbeelding, zelfde taal als
	   de tegels in de kaartbrowser. */
	.art { width: 100%; border-radius: var(--radius-lg); border: 1px solid var(--border); border-top: 3px solid var(--card-dom); }
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
	/* Domein-chip: getint met de eigen domeinkleur (--dc, gezet per chip via
	   domainColorVar) — color-mix zodat er één bron (het token) is. */
	.chip.domain {
		color: var(--dc, var(--dom-colorless));
		background: color-mix(in srgb, var(--dc, var(--dom-colorless)) 12%, transparent);
		border-color: color-mix(in srgb, var(--dc, var(--dom-colorless)) 45%, transparent);
	}
	a.chip.domain:hover { border-color: var(--dc, var(--dom-colorless)); }
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
	.status {
		font-size: 0.78rem; vertical-align: middle; margin-left: 8px;
		border-radius: 999px; padding: 3px 10px; border: 1px solid var(--border);
	}
	.status.legal { background: var(--ok-soft); color: var(--ok); border-color: var(--ok); }
	.status.upcoming { background: var(--warn-soft); color: var(--warn); border-color: var(--warn); }
	.status.announced { color: var(--muted); }
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
	/* Dossier (#127): ankerdoelen niet onder de sticky header laten schuiven. */
	section[id] { scroll-margin-top: 70px; }
	.badge.verified { background: var(--ok-soft); color: var(--ok); }
	.badge.banned-badge { background: var(--err-soft); color: var(--err); }
	/* Supersede-signaal (#168): neutraal, geen alarmkleur — een oudere
	   errata-versie is geen fout, alleen niet meer de actuele tekst. */
	.badge.superseded { background: var(--surface-deep); color: var(--muted); margin-left: 6px; }
	.rulebox.ban-box { border-color: var(--err); }
	.rulebox .q { font-weight: 600; margin: 8px 0 2px; overflow-wrap: anywhere; }
	.rulebox .trust { font-size: 0.78rem; }
	.rulebox .source { border-left: 2px solid var(--border); padding-left: 8px; overflow-wrap: anywhere; }
	.secrefs { margin: 6px 0 2px; display: flex; gap: 6px; flex-wrap: wrap; }
	.chip.relkind { color: var(--warn); }
	.chip.unreviewed { color: var(--muted); font-size: 0.72rem; }
	.deckpct { font-size: 1.05rem; }
	.deckpct strong { color: var(--accent); font-size: 1.3rem; }

	/* Contextuele rail ("Op deze pagina") — zelfde vormtaal als de regel-
	   leespagina. */
	.rail-nav { display: flex; flex-direction: column; gap: 2px; margin-bottom: 18px; }
	.rail-nav a {
		color: var(--muted); text-decoration: none; font-size: 0.88rem;
		padding: 5px 8px; border-radius: 6px; border-left: 2px solid var(--border); font-weight: 400;
	}
	.rail-nav a:hover { color: var(--text); border-left-color: var(--accent); background: var(--surface-deep); }
	.rail-block { border-top: 1px solid var(--border); padding-top: 12px; }
	.rail-h {
		font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em;
		color: var(--muted); margin: 0 0 8px;
	}
	.rail-doms { display: flex; flex-wrap: wrap; gap: 6px; }
	.rail-dom {
		font-size: 0.78rem; font-weight: 700; border-radius: 999px; padding: 2px 10px;
		color: var(--dc); background: color-mix(in srgb, var(--dc) 14%, transparent);
		border: 1px solid color-mix(in srgb, var(--dc) 45%, transparent);
	}
</style>
