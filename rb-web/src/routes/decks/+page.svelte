<script lang="ts">
	import { enhance } from '$app/forms';
	import { navigating } from '$app/state';
	import { useShell } from '$lib/shell.svelte';

	let { data, form } = $props();
	const shell = useShell();

	const busy = $derived(navigating.to?.url.pathname === '/decks');
	const lastPage = $derived(Math.max(1, Math.ceil(data.result.total / data.result.pageSize)));

	const SORTS = [
		{ key: 'recent', label: 'Recent bijgewerkt' },
		{ key: 'views', label: 'Meeste views' },
		{ key: 'likes', label: 'Meeste likes' }
	];

	const LEGALITIES = [
		{ key: '', label: 'Alle decks' },
		{ key: 'legal', label: 'Alleen legaal' },
		{ key: 'illegal', label: 'Alleen niet legaal' },
		{ key: 'incomplete', label: 'Alleen onvolledig' }
	];

	const LEGALITY_LABEL: Record<string, string> = {
		legal: 'Legaal',
		illegal: 'Niet legaal',
		incomplete: 'Onvolledig te beoordelen'
	};
	const LEGALITY_BADGE: Record<string, string> = {
		legal: 'ok-b',
		illegal: 'err',
		incomplete: 'warn-b'
	};
	const REASON_LABEL: Record<string, string> = {
		'not-yet-legal': 'kaart komt uit een set die nog niet legaal is',
		banned: 'kaart staat op de banlijst'
	};

	// De secties zoals het deck-code-formaat ze kent (#264) — bewust niet de
	// zeven PA-secties: die indeling zit niet in een deck-code.
	const CODE_SECTION_LABEL: Record<string, string> = {
		maindeck: 'Hoofddeck',
		sideboard: 'Sideboard',
		'chosen-champion': 'Chosen champion'
	};

	const cardFilter = $derived(data.result.cardFilter);
	// Schrijfbaar afgeleid: typen behoudt de invoer, navigatie (form GET →
	// nieuwe ?q=) reset naar de URL-waarde (zelfde patroon als /rulings).
	let q = $derived(data.q);
	const decoded = $derived(form?.decoded ?? null);

	function href(params: {
		domain?: string;
		sort?: string;
		page?: number;
		card?: string;
		legality?: string;
		q?: string;
	}): string {
		const p = new URLSearchParams();
		const domain = params.domain ?? data.domain;
		const sort = params.sort ?? data.sort;
		// Het kaart-filter reist standaard mee met paginering en facetten;
		// een expliciete lege string (filter wissen) laat het weg.
		const card = params.card ?? data.card;
		const legality = params.legality ?? data.legality;
		const search = params.q ?? data.q;
		if (domain) p.set('domain', domain);
		if (sort && sort !== 'recent') p.set('sort', sort);
		if (params.page && params.page > 1) p.set('page', String(params.page));
		if (card) p.set('card', card);
		if (legality) p.set('legality', legality);
		if (search) p.set('q', search);
		const qs = p.toString();
		return qs ? `/decks?${qs}` : '/decks';
	}

	function formatDate(iso: string | null): string | null {
		return iso ? new Date(iso).toLocaleDateString('nl-NL') : null;
	}

	// Filters (domein + legaliteit + sortering) in de rechterrail (desktop) /
	// bottom-sheet (mobiel) — feed-patroon #214. Sortering is een ordening,
	// geen filter, en telt dus niet mee in de teller.
	const activeCount = $derived((data.domain ? 1 : 0) + (data.legality ? 1 : 0));
	$effect(() => {
		shell.rail = { snippet: filters, kind: 'filters', count: activeCount, title: 'Filters' };
		return () => (shell.rail = null);
	});
</script>

<svelte:head>
	<title>Decks — Poracle</title>
	<meta
		name="description"
		content="Community-decks van Piltover Archive, doorzoekbaar met legaliteitscheck tegen de actuele sets en banlijst — met attributie en deep-link terug."
	/>
</svelte:head>

