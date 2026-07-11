<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';

	let { data, form } = $props();

	interface Source {
		id: string; name: string; url: string; trustTier: number;
		cadence: string; enabled: boolean; lastChecked: string | null;
	}
	interface Job {
		name: string; status: string; startedAt: string;
		finishedAt: string | null; detail: string | null; progress: string | null;
	}
	interface Log { id: number; kind: string; ref: string | null; status: string; detail: string | null; createdAt: string; }
	interface Status {
		running: Job | null;
		lastJob: Job | null;
		counts: Record<string, number>;
		logs: Log[];
	}

	const JOBS: { name: string; label: string; hint: string }[] = [
		{ name: 'all', label: 'Alles bijwerken', hint: 'alle stappen in de juiste volgorde' },
		{ name: 'scan', label: 'Bronnen scannen', hint: 'regels en changelogs ophalen en vergelijken' },
		{ name: 'cards', label: 'Kaarten synchroniseren', hint: 'nieuwe sets en reveals binnenhalen' },
		{ name: 'embed', label: 'Embeddings berekenen', hint: 'voedt het semantisch zoeken' },
		{ name: 'mine', label: 'Mechanieken analyseren', hint: 'LLM-analyse van kaartteksten' },
		{ name: 'rules', label: 'Regels indexeren', hint: 'sectie-chunks en embeddings opbouwen' },
		{ name: 'bans', label: 'Bans en errata structureren', hint: 'uit de officiële documenten' },
		{ name: 'primer', label: 'Primer genereren', hint: 'spelbegrip-docs destilleren uit de regels (drafts ter review)' },
		{ name: 'graph', label: 'Graph synchroniseren', hint: 'Neo4j bijwerken' },
		{ name: 'interactions', label: 'Interacties minen', hint: 'kandidaten zoeken en LLM-verifiëren' },
		{ name: 'classify', label: 'Classificaties aanvullen', hint: 'changes zonder samenvatting/duiding alsnog classificeren' }
	];

	interface Correction {
		id: number; scope: string; ref: string; text: string;
		question: string | null; status: string; createdAt: string;
	}

	interface AskTrace {
		id: number; question: string; questionType: string | null;
		sourceBias: string | null; mentionsCard: boolean;
		mechanicMatches: string | null; sections: string | null;
		contextCards: string | null; verifiedRulings: number;
		model: string | null; hadImage: boolean; durationMs: number;
		ok: boolean; createdAt: string;
	}

	const sources = $derived(data.sources as Source[]);
	const corrections = $derived((data.corrections ?? []) as Correction[]);
	const openCorrections = $derived(corrections.filter((c) => c.status === 'unverified'));
	const askTraces = $derived((data.askTraces ?? []) as AskTrace[]);

	interface KnowledgeDoc {
		id: number; kind: string; topic: string; title: string; body: string;
		sectionRefs: string | null; status: string; updatedAt: string;
	}
	const knowledge = $derived((data.knowledge ?? []) as KnowledgeDoc[]);
	const draftDocs = $derived(knowledge.filter((k) => k.status === 'draft'));
	const approvedDocs = $derived(knowledge.filter((k) => k.status === 'approved'));
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
	function jobLabel(name: string): string {
		return JOBS.find((j) => j.name === name)?.label ?? name;
	}

	// Elke tegel klikt door naar het onderliggende overzicht (#61).
	const TILES: { key: string; label: string; slug: string }[] = [
		{ key: 'cards', label: 'Kaarten', slug: 'kaarten' },
		{ key: 'cardsEmbedded', label: 'Geëmbed', slug: 'embeddings' },
		{ key: 'cardsMined', label: 'Geanalyseerd', slug: 'analyse' },
		{ key: 'ruleChunks', label: 'Regelsecties', slug: 'regelsecties' },
		{ key: 'bans', label: 'Bans', slug: 'bans' },
		{ key: 'errata', label: 'Errata', slug: 'errata' },
		{ key: 'interactions', label: 'Interacties', slug: 'interacties' },
		{ key: 'changes', label: 'Wijzigingen', slug: 'wijzigingen' },
		{ key: 'openCorrections', label: 'Open correcties', slug: 'correcties' }
	];
</script>

<svelte:head><title>Beheer — RB Rules</title></svelte:head>

