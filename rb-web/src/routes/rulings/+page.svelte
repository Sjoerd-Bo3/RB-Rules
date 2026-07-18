<script lang="ts">
	import { navigating } from '$app/state';
	import RbText from '$lib/RbText.svelte';
	import { useShell } from '$lib/shell.svelte';
	import type { RulingsItem } from './+page.server';

	let { data } = $props();
	const shell = useShell();

	// Schrijfbaar afgeleid: typen behoudt de invoer, navigatie (form GET →
	// nieuwe ?q=) reset naar de URL-waarde (route-hergebruik, zelfde patroon
	// als /rules).
	let q = $derived(data.q);
	const busy = $derived(navigating.to?.url.pathname === '/rulings');

	const TOPICS = [
		{ key: 'card', label: 'Kaart' },
		{ key: 'mechanic', label: 'Mechaniek' },
		{ key: 'section', label: 'Regelsectie' },
		{ key: 'concept', label: 'Concept' },
		{ key: 'answer', label: 'Vraag & antwoord' }
	];
	const topicLabel = $derived(TOPICS.find((t) => t.key === data.topic)?.label ?? data.topic);

	/** Filter-/paginalinks behouden de rest van de querystring. */
	function href(params: { topic?: string; page?: number }): string {
		const p = new URLSearchParams();
		if (data.q) p.set('q', data.q);
		const topic = params.topic ?? data.topic;
		if (topic) p.set('topic', topic);
		if (params.page && params.page > 1) p.set('page', String(params.page));
		const qs = p.toString();
		return qs ? `/rulings?${qs}` : '/rulings';
	}

	/** Onderwerp van een item als klikbare bestemming, waar die bestaat. */
	function topicHref(item: RulingsItem): string | null {
		if (item.topic === 'card')
			return item.cardId
				? `/cards/${encodeURIComponent(item.cardId)}`
				: item.topicRef
					? `/cards?q=${encodeURIComponent(item.topicRef)}`
					: null;
		if (item.topic === 'mechanic' && item.topicRef)
			return `/cards?mechanic=${encodeURIComponent(item.topicRef)}`;
		if (item.topic === 'concept') return '/primer';
		return null; // secties zijn al klikbaar via de §-verwijzingen
	}

	function anchor(ref: string): string {
		return ref.replace(':', '-');
	}

	// Bronverwijzing (#166) is URL of vrije citatie — alleen linken als het
	// er echt een is (zelfde patroon als /ask).
	const isHttp = (url: string) => /^https?:\/\//.test(url);

	const lastPage = $derived(Math.max(1, Math.ceil(data.total / data.pageSize)));

	// Onderwerp-filter in de rechterrail (desktop) / bottom-sheet (mobiel) —
	// feed-patroon #214, nooit een horizontaal scrollende chip-rij.
	$effect(() => {
		shell.rail = {
			snippet: filters,
			kind: 'filters',
			count: data.topic ? 1 : 0,
			title: 'Filters'
		};
		return () => (shell.rail = null);
	});
</script>

<svelte:head>
	<title>Rulings — Poracle</title>
	<meta
		name="description"
		content="Doorzoekbare databank van geverifieerde Riftbound-rulings en officieel bevestigde community-inzichten, met bronnen en regelverwijzingen."
	/>
</svelte:head>