{#snippet filters()}
	<!-- GET-form: kaart-filter en zoekterm reizen mee als verborgen velden
	     zodat filteren een lopende selectie behoudt. -->
	<form method="GET" class="filter-form">
		{#if data.card}<input type="hidden" name="card" value={data.card} />{/if}
		{#if data.q}<input type="hidden" name="q" value={data.q} />{/if}
		<label>
			<span>Domein</span>
			<select name="domain" value={data.domain}>
				<option value="">Alle domeinen</option>
				{#each data.facets.domains as d (d)}<option value={d}>{d}</option>{/each}
			</select>
		</label>
		<label>
			<span>Legaliteit</span>
			<select name="legality" value={data.legality}>
				{#each LEGALITIES as l (l.key)}<option value={l.key}>{l.label}</option>{/each}
			</select>
		</label>
		<label>
			<span>Sortering</span>
			<select name="sort" value={data.sort}>
				{#each SORTS as s (s.key)}<option value={s.key}>{s.label}</option>{/each}
			</select>
		</label>
		<div class="filter-actions">
			<a href={data.card ? `/decks?card=${encodeURIComponent(data.card)}` : '/decks'} class="link-btn">Reset</a>
			<button type="submit" onclick={() => (shell.sheetOpen = false)}>Toon decks</button>
		</div>
	</form>
{/snippet}

<main>
	<h1>Decks <span>van Piltover Archive</span></h1>
	<p class="subtitle">
		Wij bouwen geen eigen deckbuilder — dit spiegelt de publieke community-decks van
		<a href="https://piltoverarchive.com" target="_blank" rel="noopener">Piltover Archive</a>, met per
		deck een legaliteitscheck tegen de actuele sets en banlijst en een link terug naar de bron.
	</p>

	{#if cardFilter}
		<p class="card-filter">
			Gefilterd op kaart:
			<a href="/cards/{cardFilter.canonicalId}">{cardFilter.name ?? cardFilter.canonicalId}</a>
			<a class="clear" href={href({ page: 1, card: '' })}>filter wissen</a>
		</p>
	{/if}

	<!-- Deck-code plakken (#264): leest een gedeelde code uit en toont welk
	     deck erin zit, met hetzelfde legaliteitsoordeel als de lijst. -->
	<section class="panel code-import">
		<h2>Deck-code plakken</h2>
		<p class="meta">
			Heb je een gedeelde deck-code? Plak hem hier om te zien welke kaarten erin zitten en of het
			deck legaal is. Wij slaan niets op — dit is puur uitlezen.
		</p>
		<form
			method="POST"
			action="?/decode"
			class="code-form"
			use:enhance={() =>
				// Alleen het formulierresultaat toepassen: de deck-lijst eromheen
				// hoeft niet opnieuw opgehaald te worden, en de geplakte code
				// blijft staan (reset: false).
				async ({ update }) => await update({ reset: false, invalidateAll: false })}
		>
			<input
				type="text"
				name="code"
				value={form?.code ?? ''}
				spellcheck="false"
				autocapitalize="off"
				autocomplete="off"
				placeholder="Bijv. CMAAAAAAAAAQCAAAA4AACAIAAB…"
				aria-label="Deck-code"
			/>
			<button type="submit">Deck uitlezen</button>
		</form>

		{#if form?.decodeError}
			<p class="warn code-error">{form.decodeError}</p>
		{/if}

		{#if decoded}
			<div class="decoded">
				<header>
					<strong class="tnum">{decoded.cardCount} kaarten</strong>
					<span class="badge {LEGALITY_BADGE[decoded.legality.status]}">
						{LEGALITY_LABEL[decoded.legality.status]}
					</span>
				</header>

				{#if decoded.legality.status === 'illegal'}
					<ul class="issues">
						{#each decoded.legality.issues as issue (issue.cardCode + issue.reason)}
							<li>
								<strong>{issue.cardName ?? issue.cardCode}</strong> — {REASON_LABEL[issue.reason] ??
									issue.reason}
							</li>
						{/each}
					</ul>
				{:else if decoded.legality.status === 'incomplete'}
					<p class="meta">
						Onvolledig te beoordelen — {decoded.legality.unknownCount}
						{decoded.legality.unknownCount === 1 ? 'kaart is' : 'kaarten zijn'} niet gekoppeld aan onze
						kaartendatabank of komt/komen uit een set zonder bekende releasedatum. Geen aangetoonde
						overtreding, maar ook geen garantie.
					</p>
				{:else}
					<p class="meta">Alle kaarten zitten in een legale set en staan niet op de banlijst.</p>
				{/if}

				{#each decoded.sections as section (section.section)}
					<h3>{CODE_SECTION_LABEL[section.section] ?? section.section}</h3>
					<ul class="code-cards">
						{#each section.cards as card (card.cardCode)}
							<li>
								<span class="qty tnum">{card.quantity}×</span>
								{#if card.canonicalRiftboundId}
									<a href="/cards/{card.canonicalRiftboundId}">{card.cardName ?? card.cardCode}</a>
								{:else}
									<span class="unlinked">{card.cardCode}</span>
									<span class="meta note">niet in onze databank</span>
								{/if}
							</li>
						{/each}
					</ul>
				{/each}
				<p class="meta note">
					Een deck-code kent alleen hoofddeck, sideboard en chosen champion — de indeling in legend,
					champions, battlefields en runes zit er niet in.
				</p>
			</div>
		{/if}
	</section>

	<form method="GET" class="search">
		<input type="search" name="q" bind:value={q} placeholder="Zoek op deck-, legend- of championnaam" />
		{#if data.domain}<input type="hidden" name="domain" value={data.domain} />{/if}
		{#if data.legality}<input type="hidden" name="legality" value={data.legality} />{/if}
		{#if data.sort !== 'recent'}<input type="hidden" name="sort" value={data.sort} />{/if}
		{#if data.card}<input type="hidden" name="card" value={data.card} />{/if}
		<button type="submit" disabled={busy}>{busy ? 'Zoeken…' : 'Zoek'}</button>
	</form>

	<!-- Eén klik naar alleen legale decks: dat is waar de lijst als
	     inspiratiebron bruikbaar wordt (#265). -->
	<div class="quick-filter">
		<a
			class="active-chip"
			class:on={data.legality === 'legal'}
			href={href({ legality: data.legality === 'legal' ? '' : 'legal', page: 1 })}
			aria-current={data.legality === 'legal' ? 'true' : undefined}
		>
			Alleen legale decks
		</a>
	</div>

	{#if data.domain || data.legality || data.q}
		<div class="active-chips">
			{#if data.domain}
				<a class="active-chip" href={href({ domain: '', page: 1 })}>domein: {data.domain} ✕</a>
			{/if}
			{#if data.legality}
				<a class="active-chip" href={href({ legality: '', page: 1 })}>
					legaliteit: {LEGALITY_LABEL[data.legality]} ✕
				</a>
			{/if}
			{#if data.q}
				<a class="active-chip" href={href({ q: '', page: 1 })}>zoekterm: {data.q} ✕</a>
			{/if}
		</div>
	{/if}

	{#if data.error}
		<p class="warn">{data.error}</p>
	{:else if busy}
		<div class="loading"><span class="spin"></span> Decks laden…</div>
	{:else if data.result.items.length === 0}
		<p class="meta">
			Geen decks gevonden{data.q ? ` voor "${data.q}"` : ''}{cardFilter
				? ' met deze kaart'
				: ''}{data.domain ? ' in dit domein' : ''}{data.legality
				? ` met legaliteit "${LEGALITY_LABEL[data.legality]}"`
				: ''}.
			{#if data.legality || data.q}Probeer een filter weg te halen.{/if}
		</p>
	{:else}
		<p class="meta count tnum">{data.result.total} decks · pagina {data.result.page} van {lastPage}</p>
		<div class="grid">
			{#each data.result.items as deck (deck.id)}
				<a class="card panel" href="/decks/{deck.id}">
					<header>
						<strong>{deck.name ?? '(naamloos deck)'}</strong>
						<span class="badge {deck.legality.status === 'legal'
							? 'ok-b'
							: deck.legality.status === 'illegal'
								? 'err'
								: 'warn-b'}">
							{LEGALITY_LABEL[deck.legality.status]}
						</span>
					</header>
					<p class="meta domains">{deck.domains.join(' / ') || '—'}</p>
					<p class="meta stats tnum">
						{deck.cardCount} kaarten · {deck.views} views · {deck.likes} likes
						{#if formatDate(deck.paUpdatedAt)}· bijgewerkt {formatDate(deck.paUpdatedAt)}{/if}
					</p>
				</a>
			{/each}
		</div>

		{#if lastPage > 1}
			<div class="pager">
				{#if data.page > 1}
					<a href={href({ page: data.page - 1 })}>← Vorige</a>
				{:else}<span></span>{/if}
				{#if data.page < lastPage}
					<a href={href({ page: data.page + 1 })}>Volgende →</a>
				{/if}
			</div>
		{/if}
	{/if}
</main>

<style>
	main {
		max-width: 1080px;
		margin: 0 auto;
		padding: 24px 20px;
	}
	h1 span {
		color: var(--accent);
		font-weight: 400;
		font-size: 1.05rem;
		margin-left: 4px;
	}
	.subtitle {
		color: var(--muted);
	}
	.subtitle a {
		color: var(--accent);
	}
	.card-filter {
		background: var(--surface);
		border: 1px solid var(--border);
		border-radius: 8px;
		padding: 8px 12px;
		margin: 14px 0 0;
		color: var(--muted);
		font-size: 0.9rem;
	}
	.card-filter a {
		color: var(--accent);
		text-decoration: none;
		font-weight: 600;
	}
	.card-filter a:hover {
		text-decoration: underline;
	}
	.card-filter .clear {
		color: var(--muted);
		font-weight: 400;
		margin-left: 8px;
	}
	.loading {
		display: flex;
		align-items: center;
		gap: 10px;
		color: var(--muted);
		padding: 30px 0;
	}
	.meta {
		color: var(--muted);
	}
	.count {
		font-size: 0.85rem;
		margin: 14px 0 10px;
	}
	.warn {
		color: var(--err);
	}
	.grid {
		display: grid;
		gap: 12px;
		grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
	}
	.card {
		padding: 14px 16px;
		display: flex;
		flex-direction: column;
		gap: 6px;
		color: inherit;
		text-decoration: none;
	}
	.card:hover {
		border-color: var(--border-strong);
	}
	.card header {
		display: flex;
		align-items: baseline;
		justify-content: space-between;
		gap: 8px;
	}
	.card header strong {
		overflow-wrap: anywhere;
	}
	.badge {
		flex-shrink: 0;
	}
	.domains {
		font-size: 0.9rem;
	}
	.stats {
		font-size: 0.8rem;
	}
	.pager {
		display: flex;
		justify-content: space-between;
		margin-top: 18px;
	}
	.pager a {
		color: var(--text);
		text-decoration: none;
		font-weight: 600;
	}
	.pager a:hover {
		color: var(--accent);
	}

	/* Actieve filters als verwijderbare chips (feed-patroon). */
	.active-chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 14px 0 0; }
	.active-chip {
		background: var(--surface-deep); color: var(--text); border: 1px solid var(--border);
		border-radius: 999px; padding: 4px 12px; font-size: 0.8rem; text-decoration: none;
		overflow-wrap: anywhere;
	}
	.active-chip:hover { border-color: var(--border-strong); }
	/* Eén-klik-legaliteitsfilter: dezelfde chipvorm, aan-staat via de accent-tint. */
	.quick-filter { display: flex; flex-wrap: wrap; gap: 6px; margin: 14px 0 0; }
	.quick-filter .active-chip.on {
		background: var(--accent); color: var(--accent-ink); border-color: var(--accent);
		font-weight: 700;
	}

	/* Zoeken op deck-/legend-/championnaam (#265). */
	.search { display: flex; gap: 8px; margin: 14px 0 0; }
	.search input[type='search'] {
		flex: 1; min-width: 0; box-sizing: border-box; background: var(--surface-deep);
		color: var(--text); border: 1px solid var(--border); border-radius: 10px;
		padding: 10px 14px; font-size: 1rem;
	}
	.search button {
		background: var(--accent); color: var(--accent-ink); border: 0; border-radius: 10px;
		padding: 10px 18px; font-weight: 700; cursor: pointer;
	}
	.search button:disabled { opacity: 0.6; cursor: default; }

	/* Deck-code-import (#264). */
	.code-import { padding: 14px 16px; margin: 14px 0 0; }
	.code-import h2 { margin: 0 0 4px; font-size: 1rem; }
	.code-import .meta { font-size: 0.85rem; margin: 0; }
	.code-form { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 10px; }
	.code-form input {
		flex: 1 1 220px; min-width: 0; box-sizing: border-box; background: var(--surface-deep);
		color: var(--text); border: 1px solid var(--border); border-radius: 10px;
		padding: 10px 14px; font-size: 1rem; font-family: var(--mono, monospace);
	}
	.code-form button {
		background: var(--accent); color: var(--accent-ink); border: 0; border-radius: 10px;
		padding: 10px 18px; font-weight: 700; cursor: pointer;
	}
	.code-error { margin: 10px 0 0; font-size: 0.9rem; overflow-wrap: anywhere; }
	.decoded { margin-top: 12px; border-top: 1px solid var(--border); padding-top: 12px; }
	.decoded header { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; }
	.decoded h3 {
		margin: 12px 0 4px; font-size: 0.78rem; text-transform: uppercase; letter-spacing: 0.05em;
		color: var(--muted);
	}
	.decoded .issues { margin: 8px 0 0; padding-left: 20px; font-size: 0.9rem; }
	.decoded .issues li { overflow-wrap: anywhere; }
	.code-cards { list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
	.code-cards li {
		display: flex; align-items: baseline; gap: 8px; font-size: 0.9rem; overflow-wrap: anywhere;
	}
	.code-cards .qty { color: var(--muted); min-width: 2.4em; flex-shrink: 0; }
	.code-cards a { color: var(--text); text-decoration: none; }
	.code-cards a:hover { color: var(--accent); text-decoration: underline; }
	.code-cards .unlinked { color: var(--muted); }
	.note { font-size: 0.78rem; }

	/* Filter-form (rail + sheet) — zelfde vormtaal als /cards. */
	.filter-form { display: flex; flex-direction: column; gap: 12px; }
	.filter-form label { display: flex; flex-direction: column; gap: 4px; }
	.filter-form label span {
		font-size: 0.72rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em;
		color: var(--muted);
	}
	.filter-form select {
		width: 100%; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 9px 11px;
	}
	.filter-actions { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-top: 4px; }
	.filter-actions .link-btn { color: var(--muted); text-decoration: none; font-size: 0.85rem; padding: 6px 4px; }
	.filter-actions button {
		background: var(--accent); color: var(--accent-ink); border: 0; border-radius: 8px;
		padding: 9px 16px; font-weight: 700; cursor: pointer;
	}
</style>
