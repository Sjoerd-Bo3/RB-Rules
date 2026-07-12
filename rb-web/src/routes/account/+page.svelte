<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import {
		assertionToJson,
		registrationToJson,
		toCreationOptions,
		toRequestOptions
	} from '$lib/passkeys';

	let { data, form } = $props();

	const account = $derived(data.account);
	const quotaLeft = $derived(
		account ? Math.max(0, account.dailyQuota - account.questionsToday) : 0
	);
	const photoLeft = $derived(
		account ? Math.max(0, account.dailyPhotoQuota - account.photosToday) : 0
	);

	// Passkeys (#109) hebben client-side JS nodig (navigator.credentials);
	// zonder ondersteuning blijft de magic-link-flow het werkende pad.
	let supported = $state(true);
	$effect(() => {
		supported = typeof window !== 'undefined' && 'PublicKeyCredential' in window;
	});

	let busy = $state(false);
	let ceremonyError = $state<string | null>(null);
	let registerEmail = $state('');
	let confirmRemove = $state<number | null>(null);

	// Route-hergebruik: bij uit- en weer inloggen blijft de component leven —
	// oude foutmeldingen en bevestigingsstanden horen dan niet te blijven staan.
	$effect(() => {
		void account;
		ceremonyError = null;
		confirmRemove = null;
	});

	function ceremonyMessage(e: unknown): string {
		if (e instanceof DOMException && e.name === 'NotAllowedError')
			return 'Geannuleerd of geweigerd — probeer het opnieuw.';
		if (e instanceof DOMException && e.name === 'InvalidStateError')
			return 'Dit apparaat heeft al een passkey voor dit account.';
		return `Passkey-actie mislukt (${e instanceof Error ? e.message : String(e)}).`;
	}

	/** Eén ceremonie-flow voor login, registratie en passkey-toevoegen: opties
	 *  halen, de authenticator laten tekenen, antwoord verzilveren. De cookie
	 *  wordt server-side gezet (verify-proxy); daarna herlaadt invalidateAll
	 *  de accountgegevens. */
	async function runCeremony(kind: 'login' | 'register', email?: string) {
		busy = true;
		ceremonyError = null;
		try {
			const optRes = await fetch('/account/passkey/options', {
				method: 'POST',
				headers: { 'content-type': 'application/json' },
				body: JSON.stringify({ kind, email })
			});
			const opt = await optRes.json();
			if (!optRes.ok) {
				ceremonyError = opt?.error ?? 'De passkey-stap kon niet gestart worden.';
				return;
			}
			const credential =
				kind === 'login'
					? await navigator.credentials.get({ publicKey: toRequestOptions(opt.options) })
					: await navigator.credentials.create({ publicKey: toCreationOptions(opt.options) });
			if (!(credential instanceof PublicKeyCredential)) {
				ceremonyError = 'De browser gaf geen passkey terug.';
				return;
			}
			const verRes = await fetch('/account/passkey/verify', {
				method: 'POST',
				headers: { 'content-type': 'application/json' },
				body: JSON.stringify({
					kind,
					token: opt.token,
					response:
						kind === 'login' ? assertionToJson(credential) : registrationToJson(credential)
				})
			});
			const ver = await verRes.json();
			if (!verRes.ok) {
				ceremonyError = ver?.error ?? 'De passkey kon niet geverifieerd worden.';
				return;
			}
			registerEmail = '';
			await invalidateAll();
		} catch (e) {
			ceremonyError = ceremonyMessage(e);
		} finally {
			busy = false;
		}
	}
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

		<h2>Passkeys</h2>
		<div class="panel block">
			{#if data.passkeys.length === 0}
				<p class="warn">
					Dit account heeft nog geen passkeys. Zonder passkey kun je alleen inloggen via een
					inloglink per e-mail — en die werkt alleen als de server een mailvoorziening heeft.
				</p>
			{:else}
				<ul class="keys">
					{#each data.passkeys as key (key.id)}
						<li>
							<div class="key-info">
								<strong>{key.name}</strong>
								<span class="meta">
									aangemaakt {new Date(key.createdAt).toLocaleDateString('nl-NL')}
									· {key.lastUsedAt
										? `laatst gebruikt ${new Date(key.lastUsedAt).toLocaleDateString('nl-NL')}`
										: 'nog niet gebruikt'}
								</span>
							</div>
							{#if confirmRemove === key.id}
								<form method="POST" action="?/removePasskey" use:enhance class="confirm">
									<input type="hidden" name="id" value={key.id} />
									{#if data.passkeys.length === 1}
										<p class="warn">
											Dit is je laatste passkey. Zolang er geen accountherstel per e-mail is,
											wordt dit account na verwijdering onbereikbaar.
										</p>
									{/if}
									<div class="confirm-row">
										<button class="danger">Ja, verwijder</button>
										<button type="button" class="ghost" onclick={() => (confirmRemove = null)}>
											Annuleer
										</button>
									</div>
								</form>
							{:else}
								<button type="button" class="ghost" onclick={() => (confirmRemove = key.id)}>
									Verwijderen
								</button>
							{/if}
						</li>
					{/each}
				</ul>
			{/if}
			{#if form && 'passkeyError' in form && form.passkeyError}
				<p class="warn">{form.passkeyError}</p>
			{/if}
			{#if supported}
				<button type="button" disabled={busy} onclick={() => runCeremony('register')}>
					Passkey toevoegen
				</button>
				<p class="meta">
					Voeg op elk apparaat (telefoon, laptop) een eigen passkey toe — dan blijft je account
					bereikbaar als er eentje kwijtraakt.
				</p>
			{:else}
				<p class="meta">Deze browser ondersteunt geen passkeys.</p>
			{/if}
			{#if ceremonyError}<p class="warn">{ceremonyError}</p>{/if}
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
			Log in om vragen te stellen met een ruimer dagquotum. Een passkey (Face ID,
			vingerafdruk of security key) is de snelste route — geen wachtwoord en geen mail nodig.
			Regels, kaarten en de rest van de site blijven ook zonder account gewoon toegankelijk.
		</p>

		<section class="panel narrow">
			<h2 class="panel-title">Inloggen</h2>
			<button type="button" disabled={busy || !supported} onclick={() => runCeremony('login')}>
				Log in met een passkey
			</button>
			<p class="meta">Je apparaat toont je passkeys en je kiest er een — meer is het niet.</p>
			{#if !supported}
				<p class="warn">
					Deze browser ondersteunt geen passkeys — gebruik de inloglink per e-mail hieronder.
				</p>
			{/if}
		</section>

		<section class="panel narrow">
			<h2 class="panel-title">Nieuw account</h2>
			<label>
				E-mailadres (je accountnaam)
				<input
					type="email"
					bind:value={registerEmail}
					autocomplete="email"
					required
					placeholder="jij@example.com"
					disabled={!supported}
				/>
			</label>
			<button
				type="button"
				disabled={busy || !supported || !registerEmail.trim()}
				onclick={() => runCeremony('register', registerEmail)}
			>
				Maak account aan met passkey
			</button>
			<p class="meta warn-note">
				Let op: er is nog geen accountherstel per e-mail. Raak je al je passkeys kwijt, dan is
				het account onbereikbaar — voeg daarom daarna op je andere apparaten een extra passkey
				toe.
			</p>
		</section>

		{#if ceremonyError}<p class="warn">{ceremonyError}</p>{/if}

		<details class="alt">
			<summary>Liever een inloglink per e-mail?</summary>
			<p class="meta">
				Dit pad werkt alleen als er op de server een mailvoorziening is geconfigureerd.
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
				{#if form && 'error' in form && form.error}<p class="warn">{form.error}</p>{/if}
				<button type="submit">Stuur inloglink</button>
			</form>
		</details>
	{/if}
</main>

<style>
	main { max-width: 640px; margin: 0 auto; padding: 24px 20px; }
	h1 { margin: 0 0 10px; }
	h2 { font-size: 1.02rem; color: var(--accent); margin: 22px 0 10px; }
	h2.panel-title { margin: 0; }
	.sub { color: var(--muted); margin: 0 0 18px; line-height: 1.55; }
	.block { padding: 14px 16px; margin-bottom: 12px; }
	.block p { margin: 0 0 8px; }
	.block p:last-child { margin-bottom: 0; }
	.row-line { display: flex; align-items: baseline; gap: 8px; flex-wrap: wrap; }
	.narrow {
		display: flex; flex-direction: column; gap: 12px; padding: 16px;
		max-width: 420px; margin-bottom: 14px;
	}
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
	button:disabled { opacity: 0.55; cursor: default; }
	button.ghost { background: transparent; color: var(--muted); border: 1px solid var(--border); }
	button.danger { background: var(--err); color: #fff; }
	.meta { color: var(--muted); font-size: 0.85rem; }
	.warn { color: var(--err); }
	.warn-note { color: var(--warn); }
	/* Lange dev-links mogen op 390px nooit horizontale overflow geven. */
	.dev-link { overflow-wrap: anywhere; color: var(--accent); }

	.keys { list-style: none; margin: 0 0 12px; padding: 0; display: flex; flex-direction: column; gap: 10px; }
	.keys li {
		display: flex; justify-content: space-between; align-items: center;
		gap: 10px; flex-wrap: wrap;
		border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px;
	}
	.key-info { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
	.key-info .meta { overflow-wrap: anywhere; }
	.confirm { display: flex; flex-direction: column; gap: 8px; flex-basis: 100%; }
	.confirm-row { display: flex; gap: 8px; flex-wrap: wrap; }

	details.alt { margin-top: 18px; }
	details.alt summary {
		cursor: pointer; color: var(--muted); font-size: 0.92rem;
		padding: 6px 0;
	}
	details.alt summary:hover { color: var(--text); }
	details.alt .meta { margin: 8px 0 10px; }
</style>
