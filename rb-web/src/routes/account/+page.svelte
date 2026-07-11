<script lang="ts">
	import { enhance } from '$app/forms';

	let { data, form } = $props();

	const account = $derived(data.account);
	const quotaLeft = $derived(
		account ? Math.max(0, account.dailyQuota - account.questionsToday) : 0
	);
	const photoLeft = $derived(
		account ? Math.max(0, account.dailyPhotoQuota - account.photosToday) : 0
	);
</script>

<svelte:head><title>Account — RB Rules</title></svelte:head>

<main>
	<h1>Account</h1>

	{#if data.apiDown}
		<p class="warn">De server is even niet bereikbaar — probeer het zo opnieuw.</p>
	{:else if account}
		<div class="panel block">
			<p class="row-line">
				<strong>{account.email}</strong>
				{#if account.blocked}
					<span class="badge err">geblokkeerd</span>
				{:else}
					<span class="badge ok-b">actief</span>
				{/if}
			</p>
			<p class="meta">Account sinds {new Date(account.createdAt).toLocaleDateString('nl-NL')}</p>
			{#if account.blocked}
				<p class="warn">
					Dit account is geblokkeerd door de beheerder — vragen stellen kan tijdelijk niet.
				</p>
			{/if}
		</div>

		<h2>Gebruik vandaag</h2>
		<div class="panel block">
			<p>
				<strong>{account.questionsToday}</strong> van {account.dailyQuota} vragen gebruikt
				<span class="meta">({quotaLeft} over)</span>
			</p>
			<p>
				<strong>{account.photosToday}</strong> van {account.dailyPhotoQuota} foto-vragen gebruikt
				<span class="meta">({photoLeft} over)</span>
			</p>
			<p class="meta">
				De teller staat elke dag (UTC) weer op nul. Ingelogd geldt je eigen dagquotum in
				plaats van de krappere anonieme limiet.
			</p>
		</div>

		<form method="POST" action="?/logout" use:enhance>
			<button class="ghost">Uitloggen</button>
		</form>
	{:else if form && 'sent' in form && form.sent}
		<div class="panel block">
			<p><strong>Controleer je mail</strong></p>
			<p>
				Als {form.email} klopt, staat er nu een inloglink in je inbox. De link is 15 minuten
				geldig en werkt één keer.
			</p>
			{#if form.devLink}
				<p class="meta">
					Ontwikkelmodus — de link zonder mailserver:
					<a href={form.devLink} class="dev-link">{form.devLink}</a>
				</p>
			{/if}
		</div>
	{:else}
		<p class="sub">
			Log in met je e-mailadres om vragen te stellen met een ruimer dagquotum. Je krijgt een
			inloglink per mail — een wachtwoord is niet nodig. Regels, kaarten en de rest van de site
			blijven ook zonder account gewoon toegankelijk.
		</p>
		<form method="POST" action="?/login" use:enhance class="panel narrow">
			<label>
				E-mailadres
				<input
					type="email"
					name="email"
					value={form && 'email' in form ? (form.email as string) : ''}
					autocomplete="email"
					required
					placeholder="jij@example.com"
				/>
			</label>
			{#if form?.error}<p class="warn">{form.error}</p>{/if}
			<button type="submit">Stuur inloglink</button>
		</form>
	{/if}
</main>

<style>
	main { max-width: 640px; margin: 0 auto; padding: 24px 20px; }
	h1 { margin: 0 0 10px; }
	h2 { font-size: 1.02rem; color: var(--accent); margin: 22px 0 10px; }
	.sub { color: var(--muted); margin: 0 0 18px; line-height: 1.55; }
	.block { padding: 14px 16px; margin-bottom: 12px; }
	.block p { margin: 0 0 8px; }
	.block p:last-child { margin-bottom: 0; }
	.row-line { display: flex; align-items: baseline; gap: 8px; flex-wrap: wrap; }
	.narrow { display: flex; flex-direction: column; gap: 12px; padding: 16px; max-width: 420px; }
	label { display: flex; flex-direction: column; gap: 6px; color: var(--muted); font-size: 0.9rem; }
	input {
		background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px;
		font-size: 16px; /* iOS zoomt in op form-controls kleiner dan 16px */
	}
	button {
		background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 10px 16px; font-weight: 600; cursor: pointer;
		align-self: flex-start;
	}
	button.ghost { background: transparent; color: var(--muted); border: 1px solid var(--border); }
	.meta { color: var(--muted); font-size: 0.85rem; }
	.warn { color: var(--err); }
	/* Lange dev-links mogen op 390px nooit horizontale overflow geven. */
	.dev-link { overflow-wrap: anywhere; color: var(--accent); }
</style>
