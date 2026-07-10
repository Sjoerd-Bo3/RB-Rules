<script lang="ts">
	import favicon from '$lib/assets/favicon.svg';

	let { children } = $props();
</script>

<svelte:head>
	<link rel="icon" href={favicon} />
</svelte:head>

<header class="site">
	<a href="/" class="brand">Riftbound <span>Rules</span></a>
	<nav>
		<a href="/">Wijzigingen</a>
		<a href="/rules">Regels</a>
		<a href="/ask">Vraag</a>
		<a href="/cards">Kaarten</a>
		<a href="/admin">Beheer</a>
	</nav>
</header>

{@render children()}

<style>
	.site {
		display: flex; justify-content: space-between; align-items: center;
		padding: 12px 20px; background: #16233b; border-bottom: 1px solid #243551;
	}
	.brand { font-weight: 700; color: #e7eefc; text-decoration: none; }
	.brand span { color: #d98a4e; }
	nav a { color: #9fb0cc; text-decoration: none; margin-left: 16px; }
	nav a:hover { color: #e7eefc; }
</style>
