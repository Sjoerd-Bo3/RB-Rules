<script lang="ts">
	import RbText from '$lib/RbText.svelte';

	interface CardLike {
		riftboundId: string;
		name: string;
		type: string | null;
		supertype: string | null;
		domains: string[];
		energy: number | null;
		might: number | null;
		textPlain: string | null;
		mechanics: string[] | null;
		imageUrl: string | null;
		banned: boolean;
		// Set-legaliteit (#68); optioneel zodat oudere payloads blijven werken.
		setName?: string | null;
		legalFrom?: string | null;
		legality?: 'legal' | 'upcoming' | 'announced';
	}

	let { name, cards }: { name: string; cards: CardLike[] } = $props();
	const card = $derived(
		cards.find((c) => c.name.toLowerCase() === name.toLowerCase()) ??
		cards.find((c) => c.name.toLowerCase().includes(name.toLowerCase())) ??
		null
	);
</script>

{#if card}
	<a class="card-widget" href="/cards/{card.riftboundId}">
		{#if card.imageUrl}<img src={card.imageUrl} alt={card.name} loading="lazy" />{/if}
		<span class="body">
			<span class="name">
				{card.name}
				{#if card.banned}<span class="ban">Verboden</span>{/if}
				<!-- Alleen bij een bekende toekomstige releasedatum een claim;
				     "datum onbekend" kan ook een allang verschenen set zijn (#68). -->
				{#if card.legality === 'upcoming'}
					<span class="soon">Nog niet legaal — komt{card.setName ? ` in ${card.setName}` : ''}{card.legalFrom ? ` op ${new Date(card.legalFrom).toLocaleDateString('nl-NL')}` : ''}</span>
				{/if}
			</span>
			<span class="meta">
				{[card.supertype, card.type].filter(Boolean).join(' ')} · {card.domains.join('/') || '—'}
				{#if card.energy !== null}· Energy {card.energy}{/if}
				{#if card.might !== null}· Might {card.might}{/if}
			</span>
			{#if card.textPlain}<span class="text"><RbText text={card.textPlain} /></span>{/if}
		</span>
	</a>
{:else}
	<p class="card-fallback"><a href="/cards?q={encodeURIComponent(name)}">{name} — zoek de kaart</a></p>
{/if}

<style>
	.card-widget {
		display: flex; gap: 12px; align-items: flex-start;
		background: var(--surface-deep); border: 1px solid var(--accent);
		border-radius: 10px; padding: 10px 12px; margin: 10px 0;
		color: inherit; text-decoration: none;
	}
	.card-widget:hover { border-color: var(--border-strong); }
	img { width: 74px; border-radius: 6px; border: 1px solid var(--border); flex-shrink: 0; }
	.body { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
	.name { font-weight: 700; }
	.ban {
		font-size: 0.68rem; text-transform: uppercase; margin-left: 6px;
		background: var(--err-soft); color: var(--err); border-radius: 999px; padding: 1px 8px;
	}
	.soon {
		font-size: 0.68rem; margin-left: 6px;
		background: var(--warn-soft); color: var(--warn); border-radius: 999px; padding: 1px 8px;
	}
	.meta { color: var(--muted); font-size: 0.82rem; }
	.text { font-size: 0.86rem; line-height: 1.5; }
	.card-fallback { margin: 8px 0; }
	.card-fallback a { color: var(--accent); font-weight: 600; }
</style>
