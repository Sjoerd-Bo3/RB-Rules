<script lang="ts">
	// Globale foutpagina (#219). Rendert binnen de shell (`+layout.svelte`), dus
	// de zijbalk en footer blijven staan. Bij een 404 een vriendelijke,
	// "zoekende" poro met terug-links; bij elke andere status een nette generieke
	// variant (kop = status + boodschap). De status → tekst-logica leeft in
	// `$lib/errorCopy` (unit-getest); deze component is puur presentatie.
	import { page } from '$app/state';
	import PoroMark from '$lib/PoroMark.svelte';
	import { errorCopy } from '$lib/errorCopy';

	const copy = $derived(errorCopy(page.status, page.error?.message));
</script>

<svelte:head>
	<title>{copy.is404 ? '404 — niet gevonden' : `Fout ${page.status}`} · Poracle</title>
</svelte:head>

<main class="err">
	<div class="art">
		<PoroMark
			size={140}
			animate={copy.is404 ? 'wink' : 'idle'}
			label={copy.is404 ? 'Poro die zoekt' : 'Poro'}
		/>
	</div>
	<h1>{copy.heading}</h1>
	<p>{copy.body}</p>
	<nav class="actions" aria-label="Verder navigeren">
		<a class="btn primary" href="/">Naar het overzicht</a>
		<a class="btn" href="/ask">Vraag het de poro</a>
	</nav>
</main>

<style>
	.err {
		max-width: 560px;
		margin: 0 auto;
		padding: clamp(48px, 9vh, 96px) 20px 80px;
		display: flex;
		flex-direction: column;
		align-items: center;
		text-align: center;
		gap: 14px;
	}
	.art {
		display: inline-flex;
		margin-bottom: 4px;
	}
	.err h1 {
		font-size: 1.5rem;
		margin: 0;
		letter-spacing: -0.02em;
	}
	.err p {
		color: var(--muted);
		margin: 0;
		max-width: 46ch;
	}
	.actions {
		display: flex;
		flex-wrap: wrap;
		justify-content: center;
		gap: 10px;
		margin-top: 8px;
	}
	.btn {
		display: inline-flex;
		align-items: center;
		justify-content: center;
		padding: 10px 18px;
		border-radius: var(--radius);
		border: 1px solid var(--border);
		background: var(--surface);
		color: var(--text);
		font-weight: 600;
		text-decoration: none;
		white-space: nowrap;
	}
	.btn:hover {
		border-color: var(--border-strong);
	}
	.btn.primary {
		background: var(--accent);
		color: var(--accent-ink);
		border-color: transparent;
	}
</style>
