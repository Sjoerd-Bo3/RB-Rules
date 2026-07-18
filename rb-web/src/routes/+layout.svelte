<script lang="ts">
	import '../app.css';
	import { page } from '$app/state';
	import { afterNavigate, goto } from '$app/navigation';
	import { onMount } from 'svelte';
	import PoroMark from '$lib/PoroMark.svelte';
	import { provideShell } from '$lib/shell.svelte';

	let { children } = $props();

	// Samengestelde shell (#214): de store leeft per request in de context;
	// pagina's zetten opt-in rail-inhoud, en de drawer/sheet-state woont hier.
	const shell = provideShell();
	onMount(() => shell.initTheme());
	// Navigatie sluit altijd de mobiele drawer en de filter-sheet.
	afterNavigate(() => {
		shell.drawerOpen = false;
		shell.sheetOpen = false;
	});

	// Gegroepeerde navigatie. De domein-stip bij een kennis-item is puur
	// decoratief (subtiel) — het versterkt het domeinpalet, het codeert niets.
	const NAV_GROUPS = [
		{
			label: 'Actueel',
			items: [
				{ href: '/', label: 'Overzicht' },
				{ href: '/wijzigingen', label: 'Wijzigingen' },
				{ href: '/ask', label: 'Vraag' }
			]
		},
		{
			label: 'Kennis',
			items: [
				{ href: '/rules', label: 'Regels', dom: 'order' },
				{ href: '/primer', label: 'Spelbegrip', dom: 'mind' },
				{ href: '/rulings', label: 'Rulings', dom: 'calm' },
				{ href: '/cards', label: 'Kaarten', dom: 'fury' },
				{ href: '/decks', label: 'Decks', dom: 'body' },
				{ href: '/graph', label: 'Brein', dom: 'chaos' }
			]
		}
	] as const;
	const BOTTOM = [
		{ href: '/account', label: 'Account' },
		{ href: '/admin', label: 'Beheer' }
	];

	const active = $derived((href: string) =>
		href === '/' ? page.url.pathname === '/' : page.url.pathname.startsWith(href));

	// Globaal zoekveld → stelt een vraag aan /ask (de vraagbaak prefill't).
	let search = $state('');
	function onSearch(e: Event) {
		e.preventDefault();
		const q = search.trim();
		if (!q) return;
		search = '';
		goto(`/ask?q=${encodeURIComponent(q)}`);
	}
</script>

