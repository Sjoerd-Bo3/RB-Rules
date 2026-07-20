<script lang="ts">
	import RbText from '$lib/RbText.svelte';
	import { useShell } from '$lib/shell.svelte';

	let { data } = $props();
	const shell = useShell();

	// SectionRefs ("051, 103.4, …") → unieke §-links naar de regels-browser.
	// Als platte tekst gerenderd (geen {@html}) — de docs zijn gegenereerde
	// tekst, Svelte escapet alles.
	function refs(s: string | null): string[] {
		return s ? [...new Set(s.split(',').map((r) => r.trim()).filter(Boolean))] : [];
	}

	// Concept-inhoudsopgave in de contextuele rechterrail (desktop) / onder de
	// leeskolom (mobiel) — houdt de leeskolom rustig.
	$effect(() => {
		if (data.docs.length === 0) return;
		shell.rail = { snippet: rail, kind: 'context', title: 'Concepten' };
		return () => (shell.rail = null);
	});
</script>

<svelte:head><title>Spelbegrip — Poracle</title></svelte:head>

{#snippet rail()}
	<nav class="rail-nav">
		{#each data.docs as d (d.id)}
			<a href="#{d.topic}">{d.title}</a>
		{/each}
	</nav>
{/snippet}

<main>
	<h1>Spelbegrip <span>per concept</span></h1>
	<p class="subtitle">
		De flow van het spel, gedistilleerd uit de officiële regels en door de beheerder
		goedgekeurd. De regels zelf blijven normatief — elke alinea verwijst naar de §-secties
		waarop hij is gebaseerd.
	</p>

	{#if data.apiDown}
		<p class="warn">rb-api is niet bereikbaar.</p>
	{:else if data.docs.length === 0}
		<p class="meta">Nog geen goedgekeurd spelbegrip — de beheerder reviewt de gegenereerde docs eerst.</p>
	{:else}
		{#each data.docs as d (d.id)}
			<section id={d.topic} class="doc panel">
				<h2>{d.title}</h2>
				<p class="body"><RbText text={d.body} /></p>
				{#if refs(d.sectionRefs).length}
					<p class="refs meta">
						Gebaseerd op:
						{#each refs(d.sectionRefs) as r (r)}
							<a href="/rules/{encodeURIComponent(r)}">§ {r}</a>
						{/each}
					</p>
				{/if}
			</section>
		{/each}
	{/if}
</main>

<style>
	main { max-width: 720px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: var(--accent); }
	.subtitle { color: var(--muted); margin-bottom: 18px; max-width: 66ch; }
	/* scroll-margin: anker niet onder de sticky header laten verdwijnen. */
	.doc { padding: 16px 18px; margin-bottom: 12px; scroll-margin-top: 70px; }
	.doc h2 { margin: 0 0 8px; font-size: 1.1rem; color: var(--accent); }
	.body { margin: 0; white-space: pre-wrap; line-height: 1.7; overflow-wrap: anywhere; }
	.refs { margin: 10px 0 0; display: flex; flex-wrap: wrap; gap: 4px 10px; }
	.refs a { color: var(--accent); text-decoration: none; white-space: nowrap; }
	.refs a:hover { text-decoration: underline; }
	.meta { color: var(--muted); font-size: 0.85rem; }
	.warn { color: var(--err); }

	/* Contextuele rail (concept-TOC) — zelfde vormtaal als de leespagina's. */
	.rail-nav { display: flex; flex-direction: column; gap: 2px; }
	.rail-nav a {
		color: var(--muted); text-decoration: none; font-size: 0.88rem;
		padding: 5px 8px; border-radius: 6px; border-left: 2px solid var(--border);
	}
	.rail-nav a:hover { color: var(--text); border-left-color: var(--accent); background: var(--surface-deep); }
</style>