{#snippet filters()}
	<div class="fgroup">
		<p class="fglabel">Onderwerp-type</p>
		<div class="chips">
			<a class="chip" class:on={!data.topic} href={href({ topic: '' })}>Alles</a>
			{#each TOPICS as t (t.key)}
				<a class="chip" class:on={data.topic === t.key} href={href({ topic: t.key })}>{t.label}</a>
			{/each}
		</div>
	</div>
{/snippet}

<main>
	<h1>Rulings<span>databank</span></h1>
	<p class="subtitle">
		Geverifieerde rulings en officieel bevestigde community-inzichten, met per item de volledige
		bewijsketen: bron, citaat en klikbare §-verwijzingen.
	</p>

	{#if data.apiDown}
		<p class="warn">rb-api is niet bereikbaar.</p>
	{:else}
		<form method="GET" class="search">
			<input
				type="search"
				name="q"
				bind:value={q}
				placeholder="Zoek op betekenis (bijv. 'deflect targeting' of 'combat damage')"
			/>
			{#if data.topic}<input type="hidden" name="topic" value={data.topic} />{/if}
			<button type="submit" disabled={busy}>{busy ? 'Zoeken…' : 'Zoek'}</button>
		</form>

		{#if data.topic}
			<div class="active-chips">
				<a class="active-chip" href={href({ topic: '' })}>type: {topicLabel} ✕</a>
			</div>
		{/if}

		{#if data.degraded}
			<p class="meta small">
				Semantisch zoeken is even niet beschikbaar — er is alleen op woorden gezocht.
			</p>
		{/if}

		{#if data.items.length === 0}
			{#if data.searching}
				<p class="meta">
					Niets gevonden voor "{data.q}" — er wordt ook op betekenis gezocht, probeer een andere
					omschrijving of haal het onderwerp-filter weg.
				</p>
			{:else if data.total === 0}
				<p class="meta">
					Nog geen items in de databank{data.topic ? ' voor dit onderwerp-type' : ''} — rulings
					verschijnen hier zodra ze in het beheer geverifieerd zijn.
				</p>
			{/if}
		{:else}
			<p class="meta count tnum">
				{#if data.searching}
					{data.items.length} resultaten voor "{data.q}"
				{:else}
					{data.total} items · pagina {data.page} van {lastPage}
				{/if}
			</p>

			<div class="items">
				{#each data.items as item (item.ref)}
					<article class="item panel" id={anchor(item.ref)}>
						<header>
							<span class="badge {item.kind}">
								{item.kind === 'ruling' ? 'Geverifieerde ruling' : 'Bevestigde claim'}
							</span>
							{#if item.topicRef && item.topic !== 'answer'}
								{@const dest = topicHref(item)}
								{#if dest}
									<a class="topic" href={dest}>{item.topicRef}</a>
								{:else}
									<span class="topic">{item.topicRef}</span>
								{/if}
							{/if}
							<a class="perma" href="#{anchor(item.ref)}" aria-label="Link naar dit item">#</a>
							<time class="meta tnum" datetime={item.date}>
								{new Date(item.date).toLocaleDateString('nl-NL')}
							</time>
						</header>

						{#if item.question}
							<p class="question">{item.question}</p>
						{/if}
						<p class="text"><RbText text={item.text} /></p>

						<p class="meta trust">{item.trustLabel}</p>

						{#if item.sections.length}
							<p class="refs">
								{#each item.sections as s (s.sourceId + s.code)}
									<a
										class="chip rule"
										href="/rules/{encodeURIComponent(s.code)}?source={encodeURIComponent(
											s.sourceId
										)}">§ {s.code}</a
									>
								{/each}
							</p>
						{/if}

						{#if item.provenance}
							<p class="meta small">Bron: {item.provenance}</p>
						{/if}
						{#if item.sourceRef}
							<p class="meta small">
								Bron van deze ruling:
								{#if isHttp(item.sourceRef)}
									<a href={item.sourceRef} target="_blank" rel="noopener">{item.sourceRef}</a>
								{:else}
									{item.sourceRef}
								{/if}
							</p>
						{/if}
						{#each item.sources as src (src.name + (src.url ?? ''))}
							<div class="source">
								{#if src.quote}<blockquote>“{src.quote}”</blockquote>{/if}
								<p class="meta small">
									{#if src.url}
										<a href={src.url} target="_blank" rel="noopener">{src.name}</a>
									{:else}
										{src.name}
									{/if}
									· trust-tier {src.trustTier}
								</p>
							</div>
						{/each}
					</article>
				{/each}
			</div>

			{#if !data.searching && lastPage > 1}
				<div class="pager">
					{#if data.page > 1}
						<a href={href({ page: data.page - 1 })}>← Nieuwer</a>
					{:else}<span></span>{/if}
					{#if data.page < lastPage}
						<a href={href({ page: data.page + 1 })}>Ouder →</a>
					{/if}
				</div>
			{/if}
		{/if}
	{/if}
</main>

<style>
	main { max-width: 860px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: var(--accent); }
	.subtitle { color: var(--muted); }
	.search { display: flex; gap: 8px; margin: 10px 0 12px; }
	input[type='search'] {
		flex: 1; min-width: 0; box-sizing: border-box; background: var(--surface-deep);
		color: var(--text); border: 1px solid var(--border); border-radius: 10px;
		padding: 10px 14px; font-size: 1rem;
	}
	.search button {
		background: var(--accent); color: var(--accent-ink); border: 0; border-radius: 10px;
		padding: 10px 16px; font-weight: 600; cursor: pointer; flex-shrink: 0;
	}
	.search button:disabled { opacity: 0.6; cursor: wait; }
	.meta { color: var(--muted); }
	.small { font-size: 0.8rem; }
	.count { font-size: 0.85rem; margin: 0 0 10px; }
	.items { display: grid; gap: 12px; }
	.item {
		padding: 14px 16px;
		/* Ankerdoel: niet onder de sticky header verdwijnen. */
		scroll-margin-top: 70px;
	}
	.item header { display: flex; align-items: baseline; gap: 8px 10px; flex-wrap: wrap; }
	.badge {
		font-size: 0.7rem; font-weight: 700; letter-spacing: 0.05em; text-transform: uppercase;
		border-radius: 999px; padding: 2px 9px;
	}
	.badge.ruling { background: var(--ok-soft); color: var(--ok); }
	.badge.claim { background: var(--warn-soft); color: var(--warn); }
	.topic { font-weight: 600; color: var(--text); text-decoration: none; overflow-wrap: anywhere; }
	a.topic:hover { color: var(--accent); }
	.perma { color: var(--muted); text-decoration: none; margin-left: auto; }
	.perma:hover { color: var(--accent); }
	.item time { font-size: 0.8rem; }
	.question { font-weight: 600; margin: 10px 0 4px; overflow-wrap: anywhere; }
	.text { margin: 6px 0; white-space: pre-wrap; overflow-wrap: anywhere; }
	.trust { font-size: 0.8rem; margin: 4px 0; }
	.refs { margin: 8px 0 0; }
	.chip.rule {
		display: inline-block; background: var(--ok-soft); color: var(--ok);
		border: 1px solid transparent; border-radius: 999px; padding: 2px 10px;
		font-size: 0.8rem; text-decoration: none; margin: 0 6px 6px 0;
	}
	.chip.rule:hover { border-color: var(--ok); }
	.source { border-left: 2px solid var(--border); padding-left: 10px; margin-top: 8px; }
	.source blockquote {
		margin: 0 0 2px; color: var(--muted); font-style: italic; font-size: 0.9rem;
		overflow-wrap: anywhere;
	}
	.source p { margin: 0; }
	.source a { color: var(--muted); }
	.pager { display: flex; justify-content: space-between; margin-top: 18px; }
	.pager a { color: var(--text); text-decoration: none; font-weight: 600; }
	.pager a:hover { color: var(--accent); }
	.warn { color: var(--err); }

	/* Actieve filters als verwijderbare chips (feed-patroon). */
	.active-chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 0 0 14px; }
	.active-chip {
		background: var(--surface-deep); color: var(--text); border: 1px solid var(--border);
		border-radius: 999px; padding: 4px 12px; font-size: 0.8rem; text-decoration: none;
	}
	.active-chip:hover { border-color: var(--border-strong); }

	/* Filter-inhoud (gedeeld door rail + bottom-sheet) */
	.fgroup { margin-bottom: 14px; }
	.fglabel {
		margin: 0 0 6px; font-size: 0.72rem; font-weight: 700; text-transform: uppercase;
		letter-spacing: 0.05em; color: var(--muted);
	}
	.chips { display: flex; flex-wrap: wrap; gap: 6px; }
	.chip {
		display: inline-block; background: var(--surface); color: var(--muted);
		border: 1px solid var(--border); border-radius: 999px; padding: 5px 12px;
		font-size: 0.8rem; text-decoration: none;
	}
	.chip:hover { border-color: var(--border-strong); color: var(--text); }
	.chip.on { background: var(--accent); color: var(--accent-ink); border-color: var(--accent); font-weight: 700; }
</style>