<div class="shell">
	<!-- Mobiele bovenbalk: hamburger + merk (desktop verborgen). -->
	<header class="topbar">
		<button
			class="hamburger"
			aria-label="Menu openen"
			aria-expanded={shell.drawerOpen}
			onclick={() => (shell.drawerOpen = true)}
		>
			<span></span><span></span><span></span>
		</button>
		<a class="brand" href="/"><PoroMark size={22} />Poracle</a>
	</header>

	<!-- Zijbalk: vast op desktop, slide-over drawer op mobiel. -->
	<aside class="sidebar" class:open={shell.drawerOpen} aria-label="Hoofdnavigatie">
		<a class="brand brand-side" href="/"><PoroMark size={22} />Poracle</a>

		<form class="side-search" onsubmit={onSearch} role="search">
			<input
				type="search"
				placeholder="Stel een vraag…"
				aria-label="Stel een vraag"
				bind:value={search}
			/>
		</form>

		<nav>
			{#each NAV_GROUPS as group (group.label)}
				<p class="nav-group">{group.label}</p>
				{#each group.items as item (item.href)}
					<a href={item.href} class:active={active(item.href)}>
						{#if 'dom' in item && item.dom}
							<span class="dom-dot" style="background: var(--dom-{item.dom})"></span>
						{/if}
						{item.label}
					</a>
				{/each}
			{/each}
		</nav>

		<div class="side-foot">
			{#each BOTTOM as item (item.href)}
				<a href={item.href} class:active={active(item.href)}>{item.label}</a>
			{/each}
			<button
				class="theme-toggle"
				role="switch"
				aria-checked={shell.isDark}
				onclick={() => shell.toggleTheme()}
			>
				<span>Thema: {shell.isDark ? 'donker' : 'licht'}</span>
				<span class="tt-switch" class:on={shell.isDark}></span>
			</button>
		</div>
	</aside>

	{#if shell.drawerOpen}
		<button class="scrim" aria-label="Menu sluiten" onclick={() => (shell.drawerOpen = false)}
		></button>
	{/if}

	<div class="workarea" class:has-rail={!!shell.rail}>
		<main class="content">
			{@render children()}
		</main>

		{#if shell.rail}
			<aside
				class="rail"
				class:as-filters={shell.rail.kind === 'filters'}
				aria-label={shell.rail.title ?? 'Contextuele informatie'}
			>
				{#if shell.rail.title}<h2 class="rail-title">{shell.rail.title}</h2>{/if}
				{@render shell.rail.snippet()}
			</aside>
		{/if}
	</div>
</div>

<footer class="site-footer">
	<p>
		Onofficiële referentie — automatisch bijgehouden uit de officiële Riftbound-bronnen. Geen
		onderdeel van Riot Games.
	</p>
</footer>

<!-- Mobiele filter-toegang: alleen als de pagina filters aanbiedt en de rail
     niet inline zichtbaar is (< 1080px). Chips wrappen in de bottom-sheet —
     nooit horizontaal scrollen. -->
{#if shell.rail?.kind === 'filters'}
	<button class="filter-fab" onclick={() => (shell.sheetOpen = true)}>
		Filter{#if shell.rail.count}<span class="fab-badge">{shell.rail.count}</span>{/if}
	</button>
{/if}

{#if shell.sheetOpen && shell.rail}
	<button class="sheet-scrim" aria-label="Filters sluiten" onclick={() => (shell.sheetOpen = false)}
	></button>
	<section class="sheet" aria-label="Filters">
		<header class="sheet-head">
			<strong>{shell.rail.title ?? 'Filters'}</strong>
			<button class="sheet-close" aria-label="Sluiten" onclick={() => (shell.sheetOpen = false)}
				>Sluiten</button
			>
		</header>
		<div class="sheet-body">
			{@render shell.rail.snippet()}
		</div>
	</section>
{/if}

<style>
	.shell {
		display: grid;
		grid-template-columns: 1fr;
		min-height: 100vh;
	}

	/* Merk */
	.brand {
		display: inline-flex;
		align-items: center;
		gap: 8px;
		font-weight: 750;
		font-size: 1.02rem;
		color: var(--text);
		text-decoration: none;
		letter-spacing: -0.01em;
		white-space: nowrap;
	}

	/* Mobiele bovenbalk */
	.topbar {
		display: flex;
		align-items: center;
		gap: 12px;
		padding: 10px 14px;
		background: var(--surface);
		border-bottom: 1px solid var(--border);
		position: sticky;
		top: 0;
		z-index: 20;
	}
	.hamburger {
		display: inline-flex;
		flex-direction: column;
		justify-content: center;
		gap: 4px;
		width: 40px;
		height: 40px;
		padding: 0 9px;
		background: transparent;
		border: 1px solid var(--border);
		border-radius: 9px;
		cursor: pointer;
	}
	.hamburger span {
		height: 2px;
		background: var(--text);
		border-radius: 2px;
	}

	/* Zijbalk */
	.sidebar {
		display: flex;
		flex-direction: column;
		gap: 14px;
		padding: 16px 14px;
		background: var(--surface);
		border-right: 1px solid var(--border);
	}
	.brand-side {
		display: inline-flex;
	}
	.side-search input {
		width: 100%;
		background: var(--surface-deep);
		color: var(--text);
		border: 1px solid var(--border);
		border-radius: 9px;
		padding: 9px 12px;
	}
	nav {
		display: flex;
		flex-direction: column;
		gap: 2px;
	}
	.nav-group {
		margin: 12px 0 4px;
		padding: 0 8px;
		font-size: 0.68rem;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.08em;
		color: var(--muted);
	}
	.nav-group:first-child {
		margin-top: 0;
	}
	nav a {
		display: flex;
		align-items: center;
		gap: 9px;
		color: var(--muted);
		text-decoration: none;
		font-size: 0.92rem;
		padding: 8px 10px;
		border-radius: 8px;
	}
	nav a:hover {
		color: var(--text);
		background: var(--surface-deep);
	}
	nav a.active {
		color: var(--text);
		background: var(--surface-deep);
		font-weight: 600;
	}
	.dom-dot {
		width: 6px;
		height: 6px;
		border-radius: 2px;
		flex: none;
		opacity: 0.9;
	}
	.side-foot {
		display: flex;
		flex-direction: column;
		gap: 2px;
		margin-top: auto;
		padding-top: 12px;
		border-top: 1px solid var(--border);
	}
	.side-foot a {
		color: var(--muted);
		text-decoration: none;
		font-size: 0.92rem;
		padding: 8px 10px;
		border-radius: 8px;
	}
	.side-foot a:hover,
	.side-foot a.active {
		color: var(--text);
		background: var(--surface-deep);
	}
	.theme-toggle {
		display: flex;
		align-items: center;
		gap: 8px;
		margin-top: 6px;
		background: transparent;
		border: 0;
		color: var(--muted);
		font-size: 0.82rem;
		padding: 6px 9px;
		cursor: pointer;
		width: 100%;
	}
	.theme-toggle:hover {
		color: var(--text);
	}
	.tt-switch {
		margin-left: auto;
		width: 30px;
		height: 16px;
		border-radius: 999px;
		background: var(--surface-deep);
		border: 1px solid var(--border);
		position: relative;
		flex: none;
	}
	.tt-switch::after {
		content: '';
		position: absolute;
		left: 2px;
		top: 1px;
		width: 12px;
		height: 12px;
		border-radius: 50%;
		background: var(--accent);
		transition: transform 0.15s ease;
	}
	.tt-switch.on::after {
		transform: translateX(14px);
	}

	/* Scrim onder de drawer (mobiel) */
	.scrim {
		position: fixed;
		inset: 0;
		z-index: 25;
		background: rgba(10, 12, 18, 0.5);
		border: 0;
	}

	/* Werkgebied: content + optionele rail */
	.workarea {
		min-width: 0;
		display: grid;
		grid-template-columns: 1fr;
	}
	.content {
		min-width: 0;
	}
	.rail {
		min-width: 0;
	}
	.rail-title {
		font-size: 0.72rem;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.06em;
		color: var(--muted);
		margin: 0 0 10px;
	}

	.site-footer {
		max-width: 1080px;
		margin: 24px auto 0;
		padding: 18px 20px 28px;
		border-top: 1px solid var(--border);
		color: var(--muted);
		font-size: 0.8rem;
	}

	/* Mobiele filter-knop (fab) + bottom-sheet */
	.filter-fab {
		position: fixed;
		right: 16px;
		bottom: 16px;
		z-index: 24;
		display: inline-flex;
		align-items: center;
		gap: 8px;
		background: var(--accent);
		color: var(--accent-ink);
		border: 0;
		border-radius: 999px;
		padding: 12px 20px;
		font-weight: 700;
		font-size: 0.9rem;
		cursor: pointer;
		box-shadow: 0 8px 24px -8px rgba(20, 24, 40, 0.5);
	}
	.fab-badge {
		display: inline-flex;
		align-items: center;
		justify-content: center;
		min-width: 20px;
		height: 20px;
		padding: 0 6px;
		border-radius: 999px;
		background: var(--accent-ink);
		color: var(--accent);
		font-size: 0.75rem;
		font-variant-numeric: tabular-nums;
	}
	.sheet-scrim {
		position: fixed;
		inset: 0;
		z-index: 30;
		background: rgba(10, 12, 18, 0.5);
		border: 0;
	}
	.sheet {
		position: fixed;
		left: 0;
		right: 0;
		bottom: 0;
		z-index: 31;
		max-height: 82vh;
		overflow-y: auto;
		background: var(--surface);
		border-top: 1px solid var(--border);
		border-radius: 16px 16px 0 0;
		padding: 8px 16px calc(16px + env(safe-area-inset-bottom));
		box-shadow: 0 -12px 40px -18px rgba(20, 24, 40, 0.6);
	}
	.sheet-head {
		display: flex;
		align-items: center;
		justify-content: space-between;
		position: sticky;
		top: 0;
		background: var(--surface);
		padding: 10px 0;
		margin-bottom: 4px;
	}
	.sheet-close {
		background: transparent;
		border: 1px solid var(--border);
		border-radius: 8px;
		color: var(--muted);
		padding: 6px 12px;
		cursor: pointer;
	}

	/* ── Tablet/desktop: vaste zijbalk ─────────────────────────────── */
	@media (min-width: 760px) {
		.topbar {
			display: none;
		}
		.shell {
			grid-template-columns: 212px minmax(0, 1fr);
		}
		.sidebar {
			position: sticky;
			top: 0;
			height: 100vh;
			overflow-y: auto;
		}
		.brand-side {
			display: inline-flex;
		}
		.scrim {
			display: none;
		}
		.site-footer {
			margin-left: 212px;
		}
	}

	/* ── Mobiel: zijbalk als slide-over ────────────────────────────── */
	@media (max-width: 759px) {
		.sidebar {
			position: fixed;
			top: 0;
			left: 0;
			bottom: 0;
			width: 264px;
			max-width: 84vw;
			z-index: 26;
			transform: translateX(-100%);
			transition: transform 0.18s ease;
			overflow-y: auto;
		}
		.sidebar.open {
			transform: translateX(0);
		}
	}

	/* ── Rechterrail: pas inline vanaf 1080px ──────────────────────── */
	@media (min-width: 1080px) {
		.workarea.has-rail {
			grid-template-columns: minmax(0, 1fr) 288px;
			gap: 8px;
		}
		.rail {
			padding: 24px 20px 24px 0;
			position: sticky;
			top: 0;
			align-self: start;
			max-height: 100vh;
			overflow-y: auto;
		}
		.filter-fab {
			display: none;
		}
	}
	@media (max-width: 1079px) {
		/* Filters horen in de sheet, niet inline. Context-rails mogen wel
		   onder de content meelopen. */
		.rail.as-filters {
			display: none;
		}
		.rail {
			padding: 0 20px 8px;
		}
	}
</style>
