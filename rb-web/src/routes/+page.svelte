<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';

	let { data } = $props();

	// Web-push (#28): meldingen bij belangrijke wijzigingen (bans/errata).
	let pushState = $state<'unavailable' | 'off' | 'on' | 'busy'>('unavailable');
	$effect(() => {
		(async () => {
			if (!('serviceWorker' in navigator) || !('PushManager' in window)) return;
			const vapid = await fetch('/push');
			if (!vapid.ok) return; // server heeft geen VAPID-keys
			const reg = await navigator.serviceWorker.ready;
			pushState = (await reg.pushManager.getSubscription()) ? 'on' : 'off';
		})().catch(() => {});
	});

	function b64ToBytes(b64: string): Uint8Array<ArrayBuffer> {
		const pad = '='.repeat((4 - (b64.length % 4)) % 4);
		const raw = atob((b64 + pad).replace(/-/g, '+').replace(/_/g, '/'));
		const bytes = new Uint8Array(new ArrayBuffer(raw.length));
		for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
		return bytes;
	}

	async function togglePush() {
		const prev = pushState;
		pushState = 'busy';
		try {
			const reg = await navigator.serviceWorker.ready;
			if (prev === 'on') {
				const sub = await reg.pushManager.getSubscription();
				if (sub) {
					await fetch('/push', {
						method: 'POST',
						headers: { 'content-type': 'application/json' },
						body: JSON.stringify({ action: 'unsubscribe', endpoint: sub.endpoint })
					});
					await sub.unsubscribe();
				}
				pushState = 'off';
				return;
			}
			if ((await Notification.requestPermission()) !== 'granted') { pushState = 'off'; return; }
			const { publicKey } = await (await fetch('/push')).json();
			const sub = await reg.pushManager.subscribe({
				userVisibleOnly: true,
				applicationServerKey: b64ToBytes(publicKey)
			});
			const raw = sub.toJSON();
			await fetch('/push', {
				method: 'POST',
				headers: { 'content-type': 'application/json' },
				body: JSON.stringify({
					action: 'subscribe',
					endpoint: sub.endpoint,
					p256dh: raw.keys?.p256dh,
					auth: raw.keys?.auth
				})
			});
			pushState = 'on';
		} catch {
			pushState = prev === 'busy' ? 'off' : prev;
		}
	}

	let sevFilter = $state<string | null>(null);
	let typeFilter = $state<string | null>(null);
	let srcFilter = $state<string | null>(null);

	const changes = $derived(
		data.changes.filter(
			(c) =>
				(!sevFilter || c.severity === sevFilter) &&
				(!typeFilter || c.changeType === typeFilter) &&
				(!srcFilter || c.sourceId === srcFilter)
		)
	);
	const severities = $derived([...new Set(data.changes.map((c) => c.severity))]);
	const types = $derived([...new Set(data.changes.map((c) => c.changeType))]);
	const sources = $derived(
		[...new Map(data.changes.map((c) => [c.sourceId, c.sourceName])).entries()]
	);

	// Diff-tekst → regels met kleur: '+'-blok groen, '-'-blok rood.
	function diffLines(diff: string): { text: string; kind: 'add' | 'del' | 'head' }[] {
		const out: { text: string; kind: 'add' | 'del' | 'head' }[] = [];
		let mode: 'add' | 'del' = 'add';
		for (const line of diff.split('\n')) {
			if (line.startsWith('+')) { mode = 'add'; out.push({ text: line, kind: 'head' }); }
			else if (line.startsWith('-')) { mode = 'del'; out.push({ text: line, kind: 'head' }); }
			else if (line.trim()) out.push({ text: line.trim(), kind: mode });
		}
		return out;
	}

	function fmtWhen(iso: string): string {
		const d = new Date(iso);
		const days = Math.floor((Date.now() - d.getTime()) / 86_400_000);
		const time = d.toLocaleString('nl-NL', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
		return days === 0 ? `vandaag ${d.toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' })}` : time;
	}
</script>

<svelte:head>
	<title>Riftbound Rules Companion</title>
</svelte:head>

<main>
	<div class="hero">
		<div>
			<h1>Riftbound <span>Rules Companion</span></h1>
			<p class="subtitle">Wat is er veranderd in de regels, bans en errata — automatisch bijgehouden.</p>
		</div>
		{#if pushState !== 'unavailable'}
			<button class="push-toggle" class:on={pushState === 'on'} disabled={pushState === 'busy'} onclick={togglePush}>
				{pushState === 'on' ? 'Meldingen aan' : pushState === 'busy' ? 'Bezig…' : 'Meldingen bij belangrijke wijzigingen'}
			</button>
		{/if}
	</div>

	{#if data.apiDown}
		<p class="warn">rb-api is niet bereikbaar.</p>
	{:else if data.changes.length === 0}
		<p class="empty">Nog geen wijzigingen geregistreerd.</p>
	{:else}
		<!-- Filters -->
		<div class="filters">
			<div class="fgroup">
				{#each severities as s (s)}
					<button class="chip sev-{s}" class:on={sevFilter === s}
						onclick={() => (sevFilter = sevFilter === s ? null : s)}>{s}</button>
				{/each}
			</div>
			<div class="fgroup">
				{#each types as t (t)}
					<button class="chip" class:on={typeFilter === t}
						onclick={() => (typeFilter = typeFilter === t ? null : t)}>{t}</button>
				{/each}
			</div>
			{#if sources.length > 1}
				<div class="fgroup">
					{#each sources as [id, name] (id)}
						<button class="chip" class:on={srcFilter === id}
							onclick={() => (srcFilter = srcFilter === id ? null : id)}>{name}</button>
					{/each}
				</div>
			{/if}
			{#if sevFilter || typeFilter || srcFilter}
				<button class="chip clear" onclick={() => { sevFilter = null; typeFilter = null; srcFilter = null; }}>Wis filters</button>
			{/if}
		</div>
		<p class="meta count">{changes.length} van {data.changes.length} wijzigingen</p>

		{#each changes as c (c.id)}
			<article class="card">
				<header>
					<span class="badge {c.severity}">{c.severity}</span>
					<strong>{c.changeType}</strong>
					<a class="src" href={c.sourceUrl} target="_blank" rel="noopener"
						title="trust-tier {c.trustTier}">{c.sourceName} ↗</a>
					<span class="meta when">{fmtWhen(c.detectedAt)}</span>
					{#if data.isAdmin}
						<form method="POST" action="?/delete" use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }}>
							<input type="hidden" name="id" value={c.id} />
							<button class="del" title="Verwijder uit feed">Verwijder</button>
						</form>
					{/if}
				</header>
				{#if c.summary}<p>{c.summary}</p>{/if}
				{#if c.meaning}<p class="meaning">{c.meaning}</p>{/if}
				{#if c.diff}
					<details>
						<summary>Wat is er precies gewijzigd? (voor/na)</summary>
						<div class="diff">
							{#each diffLines(c.diff) as l, i (i)}
								<div class="dline {l.kind}">{l.text}</div>
							{/each}
						</div>
					</details>
				{/if}
			</article>
		{/each}
	{/if}
</main>

<style>
	main { max-width: 860px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: #d98a4e; }
	.hero { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; flex-wrap: wrap; }
	.push-toggle {
		background: transparent; color: #9fb0cc; border: 1px solid #243551;
		border-radius: 999px; padding: 7px 14px; font-size: 0.82rem; cursor: pointer;
		margin-top: 14px; white-space: nowrap;
	}
	.push-toggle.on { color: #7fd1a8; border-color: #4fbf8b; }
	.push-toggle:disabled { opacity: 0.5; }
	.subtitle, .meta, .empty { color: #9fb0cc; }
	.filters { display: flex; flex-wrap: wrap; gap: 6px 14px; margin: 14px 0 4px; }
	.fgroup { display: flex; flex-wrap: wrap; gap: 6px; }
	.chip {
		background: #16233b; color: #9fb0cc; border: 1px solid #243551;
		border-radius: 999px; padding: 3px 12px; font-size: 0.8rem; cursor: pointer;
	}
	.chip.on { background: #d98a4e; color: #1a1206; border-color: #d98a4e; font-weight: 700; }
	.chip.clear { border-style: dashed; }
	.count { font-size: 0.8rem; margin: 4px 0 12px; }
	.card {
		background: #16233b;
		border: 1px solid #243551;
		border-radius: 12px;
		padding: 14px 16px;
		margin-bottom: 12px;
	}
	.card header { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
	.src { color: #9fb0cc; font-size: 0.85rem; text-decoration: none; border-bottom: 1px dotted #9fb0cc66; }
	.when { margin-left: auto; font-size: 0.85rem; }
	.del {
		background: none; border: 1px solid #243551; border-radius: 6px;
		color: #9fb0cc; cursor: pointer; font-size: 0.75rem; padding: 2px 8px; opacity: 0.7;
	}
	.del:hover { opacity: 1; border-color: #e5484d; color: #ff8b8e; }
	.badge {
		font-size: 0.72rem;
		text-transform: uppercase;
		padding: 2px 8px;
		border-radius: 999px;
		font-weight: 700;
		background: #24355133;
	}
	.badge.high { background: #e5484d2e; color: #ff8b8e; }
	.badge.medium { background: #e0a32e2e; color: #f3c469; }
	.chip.sev-high { color: #ff8b8e; }
	.chip.sev-medium { color: #f3c469; }
	.warn { color: #ff8b8e; }
	.meaning { color: #d98a4e; }
	details { margin-top: 8px; }
	summary { color: #9fb0cc; font-size: 0.85rem; cursor: pointer; }
	.diff {
		background: #0b1322; border: 1px solid #243551; border-radius: 8px;
		padding: 10px 12px; margin-top: 8px; font-size: 0.85rem;
		max-height: 320px; overflow: auto;
	}
	.dline { padding: 1px 6px; border-radius: 4px; }
	.dline.head { color: #9fb0cc; font-weight: 700; margin-top: 4px; }
	.dline.add { background: #58c08a14; color: #a5e3c3; }
	.dline.del { background: #e5484d14; color: #ffb3b5; text-decoration: line-through; }
</style>
