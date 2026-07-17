<script lang="ts">
	import { goto } from '$app/navigation';
	import ChangeCard from '$lib/ChangeCard.svelte';

	let { data } = $props();

	let q = $state('');
	function ask(e: Event) {
		e.preventDefault();
		const query = q.trim();
		goto(query ? `/ask?q=${encodeURIComponent(query)}` : '/ask');
	}

	const tiles = $derived([
		{ label: 'Kaarten', value: data.stats.cards, href: '/cards', dom: 'fury' },
		{ label: 'Geverifieerde rulings', value: data.stats.verifiedRulings, href: '/rulings', dom: 'calm' },
		{ label: 'Actieve bans', value: data.stats.bans, href: '/wijzigingen', dom: 'fury' },
		{ label: 'Nieuwe wijzigingen', value: data.stats.recentChanges, href: '/wijzigingen', dom: 'mind', note: 'laatste 14 dagen' }
	]);

	// "Spring naar" — de kennisbronnen met een telling waar zinvol.
	const jumps = $derived([
		{ href: '/wijzigingen', label: 'Wijzigingen', desc: 'Bans, errata en regelupdates', count: data.stats.recentChanges, dom: 'mind' },
		{ href: '/rules', label: 'Regels', desc: 'Core & Tournament Rules met §-permalinks', dom: 'order' },
		{ href: '/cards', label: 'Kaarten', desc: 'Semantisch zoeken en bladeren', count: data.stats.cards, dom: 'fury' },
		{ href: '/rulings', label: 'Rulings', desc: 'Geverifieerde beslissingen', count: data.stats.verifiedRulings, dom: 'calm' },
		{ href: '/decks', label: 'Decks', desc: 'Piltover Archive-decks', dom: 'body' },
		{ href: '/graph', label: 'Brein', desc: 'De kennisgraaf verkennen', dom: 'chaos' }
	]);
</script>

<svelte:head><title>Riftbound Rules Companion</title></svelte:head>

<main>
	<section class="hero">
		<h1>Riftbound <span>Rules Companion</span></h1>
		<p class="subtitle">
			Regels, bans, errata, rulings en kaarten — automatisch bijgehouden, met een AI-vraagbaak.
		</p>
		<form class="hero-search" onsubmit={ask}>
			<input
				type="search"
				bind:value={q}
				placeholder="Stel een regelvraag, bijv. 'werkt Deflect tijdens een showdown?'"
				aria-label="Stel een regelvraag"
			/>
			<button type="submit">Vraag</button>
		</form>
	</section>

	<section class="tiles">
		{#each tiles as t (t.label)}
			<a class="tile" href={t.href} style="--tile: var(--dom-{t.dom})">
				<span class="tile-value tnum">{t.value}</span>
				<span class="tile-label">{t.label}</span>
				{#if t.note}<span class="tile-note">{t.note}</span>{/if}
			</a>
		{/each}
	</section>

	<div class="cols">
		<section class="recent">
			<div class="sec-head">
				<h2>Recente wijzigingen</h2>
				<a class="more" href="/wijzigingen">Alle wijzigingen →</a>
			</div>
			{#if data.recent.length === 0}
				<p class="meta">Nog geen wijzigingen geregistreerd.</p>
			{:else}
				{#each data.recent as c (c.id)}
					<ChangeCard change={c} compact />
				{/each}
			{/if}
		</section>

		<section class="jump">
			<h2>Spring naar</h2>
			<div class="jump-list">
				{#each jumps as j (j.href)}
					<a class="jump-item" href={j.href}>
						<span class="jump-dot" style="background: var(--dom-{j.dom})"></span>
						<span class="jump-body">
							<span class="jump-label">
								{j.label}
								{#if j.count !== undefined}<span class="jump-count tnum">{j.count}</span>{/if}
							</span>
							<span class="jump-desc">{j.desc}</span>
						</span>
					</a>
				{/each}
			</div>
		</section>
	</div>
</main>

<style>
	main { max-width: 1000px; margin: 0 auto; padding: 24px 20px; }

	.hero { margin-bottom: 22px; }
	h1 { margin: 0 0 6px; }
	h1 span { color: var(--accent); }
	.subtitle { color: var(--muted); margin: 0 0 16px; max-width: 62ch; }
	.hero-search { display: flex; gap: 8px; }
	.hero-search input {
		flex: 1; min-width: 0; background: var(--surface); color: var(--text);
		border: 1px solid var(--border); border-radius: 10px; padding: 12px 14px; font-size: 1rem;
	}
	.hero-search input:focus { outline: 2px solid var(--focus); outline-offset: -1px; }
	.hero-search button {
		background: var(--accent); color: var(--accent-ink); border: 0; border-radius: 10px;
		padding: 0 20px; font-weight: 700; cursor: pointer; white-space: nowrap;
	}

	.tiles {
		display: grid; gap: 12px; margin-bottom: 24px;
		grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
	}
	.tile {
		display: flex; flex-direction: column; gap: 2px;
		background: var(--surface); border: 1px solid var(--border);
		border-radius: var(--radius-lg); padding: 16px 16px 16px 18px;
		text-decoration: none; color: inherit; position: relative; overflow: hidden;
		box-shadow: var(--shadow-card);
	}
	/* Domein-accent als smalle linkerstreep — dezelfde taal als de ChangeCard. */
	.tile::before {
		content: ''; position: absolute; left: 0; top: 0; bottom: 0; width: 3px;
		background: var(--tile);
	}
	.tile:hover { border-color: var(--border-strong); }
	.tile-value { font-size: 1.7rem; font-weight: 700; letter-spacing: -0.02em; }
	.tile-label { color: var(--muted); font-size: 0.9rem; }
	.tile-note { color: var(--muted); font-size: 0.74rem; }

	.cols { display: grid; gap: 22px; grid-template-columns: 1fr; }
	@media (min-width: 900px) {
		.cols { grid-template-columns: minmax(0, 1.5fr) minmax(0, 1fr); align-items: start; }
	}
	.sec-head { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; }
	h2 { font-size: 1.05rem; margin: 0 0 12px; }
	.more { color: var(--accent); text-decoration: none; font-size: 0.85rem; font-weight: 600; white-space: nowrap; }
	.meta { color: var(--muted); }

	.jump-list { display: flex; flex-direction: column; gap: 6px; }
	.jump-item {
		display: flex; align-items: flex-start; gap: 10px;
		background: var(--surface); border: 1px solid var(--border);
		border-radius: var(--radius); padding: 11px 13px; text-decoration: none; color: inherit;
	}
	.jump-item:hover { border-color: var(--border-strong); }
	.jump-dot { width: 8px; height: 8px; border-radius: 50%; margin-top: 5px; flex: none; }
	.jump-body { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
	.jump-label { font-weight: 600; display: flex; align-items: center; gap: 8px; }
	.jump-count {
		font-size: 0.72rem; font-weight: 700; color: var(--muted);
		background: var(--surface-deep); border-radius: 999px; padding: 1px 8px;
	}
	.jump-desc { color: var(--muted); font-size: 0.85rem; }
</style>
