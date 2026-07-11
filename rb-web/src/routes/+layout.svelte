<script lang="ts">
	import '../app.css';
	import { page } from '$app/state';
	import favicon from '$lib/assets/favicon.svg';

	let { children } = $props();

	const NAV = [
		{ href: '/', label: 'Wijzigingen' },
		{ href: '/rules', label: 'Regels' },
		{ href: '/ask', label: 'Vraag' },
		{ href: '/cards', label: 'Kaarten' },
		{ href: '/admin', label: 'Beheer' }
	];

	const active = $derived((href: string) =>
		href === '/' ? page.url.pathname === '/' : page.url.pathname.startsWith(href));
</script>

<svelte:head>
	<link rel="icon" href={favicon} />
</svelte:head>

<header class="site">
	<a href="/" class="brand">Riftbound <span>Rules</span></a>
	<nav>
		{#each NAV as item (item.href)}
			<a href={item.href} class:active={active(item.href)}>{item.label}</a>
		{/each}
	</nav>
</header>

{@render children()}

<footer class="site-footer">
	<p>
		Onofficiële referentie — automatisch bijgehouden uit de officiële Riftbound-bronnen.
		Geen onderdeel van Riot Games.
	</p>
</footer>

<style>
	.site {
		display: flex; justify-content: space-between; align-items: center;
		padding: 14px 24px; background: var(--surface);
		border-bottom: 1px solid var(--border);
		position: sticky; top: 0; z-index: 10;
	}
	.brand { font-weight: 700; font-size: 1.02rem; color: var(--text); text-decoration: none; letter-spacing: -0.01em; }
	.brand span { color: var(--accent); }
	nav { display: flex; gap: 4px; }
	nav a {
		color: var(--muted); text-decoration: none; font-size: 0.92rem;
		padding: 6px 12px; border-radius: 8px;
	}
	nav a:hover { color: var(--text); background: var(--surface-deep); }
	nav a.active { color: var(--accent); background: var(--accent-soft); font-weight: 600; }
	.site-footer {
		max-width: 1080px; margin: 40px auto 0; padding: 18px 20px 28px;
		border-top: 1px solid var(--border);
		color: var(--muted); font-size: 0.8rem;
	}
</style>
