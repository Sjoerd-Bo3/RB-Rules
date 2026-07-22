<script lang="ts">
	import { page } from '$app/state';

	let { children } = $props();

	const path = $derived(page.url.pathname);
	const tabs: { href: string; label: string; active: boolean }[] = $derived([
		{ href: '/admin/brein', label: 'Overzicht', active: path === '/admin/brein' },
		{ href: '/admin/brein/entities', label: 'Entiteiten', active: path.startsWith('/admin/brein/entities') },
		{
			href: '/admin/brein/interactions',
			label: 'Interacties',
			active: path.startsWith('/admin/brein/interactions')
		},
		{ href: '/admin/brein/conflicts', label: 'Conflicts', active: path.startsWith('/admin/brein/conflicts') },
		{
			href: '/admin/brein/answertrace',
			label: 'AnswerTrace',
			active: path.startsWith('/admin/brein/answertrace')
		}
	]);
</script>

<div class="brein">
	<header class="bhead">
		<a class="crumb" href="/admin">&larr; beheer</a>
		<h1>Brein</h1>
		<p class="lede">
			Verken en beheer het Poracle-brein. Postgres blijft de bron van waarheid; kennis en
			afgeleide graaf-feiten zijn read-only, operationele instellingen op het overzicht zijn
			direct beheerbaar.
		</p>
	</header>

	<nav class="btabs" aria-label="Brein-verkenner">
		{#each tabs as t (t.href)}
			<a href={t.href} class:on={t.active} aria-current={t.active ? 'page' : undefined}>{t.label}</a>
		{/each}
	</nav>

	{@render children()}
</div>

<style>
	.brein {
		padding: 22px clamp(14px, 3vw, 30px) 60px;
		max-width: 1180px;
	}
	.crumb {
		font-size: 0.78rem;
		color: var(--muted);
		text-decoration: none;
	}
	.crumb:hover {
		color: var(--text);
	}
	.bhead h1 {
		margin: 6px 0 4px;
	}
	.lede {
		color: var(--muted);
		font-size: 0.86rem;
		max-width: 62ch;
		line-height: 1.5;
		margin: 0;
	}
	.btabs {
		display: flex;
		flex-wrap: wrap;
		gap: 4px;
		margin: 18px 0 22px;
		border-bottom: 1px solid var(--border);
	}
	.btabs a {
		padding: 8px 13px;
		font-size: 0.85rem;
		color: var(--muted);
		text-decoration: none;
		border-bottom: 2px solid transparent;
		margin-bottom: -1px;
	}
	.btabs a:hover {
		color: var(--text);
	}
	.btabs a.on {
		color: var(--text);
		font-weight: 650;
		border-bottom-color: var(--accent);
	}

	/* ── Gedeelde bouwstenen voor alle brein-pagina's (:global, begrensd door
	   de .brein-voorouder — lekt dus niet buiten de verkenner). ─────────── */
	:global(.brein h2) {
		font-size: 1.02rem;
		margin: 26px 0 10px;
	}
	:global(.brein .empty) {
		color: var(--muted);
		font-size: 0.9rem;
		background: var(--surface);
		border: 1px dashed var(--border-strong);
		border-radius: var(--radius-lg);
		padding: 22px 20px;
		text-align: center;
	}
	:global(.brein .apidown) {
		color: var(--err);
		font-size: 0.9rem;
		background: var(--err-soft);
		border: 1px solid var(--err);
		border-radius: var(--radius-lg);
		padding: 14px 16px;
	}
	:global(.brein table) {
		width: 100%;
		border-collapse: collapse;
		font-size: 0.83rem;
	}
	:global(.brein th),
	:global(.brein td) {
		padding: 8px 10px;
		text-align: left;
		border-bottom: 1px solid var(--border);
		vertical-align: top;
	}
	:global(.brein th) {
		color: var(--muted);
		font-weight: 500;
		font-size: 0.66rem;
		letter-spacing: 0.05em;
		text-transform: uppercase;
		white-space: nowrap;
	}
	:global(.brein td.num),
	:global(.brein th.num) {
		text-align: right;
		font-variant-numeric: tabular-nums;
	}
	:global(.brein .ref) {
		font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
		font-size: 0.78rem;
		color: var(--text);
		word-break: break-all;
	}
	:global(.brein .muted) {
		color: var(--muted);
	}
	:global(.brein .chips) {
		display: flex;
		flex-wrap: wrap;
		gap: 6px;
		margin-bottom: 16px;
	}
	:global(.brein .chips a) {
		font-size: 0.78rem;
		padding: 5px 11px;
		border-radius: 999px;
		border: 1px solid var(--border);
		background: var(--surface);
		color: var(--muted);
		text-decoration: none;
	}
	:global(.brein .chips a:hover) {
		border-color: var(--border-strong);
		color: var(--text);
	}
	:global(.brein .chips a.on) {
		background: var(--accent);
		color: var(--accent-ink);
		border-color: transparent;
		font-weight: 650;
	}
	:global(.brein .pager) {
		display: flex;
		align-items: center;
		gap: 14px;
		margin: 18px 0;
		font-size: 0.85rem;
	}
	:global(.brein .pager a) {
		color: var(--accent);
		text-decoration: none;
		font-weight: 600;
	}
	:global(.brein .pager span) {
		color: var(--muted);
	}
	/* Tier-/kanaal-badges: status = kleur + tekst, geen emoji. */
	:global(.brein .tier) {
		display: inline-block;
		font-size: 0.68rem;
		text-transform: uppercase;
		letter-spacing: 0.03em;
		font-weight: 700;
		padding: 2px 8px;
		border-radius: 999px;
		white-space: nowrap;
		background: var(--surface-deep);
		color: var(--muted);
		border: 1px solid var(--border);
	}
	:global(.brein .tier.ok) {
		background: var(--ok-soft);
		color: var(--ok);
		border-color: transparent;
	}
	:global(.brein .tier.warn) {
		background: var(--warn-soft);
		color: var(--warn);
		border-color: transparent;
	}
	:global(.brein .tier.err) {
		background: var(--err-soft);
		color: var(--err);
		border-color: transparent;
	}
	:global(.brein .tier.accent) {
		background: color-mix(in srgb, var(--accent) 22%, transparent);
		color: var(--text);
		border-color: transparent;
	}
</style>
