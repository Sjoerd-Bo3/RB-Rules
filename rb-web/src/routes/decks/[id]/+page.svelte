<script lang="ts">
	import { domainColorVar } from '$lib/changeCard';
	import { DECK_VIEW_KEY, parseDeckView, sectionTotal, type DeckView } from '$lib/deckView';
	import type { DeckCardRow } from './+page.server';

	let { data } = $props();
	const deck = $derived(data.deck);
	// Domein-tint (#214): het deck krijgt de kleur van zijn eerste domein.
	const deckDom = $derived(domainColorVar(deck.domains[0]));

	const LEGALITY_LABEL: Record<string, string> = {
		legal: 'Legaal',
		illegal: 'Niet legaal',
		incomplete: 'Onvolledig te beoordelen'
	};
	const LEGALITY_BADGE: Record<string, string> = { legal: 'ok-b', illegal: 'err', incomplete: 'warn-b' };
	const REASON_LABEL: Record<string, string> = {
		'not-yet-legal': 'kaart komt uit een set die nog niet legaal is',
		banned: 'kaart staat op de banlijst'
	};
	const SECTION_LABEL: Record<string, string> = {
		legend: 'Legend',
		champions: 'Champions',
		battlefields: 'Battlefields',
		runes: 'Runes',
		maindeck: 'Maindeck',
		sideboard: 'Sideboard',
		bench: 'Bench'
	};

	function formatDate(iso: string | null): string | null {
		return iso ? new Date(iso).toLocaleDateString('nl-NL') : null;
	}

	// Weergave (#256): grid met kaartafbeeldingen is de standaard, de compacte
	// tekstlijst blijft beschikbaar. De server rendert altijd het grid — de
	// bewaarde keuze komt pas in de browser binnen, dus een lijst-gebruiker
	// ziet één frame grid. Dat is de prijs voor een SSR-veilige start zonder
	// hydration-mismatch; de keuze zelf blijft wel hangen.
	let view = $state<DeckView>('grid');
	$effect(() => {
		try {
			view = parseDeckView(localStorage.getItem(DECK_VIEW_KEY));
		} catch {
			/* private mode: grid blijft de standaard */
		}
	});

	function setView(next: DeckView) {
		view = next;
		try {
			localStorage.setItem(DECK_VIEW_KEY, next);
		} catch {
			/* private mode: keuze geldt alleen deze sessie */
		}
	}
</script>

<svelte:head><title>{deck.name ?? 'Deck'} — Poracle</title></svelte:head>

