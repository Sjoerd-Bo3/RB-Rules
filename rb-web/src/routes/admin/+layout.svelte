<script lang="ts">
	import { page } from '$app/state';
	import { afterNavigate } from '$app/navigation';
	import { onMount } from 'svelte';
	import { useShell } from '$lib/shell.svelte';

	let { data, children } = $props();

	// De thema-schakelaar deelt de globale shell-store (per request via context):
	// we lezen/togglen alleen, we wijzigen de store niet.
	const shell = useShell();

	// Beheer-shell (#214 redesign): de admin-routes vervangen visueel de
	// publieke zijbalk door een eigen console-shell. De globale +layout.svelte
	// blijft ongewijzigd; we onderdrukken zijn chrome zolang deze layout leeft
	// via een klasse op <html> — de :global-regels hieronder zijn dus alleen
	// actief binnen het beheer en verdwijnen zodra je terug naar de site gaat.
	onMount(() => {
		const el = document.documentElement;
		el.classList.add('admin-shell');
		return () => el.classList.remove('admin-shell');
	});

	// Mobiele drawer (zelfde patroon als de publieke shell): navigatie sluit hem.
	let drawerOpen = $state(false);
	afterNavigate(() => {
		drawerOpen = false;
	});

	const authed = $derived(data.authed);

	// Tel-badges uit de al geladen page-data — geen extra fetch. Aanwezig op de
	// hoofd-/admin-pagina (status.counts + sources); op de detail-overzichten
	// ontbreken ze en tonen we simpelweg geen badge (nette degradatie).
	const pageData = $derived(page.data as {
		status?: { counts?: Record<string, number> } | null;
		sources?: unknown[];
	});
	const reviewBadge = $derived(pageData.status?.counts?.openCorrections);
	const sourceBadge = $derived(pageData.sources?.length);

	const path = $derived(page.url.pathname);
	interface NavItem {
		href: string;
		label: string;
		badge?: number;
		danger?: boolean;
		active: boolean;
	}
	const nav = $derived<NavItem[]>([
		{ href: '/admin', label: 'Overzicht', active: path === '/admin' },
		{ href: '/admin#jobs', label: 'Jobs & paden', active: false },
		{
			href: '/admin/overview/relaties',
			label: 'Reviewqueue',
			badge: reviewBadge,
			active: path.startsWith('/admin/overview/relaties')
		},
		{ href: '/admin#bronnen', label: 'Bronnen', badge: sourceBadge, active: false },
		{
			href: '/admin/overview/gebruikers',
			label: 'Kosten',
			active: path.startsWith('/admin/overview/gebruikers')
		},
		{ href: '/admin#traces', label: 'Vraag-traces', active: false },
		{ href: '/admin#gevarenzone', label: 'Gevarenzone', danger: true, active: false }
	]);
</script>

