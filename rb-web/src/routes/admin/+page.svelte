<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';

	let { data, form } = $props();

	interface Source {
		id: string; name: string; url: string; trustTier: number;
		cadence: string; enabled: boolean; lastChecked: string | null;
	}
	interface Job { name: string; status: string; startedAt: string; finishedAt: string | null; detail: string | null; }
	interface Log { id: number; kind: string; ref: string | null; status: string; detail: string | null; createdAt: string; }
	interface Status {
		running: Job | null;
		lastJob: Job | null;
		counts: Record<string, number>;
		logs: Log[];
	}

	const JOBS: { name: string; label: string; hint: string }[] = [
		{ name: 'scan', label: '📡 Scan bronnen', hint: 'regels & changelogs ophalen' },
		{ name: 'cards', label: '🃏 Kaarten sync', hint: 'nieuwe sets/reveals' },
		{ name: 'embed', label: '🧠 Embeddings', hint: 'semantisch zoeken voeden' },
		{ name: 'mine', label: '⛏️ Mechanieken', hint: 'LLM-mining kaartteksten' },
		{ name: 'rules', label: '📖 Regels-index', hint: 'sectie-chunks + embeddings' },
		{ name: 'bans', label: '🚫 Bans/errata', hint: 'structureren uit officieel' },
		{ name: 'graph', label: '🕸️ Graph sync', hint: 'Neo4j bijwerken' },
		{ name: 'interactions', label: '⚡ Interacties', hint: 'kandidaten + LLM-verify' }
	];

	const sources = $derived(data.sources as Source[]);
	// svelte-ignore state_referenced_locally
	let live = $state<Status | null>(data.status as Status | null);
	const running = $derived(live?.running ?? null);

	// Live polling zolang de admin open staat; sneller pollen als er iets draait.
	$effect(() => {
		if (!data.authed) return;
		let stop = false;
		const tick = async () => {
			try {
				const r = await fetch('/admin/status');
				if (r.ok) live = await r.json();
			} catch { /* rb-api even weg — volgende poll */ }
			if (!stop) setTimeout(tick, live?.running ? 2000 : 6000);
		};
		tick();
		return () => { stop = true; };
	});

	function fmtAgo(iso: string | null): string {
		if (!iso) return '';
		const s = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
		return s < 60 ? `${s}s` : s < 3600 ? `${Math.round(s / 60)}m` : `${Math.round(s / 3600)}u`;
	}

	const TILES: { key: string; label: string }[] = [
		{ key: 'cards', label: 'Kaarten' },
		{ key: 'cardsEmbedded', label: 'Geëmbed' },
		{ key: 'cardsMined', label: 'Gemined' },
		{ key: 'ruleChunks', label: 'Regel-chunks' },
		{ key: 'bans', label: 'Bans' },
		{ key: 'errata', label: 'Errata' },
		{ key: 'interactions', label: 'Interacties' },
		{ key: 'changes', label: 'Wijzigingen' },
		{ key: 'openCorrections', label: 'Open correcties' }
	];
</script>

<svelte:head><title>Beheer — RB Rules</title></svelte:head>

