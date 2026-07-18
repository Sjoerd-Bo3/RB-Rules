<script lang="ts">
	import { enhance } from '$app/forms';

	let { data, form } = $props();
</script>

<svelte:head><title>Inloggen bevestigen — Poracle</title></svelte:head>

<main>
	<h1>Inloggen bevestigen</h1>

	{#if !data.token}
		<p class="warn">
			Deze link is onvolledig. <a href="/account">Vraag een nieuwe inloglink aan.</a>
		</p>
	{:else}
		<p class="sub">Klik op de knop om het inloggen af te ronden.</p>
		<form method="POST" action="?/confirm" use:enhance class="panel narrow">
			<input type="hidden" name="token" value={data.token} />
			{#if form?.error}<p class="warn">{form.error} <a href="/account">Naar de accountpagina.</a></p>{/if}
			<button type="submit">Bevestig inloggen</button>
		</form>
	{/if}
</main>

<style>
	main { max-width: 640px; margin: 0 auto; padding: 24px 20px; }
	h1 { margin: 0 0 10px; }
	.sub { color: var(--muted); margin: 0 0 18px; }
	.narrow { display: flex; flex-direction: column; gap: 12px; padding: 16px; max-width: 420px; }
	button {
		background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 10px 16px; font-weight: 600; cursor: pointer;
		align-self: flex-start;
	}
	.warn { color: var(--err); }
	.warn a { color: var(--accent); }
</style>
