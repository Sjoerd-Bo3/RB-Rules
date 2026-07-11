<script lang="ts">
	import { page } from '$app/state';
</script>

<main>
	<h1>Sectie niet gevonden</h1>
	<p class="meta">
		{page.error?.message ?? 'Deze regelsectie bestaat niet (meer) in de geïndexeerde documenten.'}
		Tussenliggende nummers uit een broodkruimel bestaan niet altijd als eigen sectie.
	</p>
	<p><a href="/rules">Terug naar het regeloverzicht</a></p>
</main>

<style>
	main { max-width: 860px; margin: 0 auto; padding: 40px 20px; }
	.meta { color: var(--muted); }
	a { color: var(--accent); }
</style>