<main>
	{#if !data.authed}
		<h1>Beheer</h1>
		<form method="POST" action="?/login" use:enhance class="panel narrow">
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

		<!-- Live status -->
		{#if running}
			<div class="banner running">
				<span class="spin"></span>
				<div class="banner-body">
					<strong>{jobLabel(running.name)}</strong>
					<span class="meta">draait {fmtAgo(running.startedAt)}</span>
					{#if running.progress}
						<p class="progress">{running.progress}</p>
					{/if}
				</div>
			</div>
		{:else if live?.lastJob}
			{@const last = live.lastJob}
			<div class="banner {last.status === 'ok' ? 'done' : 'failed'}">
				<span class="status-dot {last.status === 'ok' ? 'ok' : 'err'}"></span>
				<div class="banner-body">
					<strong>{jobLabel(last.name)}</strong>
					<span class="meta">{last.status === 'ok' ? 'afgerond' : 'mislukt'} · {fmtAgo(last.finishedAt)} geleden</span>
					{#if last.detail}<p class="progress">{last.detail}</p>{/if}
				</div>
			</div>
		{/if}
		{#if form?.error}<p class="warn">{form.error}</p>{/if}

		<!-- Statistieken -->
		{#if live?.counts}
			<div class="tiles">
				{#each TILES as t (t.key)}
					<a class="tile panel" href="/admin/overview/{t.slug}">
						<span class="num">{live.counts[t.key] ?? 0}</span>
						<span class="lbl">{t.label}</span>
					</a>
				{/each}
			</div>
		{/if}

		<!-- Acties -->
		<h2>Acties</h2>
		<form method="POST" action="?/job" use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }} class="job panel job-all">
			<input type="hidden" name="name" value="all" />
			<div class="job-info">
				<strong>Alles bijwerken</strong>
				<span class="hint">kaarten → scan → regels → bans → embeddings → mechanieken → graph → interacties; een haperende stap stopt de rest niet</span>
			</div>
			<button disabled={running !== null}>
				{running?.name === 'all' ? 'Bezig' : 'Start alles'}
			</button>
		</form>
		<div class="jobs">
			{#each JOBS.filter((j) => j.name !== 'all') as j (j.name)}
				<form method="POST" action="?/job" use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }} class="job panel">
					<input type="hidden" name="name" value={j.name} />
					<div class="job-info">
						<strong>{j.label}</strong>
						<span class="hint">{j.hint}</span>
					</div>
					<button disabled={running !== null}>
						{running?.name === j.name ? 'Bezig' : 'Start'}
					</button>
				</form>
			{/each}
		</div>

		<!-- Reviewqueue (self-learning) -->
		{#if openCorrections.length}
			<h2>Reviewqueue <span class="meta">({openCorrections.length} open — geverifieerde correcties sturen toekomstige antwoorden)</span></h2>
			{#each openCorrections as c (c.id)}
				<div class="correction panel">
					<div class="correction-body">
						{#if c.question}<p class="q">{c.question}</p>{/if}
						<p class="t">{c.text}</p>
						<p class="meta">
							{c.ref === 'down' ? 'Gemeld als onjuist' : c.ref === 'up' ? 'Bevestigd als juist' : c.ref}
							· {new Date(c.createdAt).toLocaleString('nl-NL')}
						</p>
					</div>
					<div class="correction-actions">
						<form method="POST" action="?/verifyCorrection" use:enhance>
							<input type="hidden" name="id" value={c.id} />
							<button title="Maakt dit een gezaghebbende ruling voor toekomstige antwoorden">Verifieer</button>
						</form>
						<form method="POST" action="?/deleteCorrection" use:enhance>
							<input type="hidden" name="id" value={c.id} />
							<button class="ghost small">Verwijder</button>
						</form>
					</div>
				</div>
			{/each}
		{/if}

		<!-- Bronnen -->
		<h2>Bronnen</h2>
		<div class="table-wrap">
		<table>
			<thead><tr><th>Bron</th><th>Trust</th><th>Cadans</th><th>Laatst gecontroleerd</th><th>Actief</th></tr></thead>
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
								<button class="ghost small">{s.enabled ? 'Aan' : 'Uit'}</button>
							</form>
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
		</div>

		<!-- Primer-kennisdocs (#49): spelbegrip reviewen -->
		{#if knowledge.length}
			<h2>Spelbegrip-primer <span class="meta">({approvedDocs.length} goedgekeurd, {draftDocs.length} te reviewen — goedgekeurde docs voeden elke ruling)</span></h2>
			{#each draftDocs as k (k.id)}
				<div class="correction panel">
					<div class="correction-body">
						<p class="t"><strong>{k.title}</strong> <span class="badge warn-b">Draft</span></p>
						<details>
							<summary class="meta">Lees de gegenereerde tekst (gebaseerd op {k.sectionRefs || 'regels'})</summary>
							<p class="primer-body">{k.body}</p>
						</details>
					</div>
					<div class="correction-actions">
						<form method="POST" action="?/approveKnowledge" use:enhance>
							<input type="hidden" name="id" value={k.id} />
							<button title="Goedgekeurde docs gaan mee als spelbegrip in elke vraag">Keur goed</button>
						</form>
						<form method="POST" action="?/deleteKnowledge" use:enhance>
							<input type="hidden" name="id" value={k.id} />
							<button class="ghost small">Verwijder</button>
						</form>
					</div>
				</div>
			{/each}
		{/if}

		<!-- Vraag-traces (#40): denkstappen van de ask-pipeline -->
		{#if askTraces.length}
			<h2>Vraag-traces <span class="meta">(laatste {askTraces.length} — de route die elke vraag door de pipeline nam)</span></h2>
			{#each askTraces as t (t.id)}
				<details class="trace panel">
					<summary>
						<span class="badge {t.ok ? 'ok-b' : 'err'}">{t.questionType ?? '?'}</span>
						<span class="trace-q">{t.question}</span>
						<span class="meta">{(t.durationMs / 1000).toFixed(1)}s{t.hadImage ? ' · foto' : ''} · {new Date(t.createdAt).toLocaleTimeString('nl-NL')}</span>
					</summary>
					<dl>
						<dt>Router</dt>
						<dd>type {t.questionType} · bron-bias {t.sourceBias ?? 'geen'} · kaartnaam herkend: {t.mentionsCard ? 'ja' : 'nee'}{t.mechanicMatches ? ` · mechanieken: ${t.mechanicMatches}` : ''}</dd>
						<dt>Regelsecties</dt>
						<dd>{t.sections || '—'}</dd>
						<dt>Kaartcontext</dt>
						<dd>{t.contextCards || '—'}</dd>
						<dt>Overig</dt>
						<dd>{t.verifiedRulings} geverifieerde rulings · model {t.model} · {t.ok ? 'geslaagd' : 'AI niet beschikbaar'}</dd>
					</dl>
				</details>
			{/each}
		{/if}

		<!-- Live logs -->
		<h2>Recente activiteit <span class="meta live-tag">live</span></h2>
		<div class="table-wrap">
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
		</div>
	{/if}
</main>

<style>
	main { max-width: 1080px; margin: 0 auto; padding: 24px 20px; }
	.head { display: flex; justify-content: space-between; align-items: center; }
	.narrow { max-width: 360px; padding: 18px; }
	label { display: block; color: var(--muted); margin-bottom: 10px; }
	input {
		width: 100%; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;
	}
	button {
		background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 8px 14px; font-weight: 600; cursor: pointer;
	}
	button:disabled { opacity: 0.45; cursor: default; }
	button.ghost { background: transparent; color: var(--muted); border: 1px solid var(--border); }
	button.small { padding: 4px 10px; font-size: 0.82rem; }
	h2 { color: var(--accent); font-size: 1.02rem; margin: 28px 0 10px; }
	.banner {
		display: flex; align-items: flex-start; gap: 12px;
		border-radius: var(--radius); padding: 12px 16px; margin: 14px 0;
	}
	.banner.running { background: var(--accent-soft); border: 1px solid var(--accent); }
	.banner.done { background: var(--ok-soft); border: 1px solid var(--ok); }
	.banner.failed { background: var(--err-soft); border: 1px solid var(--err); }
	.banner .spin { margin-top: 3px; }
	.banner .status-dot { margin-top: 7px; }
	.banner-body { flex: 1; }
	.banner-body .meta { margin-left: 8px; }
	.progress {
		margin: 6px 0 0; color: var(--muted); font-size: 0.88rem;
		font-variant-numeric: tabular-nums;
	}
	.tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(118px, 1fr)); gap: 10px; }
	.tile { padding: 10px 12px; display: flex; flex-direction: column; text-decoration: none; }
	.tile:hover { border-color: var(--accent); }
	.tile .num { font-size: 1.35rem; font-weight: 700; font-variant-numeric: tabular-nums; }
	.tile .lbl { color: var(--muted); font-size: 0.76rem; }
	.jobs { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 10px; }
	.job-all { border-color: var(--accent); margin-bottom: 10px; }
	.job { display: flex; align-items: center; gap: 12px; padding: 12px 14px; }
	.job-info { flex: 1; display: flex; flex-direction: column; }
	.job-info .hint { color: var(--muted); font-size: 0.78rem; }
	.primer-body { white-space: pre-wrap; margin: 8px 0 2px; line-height: 1.6; }
	.trace { padding: 10px 14px; margin-bottom: 6px; }
	.trace summary { cursor: pointer; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
	.trace-q { flex: 1; min-width: 180px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
	.trace dl { margin: 10px 0 2px; }
	.trace dt { color: var(--muted); font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.05em; margin-top: 8px; }
	.trace dd { margin: 2px 0 0; font-size: 0.9rem; }
	.correction { display: flex; gap: 14px; padding: 12px 14px; margin-bottom: 8px; }
	.correction-body { flex: 1; }
	.correction .q { margin: 0 0 4px; color: var(--muted); font-size: 0.88rem; }
	.correction .t { margin: 0 0 4px; }
	.correction-actions { display: flex; flex-direction: column; gap: 6px; }
	table { width: 100%; border-collapse: collapse; margin-bottom: 20px; }
	th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--border); }
	th { color: var(--muted); font-size: 0.82rem; font-weight: 600; }
	.meta { color: var(--muted); font-size: 0.85rem; }
	.live-tag {
		font-size: 0.68rem; text-transform: uppercase; letter-spacing: 0.08em;
		border: 1px solid var(--border); border-radius: 999px; padding: 2px 8px; margin-left: 6px;
	}
	.badge {
		font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.04em;
		padding: 2px 8px; border-radius: 999px; font-weight: 700;
	}
	.badge.ok-b { background: var(--ok-soft); color: var(--ok); }
	.badge.warn-b { background: var(--warn-soft); color: var(--warn); }
	.badge.err { background: var(--err-soft); color: var(--err); }
	.warn { color: var(--err); }
	a { color: inherit; }
</style>