<main style="--card-dom: {deckDom}">
	<a href="/decks" class="back">← Alle decks</a>

	<span class="dom-bar" aria-hidden="true"></span>
	<header class="head">
		<h1>{deck.name ?? '(naamloos deck)'}</h1>
		<span class="badge {LEGALITY_BADGE[deck.legality.status]}">
			{LEGALITY_LABEL[deck.legality.status]}
		</span>
	</header>

	<p class="meta domains">{deck.domains.join(' / ') || '—'}</p>
	<p class="meta stats">
		{deck.views} views · {deck.likes} likes
		{#if formatDate(deck.paCreatedAt)}· aangemaakt {formatDate(deck.paCreatedAt)}{/if}
		{#if formatDate(deck.paUpdatedAt)}· bijgewerkt {formatDate(deck.paUpdatedAt)}{/if}
	</p>

	<a class="pa-link" href={deck.sourceUrl} target="_blank" rel="noopener">
		Bekijk op Piltover Archive →
	</a>
	<p class="attribution">
		Dit deck is publiek gedeeld op Piltover Archive; wij bouwen geen eigen deckbuilder en
		spiegelen alleen hun decklijst, met bronvermelding hierboven.
	</p>

	<section class="legality-detail">
		{#if deck.legality.status === 'illegal'}
			<p class="issue-summary err">Dit deck is niet legaal:</p>
			<ul class="issues">
				{#each deck.legality.issues as issue (issue.cardCode + issue.reason)}
					<li>
						<strong>{issue.cardName ?? issue.cardCode}</strong> — {REASON_LABEL[issue.reason] ??
							issue.reason}
					</li>
				{/each}
			</ul>
		{:else if deck.legality.status === 'incomplete'}
			<p class="issue-summary warn">
				Onvolledig te beoordelen — {deck.legality.unknownCount}
				{deck.legality.unknownCount === 1 ? 'kaart is' : 'kaarten zijn'} niet gekoppeld aan onze
				kaartendatabank of komt/komen uit een set zonder bekende releasedatum. Geen aangetoonde
				overtreding, maar ook geen garantie.
			</p>
		{:else}
			<p class="issue-summary ok">Alle kaarten zitten in een legale set en staan niet op de banlijst.</p>
		{/if}
	</section>

	<div class="view-switch" role="group" aria-label="Weergave">
		<button type="button" class:on={view === 'grid'} aria-pressed={view === 'grid'} onclick={() => setView('grid')}>
			Grid
		</button>
		<button type="button" class:on={view === 'list'} aria-pressed={view === 'list'} onclick={() => setView('list')}>
			Lijst
		</button>
	</div>

	<div class="sections">
		{#each deck.sections as section (section.section)}
			<div class="section">
				<h2>
					{SECTION_LABEL[section.section] ?? section.section}
					<span class="sec-count tnum">{sectionTotal(section.cards)}</span>
				</h2>
				{#if view === 'grid'}
					<div class="tiles">
						{#each section.cards as card (card.cardCode)}
							{#if card.canonicalRiftboundId}
								<a class="tile" href="/cards/{card.canonicalRiftboundId}">
									{@render tileFace(card)}
								</a>
							{:else}
								<!-- Onbekend is data, geen fout: geen link, wel de kaartcode zodat
								     de gebruiker ziet wát er ontbreekt. -->
								<div class="tile unknown" title="Niet gekoppeld aan onze kaartendatabank">
									{@render tileFace(card)}
								</div>
							{/if}
						{/each}
					</div>
				{:else}
					<ul class="cards">
						{#each section.cards as card (card.cardCode)}
							<li>
								<span class="qty">{card.quantity}×</span>
								{#if card.canonicalRiftboundId}
									<a href="/cards/{card.canonicalRiftboundId}">{card.cardName}</a>
								{:else}
									<span class="unlinked">{card.cardName ?? card.cardCode}</span>
								{/if}
							</li>
						{/each}
					</ul>
				{/if}
			</div>
		{/each}
	</div>
</main>

<!-- Tegelvoorkant: afbeelding als we die hebben, anders een leesbare
     tekstplaatshouder in dezelfde kaartverhouding — nooit een gat in het grid. -->
{#snippet tileFace(card: DeckCardRow)}
	{#if card.imageUrl}
		<img src={card.imageUrl} alt={card.cardName ?? card.cardCode} loading="lazy" />
	{:else}
		<span class="face-fallback">
			<span class="fb-name">{card.cardName ?? card.cardCode}</span>
			{#if card.cardName}<span class="fb-code">{card.cardCode}</span>{/if}
			{#if !card.canonicalRiftboundId}<span class="fb-note">niet in onze databank</span>{/if}
		</span>
	{/if}
	<span class="qty-badge tnum">×{card.quantity}</span>
{/snippet}

<style>
	main {
		max-width: 860px;
		margin: 0 auto;
		padding: 24px 20px;
	}
	.back {
		color: var(--muted);
		text-decoration: none;
		font-size: 0.85rem;
	}
	.back:hover {
		color: var(--accent);
	}
	/* Domein-streep: 3px domein-accent boven de decktitel. */
	.dom-bar {
		display: block;
		width: 34px;
		height: 3px;
		border-radius: 3px;
		background: var(--card-dom);
		margin: 14px 0 0;
	}
	.head {
		display: flex;
		align-items: baseline;
		gap: 10px;
		flex-wrap: wrap;
		margin-top: 8px;
	}
	h1 {
		margin: 0;
		overflow-wrap: anywhere;
	}
	.meta {
		color: var(--muted);
	}
	.domains {
		margin: 6px 0 2px;
	}
	.stats {
		font-size: 0.85rem;
		margin: 2px 0 14px;
	}
	.pa-link {
		display: inline-block;
		background: var(--accent);
		color: var(--accent-ink);
		text-decoration: none;
		font-weight: 600;
		padding: 9px 16px;
		border-radius: 8px;
	}
	.attribution {
		color: var(--muted);
		font-size: 0.8rem;
		margin: 8px 0 20px;
	}
	.legality-detail {
		background: var(--surface);
		border: 1px solid var(--border);
		border-radius: var(--radius-lg);
		padding: 12px 16px;
		margin-bottom: 20px;
	}
	.issue-summary {
		margin: 0 0 6px;
		font-weight: 600;
	}
	.issue-summary.err {
		color: var(--err);
	}
	.issue-summary.warn {
		color: var(--warn);
	}
	.issue-summary.ok {
		color: var(--ok);
	}
	.issues {
		margin: 0;
		padding-left: 20px;
	}
	.issues li {
		margin: 3px 0;
		overflow-wrap: anywhere;
	}
	/* Weergaveschakelaar: segmented control, status via kleur + tekst. */
	.view-switch {
		display: inline-flex;
		gap: 2px;
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-radius: 999px;
		padding: 3px;
		margin: 0 0 16px;
	}
	.view-switch button {
		background: none;
		border: 0;
		border-radius: 999px;
		color: var(--muted);
		font: inherit;
		font-size: 0.85rem;
		font-weight: 600;
		padding: 6px 16px;
		cursor: pointer;
	}
	.view-switch button:hover {
		color: var(--text);
	}
	.view-switch button.on {
		background: var(--accent);
		color: var(--accent-ink);
	}
	.sections {
		display: grid;
		gap: 18px;
	}
	.section h2 {
		font-size: 1rem;
		color: var(--accent);
		margin: 0 0 8px;
		display: flex;
		align-items: baseline;
		gap: 8px;
	}
	/* Sectietelling: het totale aantal kaarten. We tonen bewust geen "40/40" —
	   deckbouw-limieten staan nergens in onze data, dus een noemer zou een
	   verzonnen regel zijn. */
	.sec-count {
		color: var(--muted);
		font-size: 0.85rem;
		font-weight: 400;
	}

	/* Kaartgrid: wrapt vanzelf: ~3 tegels op 390px, ~7 op desktop. */
	.tiles {
		display: grid;
		gap: 10px;
		grid-template-columns: repeat(auto-fill, minmax(104px, 1fr));
	}
	.tile {
		position: relative;
		display: block;
		border-radius: var(--radius);
		overflow: hidden;
		border: 1px solid var(--border);
		background: var(--surface-deep);
		aspect-ratio: 744 / 1039;
		text-decoration: none;
		color: inherit;
	}
	a.tile:hover {
		border-color: var(--accent);
	}
	.tile img {
		display: block;
		width: 100%;
		height: 100%;
		object-fit: cover;
	}
	.tile.unknown {
		border-style: dashed;
		border-color: var(--border-strong);
	}
	.face-fallback {
		display: flex;
		flex-direction: column;
		align-items: center;
		justify-content: center;
		gap: 4px;
		height: 100%;
		padding: 8px;
		text-align: center;
	}
	.fb-name {
		font-size: 0.78rem;
		font-weight: 600;
		overflow-wrap: anywhere;
	}
	.fb-code,
	.fb-note {
		font-size: 0.68rem;
		color: var(--muted);
		overflow-wrap: anywhere;
	}
	/* Aantal-badge: ondoorzichtig oppervlak, leesbaar over elke kaartillustratie
	   in licht én donker. */
	.qty-badge {
		position: absolute;
		right: 4px;
		bottom: 4px;
		background: var(--surface);
		color: var(--text);
		border: 1px solid var(--border-strong);
		border-radius: 999px;
		font-size: 0.72rem;
		font-weight: 700;
		padding: 1px 7px;
	}
	.cards {
		list-style: none;
		margin: 0;
		padding: 0;
		display: grid;
		gap: 4px;
	}
	.cards li {
		display: flex;
		gap: 8px;
		align-items: baseline;
		border-bottom: 1px solid var(--border);
		padding: 4px 0;
	}
	.qty {
		color: var(--muted);
		font-variant-numeric: tabular-nums;
		flex-shrink: 0;
		width: 2.4em;
	}
	.cards a {
		color: var(--text);
		text-decoration: none;
		overflow-wrap: anywhere;
	}
	.cards a:hover {
		color: var(--accent);
	}
	.unlinked {
		color: var(--muted);
		overflow-wrap: anywhere;
	}
</style>
