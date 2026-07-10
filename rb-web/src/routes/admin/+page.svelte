<script lang="ts">
	import { enhance } from '$app/forms';

	let { data, form } = $props();

	interface Source {
		id: string;
		name: string;
		url: string;
		type: string;
		trustTier: number;
		rank: number;
		cadence: string;
		enabled: boolean;
		lastChecked: string | null;
	}
	interface Log {
		id: number;
		kind: string;
		ref: string | null;
		status: string;
		detail: string | null;
		createdAt: string;
	}

	const sources = $derived(data.sources as Source[]);
	const logs = $derived(data.logs as Log[]);
</script>

<svelte:head><title>Beheer — RB Rules</title></svelte:head>

<main>
	{#if !data.authed}
		<h1>Beheer — inloggen</h1>
		<form method="POST" action="?/login" use:enhance class="card narrow">
			<label>
				Wachtwoord
				<input type="password" name="password" autocomplete="current-password" />
			</label>
			{#if form?.error}<p class="warn">{form.error}</p>{/if}
			<button type="submit">Inloggen</button>
		</form>
	{:else}
		<header class="head">
			<h1>Beheer</h1>
			<div class="row">
				<form method="POST" action="?/scan" use:enhance><button>Scan bronnen</button></form>
				<form method="POST" action="?/cardsync" use:enhance><button>Kaarten sync</button></form>
				<form method="POST" action="?/logout" use:enhance><button class="ghost">Uitloggen</button></form>
			</div>
		</header>

		{#if data.apiDown}<p class="warn">rb-api is niet bereikbaar.</p>{/if}
		{#if form?.scanned}<p class="meta">Scan: {JSON.stringify(form.scanned)}</p>{/if}
		{#if form?.cardsync}<p class="meta">Kaart-sync: {JSON.stringify(form.cardsync)}</p>{/if}

		<h2>Bronnen</h2>
		<table>
			<thead>
				<tr><th>Bron</th><th>Trust</th><th>Cadans</th><th>Laatst</th><th>Aan</th></tr>
			</thead>
			<tbody>
				{#each sources as s (s.id)}
					<tr>
						<td><strong>{s.name}</strong><br /><a class="meta" href={s.url}>{s.id}</a></td>
						<td>{s.trustTier}</td>
						<td>{s.cadence}</td>
						<td class="meta">
							{s.lastChecked ? new Date(s.lastChecked).toLocaleString('nl-NL') : '—'}
						</td>
						<td>
							<form method="POST" action="?/toggle" use:enhance>
								<input type="hidden" name="id" value={s.id} />
								<input type="hidden" name="enabled" value={String(!s.enabled)} />
								<button class="ghost">{s.enabled ? '✅' : '⬜'}</button>
							</form>
						</td>
					</tr>
				{/each}
			</tbody>
		</table>

		<h2>Logs</h2>
		<table>
			<thead>
				<tr><th>Tijd</th><th>Soort</th><th>Ref</th><th>Status</th><th>Detail</th></tr>
			</thead>
			<tbody>
				{#each logs as l (l.id)}
					<tr>
						<td class="meta">{new Date(l.createdAt).toLocaleString('nl-NL')}</td>
						<td>{l.kind}</td>
						<td class="meta">{l.ref ?? '—'}</td>
						<td>{l.status}</td>
						<td>{l.detail ?? ''}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</main>

<style>
	main { max-width: 960px; margin: 0 auto; padding: 24px 20px; }
	.head { display: flex; justify-content: space-between; align-items: center; }
	.row { display: flex; gap: 8px; }
	.card { background: #16233b; border: 1px solid #243551; border-radius: 12px; padding: 16px; }
	.narrow { max-width: 360px; }
	label { display: block; color: #9fb0cc; margin-bottom: 10px; }
	input { width: 100%; background: #0b1322; color: #e7eefc; border: 1px solid #243551; border-radius: 8px; padding: 8px 10px; }
	button { background: #d98a4e; color: #1a1206; border: 0; border-radius: 8px; padding: 8px 14px; font-weight: 600; cursor: pointer; }
	button.ghost { background: transparent; color: #9fb0cc; border: 1px solid #243551; }
	table { width: 100%; border-collapse: collapse; margin-bottom: 24px; }
	th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid #243551; }
	th { color: #9fb0cc; font-size: 0.85rem; }
	.meta { color: #9fb0cc; font-size: 0.85rem; }
	.warn { color: #ff8b8e; }
</style>