<div class="admin" class:drawer-open={drawerOpen}>
	<!-- Mobiele bovenbalk (< 760px): hamburger opent de beheer-drawer. -->
	<header class="atop">
		<button
			class="hamburger"
			aria-label="Beheermenu openen"
			aria-expanded={drawerOpen}
			onclick={() => (drawerOpen = true)}
		>
			<span></span><span></span><span></span>
		</button>
		<span class="sbrand"><span class="mark"></span>Riftbound<span class="tagb">beheer</span></span>
	</header>

	<aside class="asidebar" class:open={drawerOpen} aria-label="Beheernavigatie">
		<a class="back" href="/">&larr; naar de site</a>
		<span class="sbrand"><span class="mark"></span>Riftbound<span class="tagb">beheer</span></span>

		{#if authed}
			<nav class="anav">
				{#each nav as item (item.href + item.label)}
					<a
						href={item.href}
						class:on={item.active}
						class:danger={item.danger}
						aria-current={item.active ? 'page' : undefined}
					>
						<span class="lbl">{item.label}</span>
						{#if item.badge !== undefined}<span class="b tnum">{item.badge}</span>{/if}
					</a>
				{/each}
			</nav>
		{:else}
			<p class="signed-out">Niet ingelogd.</p>
		{/if}

		<div class="sbottom">
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

	{#if drawerOpen}
		<button class="scrim" aria-label="Beheermenu sluiten" onclick={() => (drawerOpen = false)}
		></button>
	{/if}

	<div class="amain">
		{@render children()}
	</div>
</div>

<style>
	/* De publieke shell-chrome onderdrukken zolang het beheer open staat. Gated
	   op <html class="admin-shell"> (gezet in onMount) — dus nooit actief buiten
	   het beheer, ook niet als deze stylesheet blijft hangen. */
	:global(html.admin-shell .shell) {
		grid-template-columns: minmax(0, 1fr) !important;
	}
	:global(html.admin-shell .shell > .sidebar),
	:global(html.admin-shell .shell > .topbar),
	:global(html.admin-shell .site-footer) {
		display: none !important;
	}

	.admin {
		display: grid;
		grid-template-columns: 220px minmax(0, 1fr);
		min-height: 100vh;
	}

	/* ── Merk ─────────────────────────────────────────────────────── */
	.sbrand {
		display: inline-flex;
		align-items: center;
		gap: 8px;
		font-weight: 750;
		font-size: 0.95rem;
		letter-spacing: -0.01em;
		color: var(--text);
		white-space: nowrap;
	}
	.mark {
		width: 20px;
		height: 20px;
		border-radius: 6px;
		flex: none;
		background: conic-gradient(
			from 210deg,
			var(--dom-fury),
			var(--dom-body),
			var(--dom-order),
			var(--dom-calm),
			var(--dom-mind),
			var(--dom-chaos),
			var(--dom-fury)
		);
	}
	.tagb {
		font-family: ui-monospace, 'SF Mono', 'Cascadia Code', Menlo, Consolas, monospace;
		font-size: 0.56rem;
		letter-spacing: 0.1em;
		text-transform: uppercase;
		color: var(--accent-ink);
		background: var(--accent);
		border-radius: 4px;
		padding: 2px 6px;
	}

	/* ── Zijbalk ──────────────────────────────────────────────────── */
	.asidebar {
		display: flex;
		flex-direction: column;
		gap: 12px;
		padding: 16px 12px;
		background: var(--surface);
		border-right: 1px solid var(--border);
	}
	.back {
		font-size: 0.78rem;
		color: var(--muted);
		text-decoration: none;
	}
	.back:hover {
		color: var(--text);
	}
	.anav {
		display: flex;
		flex-direction: column;
		gap: 2px;
		margin-top: 4px;
	}
	.anav a {
		display: flex;
		align-items: center;
		gap: 9px;
		padding: 7px 10px;
		border-radius: 8px;
		color: var(--muted);
		text-decoration: none;
		font-size: 0.86rem;
	}
	.anav a:hover {
		color: var(--text);
		background: var(--surface-deep);
	}
	.anav a.on {
		background: var(--surface-deep);
		color: var(--text);
		font-weight: 600;
	}
	.anav a.danger {
		color: var(--err);
	}
	.anav a.danger:hover {
		background: var(--err-soft);
	}
	.anav a .lbl {
		min-width: 0;
	}
	.anav a .b {
		margin-left: auto;
		font-size: 0.66rem;
		color: var(--muted);
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-radius: 999px;
		padding: 1px 7px;
	}
	.anav a.on .b {
		background: color-mix(in srgb, var(--accent) 22%, transparent);
		border-color: transparent;
		color: var(--text);
	}
	.signed-out {
		color: var(--muted);
		font-size: 0.85rem;
		margin: 6px 4px;
	}
	.sbottom {
		margin-top: auto;
		padding-top: 12px;
		border-top: 1px solid var(--border);
	}
	.theme-toggle {
		display: flex;
		align-items: center;
		gap: 8px;
		width: 100%;
		background: transparent;
		border: 0;
		color: var(--muted);
		font-size: 0.8rem;
		padding: 6px 9px;
		cursor: pointer;
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

	.amain {
		min-width: 0;
	}

	/* ── Mobiele bovenbalk + drawer ───────────────────────────────── */
	.atop {
		display: none;
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
	.scrim {
		display: none;
		position: fixed;
		inset: 0;
		z-index: 25;
		background: rgba(10, 12, 18, 0.5);
		border: 0;
	}

	/* ── Desktop: vaste zijbalk ───────────────────────────────────── */
	@media (min-width: 760px) {
		.asidebar {
			position: sticky;
			top: 0;
			height: 100vh;
			overflow-y: auto;
		}
	}

	/* ── Mobiel: zijbalk als slide-over ───────────────────────────── */
	@media (max-width: 759px) {
		.admin {
			grid-template-columns: minmax(0, 1fr);
		}
		.atop {
			display: flex;
		}
		.asidebar {
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
		.asidebar.open {
			transform: translateX(0);
		}
		.admin.drawer-open .scrim {
			display: block;
		}
	}
</style>