<main>
	{#if !data.authed}
		<h1>Beheer — inloggen</h1>
		<form method="POST" action="?/login" use:enhance class="card narrow">
			<label>Wachtwoord <input type="password" name="password" autocomplete="current-password" /></label>
			{#if form?.error}<p class="warn">{form.error}</p>{/if}
			<button type="submit">Inloggen</button>
		</form>
	{:else}
		<header class="head">
			<h1>Beheer</h1>
			<form method="POST" action="?/logout" use:enhance><button class="ghost">Uitloggen</button></form>
		</header>

		{#if data.apiDown}<p class="warn">rb-api is niet bereikbaar.</p>{/if}

		<!-- Live status-banner -->
		{#if running}
			<div class="banner running">
				<span class="spinner"></span>
				<strong>{JOBS.find((j) => j.name === running.name)?.label ?? running.name}</strong>
				draait… ({fmtAgo(running.startedAt)})
			</div>
		{:else if live?.lastJob}
			{@const last = live.lastJob}
			<div class="banner {last.status === 'ok' ? 'ok' : 'err'}">
				{last.status === 'ok' ? '✅' : '❌'}
				<strong>{JOBS.find((j) => j.name === last.name)?.label ?? last.name}</strong>
				· {last.detail ?? last.status}
				<span class="meta">({fmtAgo(last.finishedAt)} geleden)</span>
			</div>
		{/if}
		{#if form?.error}<p class="warn">{form.error}</p>{/if}

		<!-- Status-tegels -->
		{#if live?.counts}
			<div class="tiles">
				{#each TILES as t (t.key)}
					<div class="tile">
						<span class="num">{live.counts[t.key] ?? 0}</span>
						<span class="lbl">{t.label}</span>
					</div>
				{/each}
			</div>
		{/if}

		<!-- Job-knoppen -->
		<h2>Acties</h2>
		<div class="jobs">
			{#each JOBS as j (j.name)}
				<form method="POST" action="?/job" use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }}>
					<input type="hidden" name="name" value={j.name} />
					<button disabled={running !== null} title={j.hint}>
						{running?.name === j.name ? '⏳ ' : ''}{j.label}
					</button>
					<span class="hint">{j.hint}</span>
				</form>
			{/each}
		</div>

		<!-- Bronnen -->
		<h2>Bronnen</h2>
		<table>
			<thead><tr><th>Bron</th><th>Trust</th><th>Cadans</th><th>Laatst</th><th>Aan</th></tr></thead>
			<tbody>
				{#each sources as s (s.id)}
					<tr>
						<td><strong>{s.name}</strong><br /><a class="meta" href={s.url}>{s.id}</a></td>
						<td>{s.trustTier}</td>
						<td>{s.cadence}</td>
						<td class="meta">{s.lastChecked ? new Date(s.lastChecked).toLocaleString('nl-NL') : '—'}</td>
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

		<!-- Live logs -->
		<h2>Recente activiteit <span class="meta">(live)</span></h2>
		<table>
			<thead><tr><th>Tijd</th><th>Soort</th><th>Ref</th><th>Status</th><th>Detail</th></tr></thead>
			<tbody>
				{#each live?.logs ?? [] as l (l.id)}
					<tr>
						<td class="meta">{new Date(l.createdAt).toLocaleTimeString('nl-NL')}</td>
						<td>{l.kind}</td>
						<td class="meta">{l.ref ?? '—'}</td>
						<td><span class="badge {l.status === 'error' ? 'err' : l.status === 'changed' || l.status === 'new' ? 'warn-b' : 'ok-b'}">{l.status}</span></td>
						<td class="meta">{l.detail ?? ''}</td>
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</main>

<style>
	main { max-width: 1080px; margin: 0 auto; padding: 24px 20px; }
	.head { display: flex; justify-content: space-between; align-items: center; }
	.card { background: #16233b; border: 1px solid #243551; border-radius: 12px; padding: 16px; }
	.narrow { max-width: 360px; }
	label { display: block; color: #9fb0cc; margin-bottom: 10px; }
	input { width: 100%; background: #0b1322; color: #e7eefc; border: 1px solid #243551; border-radius: 8px; padding: 8px 10px; }
	button { background: #d98a4e; color: #1a1206; border: 0; border-radius: 8px; padding: 8px 14px; font-weight: 600; cursor: pointer; }
	button:disabled { opacity: 0.5; cursor: wait; }
	button.ghost { background: transparent; color: #9fb0cc; border: 1px solid #243551; }
	h2 { color: #d98a4e; font-size: 1.05rem; margin: 26px 0 10px; }
	.banner { display: flex; align-items: center; gap: 10px; border-radius: 10px; padding: 10px 14px; margin: 14px 0; }
	.banner.running { background: #d98a4e22; border: 1px solid #d98a4e66; }
	.banner.ok { background: #58c08a1a; border: 1px solid #58c08a55; }
	.banner.err { background: #e5484d1f; border: 1px solid #e5484d66; }
	.spinner { width: 16px; height: 16px; border-radius: 50%; border: 2px solid #243551; border-top-color: #d98a4e; animation: spin 0.8s linear infinite; }
	@keyframes spin { to { transform: rotate(360deg); } }
	.tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(120px, 1fr)); gap: 10px; }
	.tile { background: #16233b; border: 1px solid #243551; border-radius: 10px; padding: 10px 12px; display: flex; flex-direction: column; }
	.tile .num { font-size: 1.4rem; font-weight: 700; }
	.tile .lbl { color: #9fb0cc; font-size: 0.78rem; }
	.jobs { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 10px; }
	.jobs form { display: flex; flex-direction: column; gap: 3px; }
	.jobs .hint { color: #9fb0cc; font-size: 0.75rem; padding-left: 2px; }
	table { width: 100%; border-collapse: collapse; margin-bottom: 20px; }
	th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid #243551; }
	th { color: #9fb0cc; font-size: 0.85rem; }
	.meta { color: #9fb0cc; font-size: 0.85rem; }
	.badge { font-size: 0.72rem; text-transform: uppercase; padding: 2px 8px; border-radius: 999px; font-weight: 700; }
	.badge.ok-b { background: #58c08a22; color: #7fd1a8; }
	.badge.warn-b { background: #e0a32e22; color: #f3c469; }
	.badge.err { background: #e5484d22; color: #ff8b8e; }
	.warn { color: #ff8b8e; }
	a { color: inherit; }
</style>
