<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import ChangeCard from '$lib/ChangeCard.svelte';
	import { useShell } from '$lib/shell.svelte';

	let { data } = $props();
	const shell = useShell();

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
			const res = await fetch('/push', {
				method: 'POST',
				headers: { 'content-type': 'application/json' },
				body: JSON.stringify({
					action: 'subscribe',
					endpoint: sub.endpoint,
					p256dh: raw.keys?.p256dh,
					auth: raw.keys?.auth
				})
			});
			if (!res.ok) {
				await sub.unsubscribe().catch(() => {});
				pushState = 'off';
				return;
			}
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
	const activeCount = $derived(
		(sevFilter ? 1 : 0) + (typeFilter ? 1 : 0) + (srcFilter ? 1 : 0)
	);
	const srcName = $derived((id: string) => sources.find(([sid]) => sid === id)?.[1] ?? id);

	function clearFilters() {
		sevFilter = null;
		typeFilter = null;
		srcFilter = null;
	}

	// Filters wonen in de rechterrail (desktop) / bottom-sheet (mobiel) via de
	// shell — nooit een horizontaal scrollende chip-rij.
	$effect(() => {
		shell.rail = { snippet: filters, kind: 'filters', count: activeCount, title: 'Filters' };
		return () => (shell.rail = null);
	});

	// Aankomende set (#52): releasedatum → "31 juli 2026 (over 20 dagen)".
	function fmtRelease(publishedOn: string): string {
		const d = new Date(publishedOn);
		const days = Math.ceil((d.getTime() - Date.now()) / 86_400_000);
		const date = d.toLocaleDateString('nl-NL', { day: 'numeric', month: 'long', year: 'numeric' });
		return days === 1 ? `${date} (morgen)` : `${date} (over ${days} dagen)`;
	}
</script>

<svelte:head>
	<title>Wijzigingen — Poracle</title>
</svelte:head>

{#snippet chipGroup(label: string, options: string[], current: string | null, set: (v: string | null) => void)}
	<div class="fgroup">
		<p class="fglabel">{label}</p>
		<div class="chips">
			{#each options as o (o)}
				<button
					type="button"
					class="chip"
					class:on={current === o}
					onclick={() => set(current === o ? null : o)}>{o}</button
				>
			{/each}
		</div>
	</div>
{/snippet}

{#snippet filters()}
	{@render chipGroup('Severity', severities, sevFilter, (v) => (sevFilter = v))}
	{@render chipGroup('Type', types, typeFilter, (v) => (typeFilter = v))}
	{#if sources.length > 1}
		<div class="fgroup">
			<p class="fglabel">Bron</p>
			<div class="chips">
				{#each sources as [id, name] (id)}
					<button
						type="button"
						class="chip"
						class:on={srcFilter === id}
						onclick={() => (srcFilter = srcFilter === id ? null : id)}>{name}</button
					>
				{/each}
			</div>
		</div>
	{/if}
	<div class="filter-actions">
		<button type="button" class="link-btn" onclick={clearFilters} disabled={activeCount === 0}
			>Reset</button
		>
		<button type="button" class="apply" onclick={() => (shell.sheetOpen = false)}
			>Toon {changes.length}</button
		>
	</div>
{/snippet}

<main>
	<div class="head">
		<div>
			<h1>Wijzigingen</h1>
			<p class="subtitle">Bans, errata, regelupdates en set-releases — automatisch bijgehouden.</p>
		</div>
		{#if pushState !== 'unavailable'}
			<button class="push-toggle" class:on={pushState === 'on'} disabled={pushState === 'busy'} onclick={togglePush}>
				{pushState === 'on' ? 'Meldingen aan' : pushState === 'busy' ? 'Bezig…' : 'Meldingen bij belangrijke wijzigingen'}
			</button>
		{/if}
	</div>

	{#each data.upcoming as s (s.setId)}
		<aside class="upcoming">
			<span class="badge up-badge">aankomende set</span>
			<strong>{s.name}{s.name !== s.setId ? ` (${s.setId})` : ''}</strong>
			<span class="up-when">
				release {fmtRelease(s.publishedOn)}{s.cardCount ? ` · ${s.cardCount} kaarten` : ''}
				— tot de release niet legaal in constructed
			</span>
		</aside>
	{/each}

	{#if data.apiDown}
		<p class="warn">rb-api is niet bereikbaar.</p>
	{:else if data.changes.length === 0}
		<p class="empty">Nog geen wijzigingen geregistreerd.</p>
	{:else}
		<!-- Actieve filters als verwijderbare chips (altijd zichtbaar). -->
		{#if activeCount > 0}
			<div class="active-chips">
				{#if sevFilter}
					<button type="button" class="active-chip" onclick={() => (sevFilter = null)}
						>severity: {sevFilter} ✕</button
					>
				{/if}
				{#if typeFilter}
					<button type="button" class="active-chip" onclick={() => (typeFilter = null)}
						>type: {typeFilter} ✕</button
					>
				{/if}
				{#if srcFilter}
					<button type="button" class="active-chip" onclick={() => (srcFilter = null)}
						>bron: {srcName(srcFilter)} ✕</button
					>
				{/if}
				<button type="button" class="active-chip clear" onclick={clearFilters}>Wis alles</button>
			</div>
		{/if}
		<p class="meta count tnum">{changes.length} van {data.changes.length} wijzigingen</p>

		{#each changes as c (c.id)}
			{#if data.isAdmin}
				<ChangeCard change={c}>
					{#snippet actions()}
						<form method="POST" action="?/delete" use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }}>
							<input type="hidden" name="id" value={c.id} />
							<button class="del" title="Verwijder uit feed"
								onclick={(e) => {
									if (c.confirmedBy.length > 0 && !confirm(
										`Dit verwijdert ook ${c.confirmedBy.length} bevestiging(en) uit andere bronnen (zelfde event). Doorgaan?`))
										e.preventDefault();
								}}>Verwijder</button>
						</form>
					{/snippet}
				</ChangeCard>
			{:else}
				<ChangeCard change={c} />
			{/if}
		{/each}
	{/if}
</main>

<style>
	main { max-width: 820px; margin: 0 auto; padding: 24px 20px; }
	.head { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; flex-wrap: wrap; }
	.push-toggle {
		background: transparent; color: var(--muted); border: 1px solid var(--border);
		border-radius: 999px; padding: 7px 14px; font-size: 0.82rem; cursor: pointer;
		margin-top: 6px; white-space: nowrap;
	}
	.push-toggle.on { color: var(--ok); border-color: var(--ok); }
	.push-toggle:disabled { opacity: 0.5; }
	.subtitle, .meta, .empty { color: var(--muted); }
	.upcoming {
		display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap;
		background: var(--accent-soft); border: 1px solid var(--accent);
		border-radius: var(--radius); padding: 10px 14px; margin: 14px 0 4px;
	}
	.up-badge { background: var(--accent); color: var(--accent-ink); }
	.up-when { color: var(--muted); font-size: 0.88rem; }
	.active-chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 14px 0 4px; }
	.active-chip {
		background: var(--surface-deep); color: var(--text); border: 1px solid var(--border);
		border-radius: 999px; padding: 4px 12px; font-size: 0.8rem; cursor: pointer;
	}
	.active-chip:hover { border-color: var(--border-strong); }
	.active-chip.clear { color: var(--muted); border-style: dashed; }
	.count { font-size: 0.8rem; margin: 4px 0 12px; }
	.warn { color: var(--err); }
	.del {
		background: none; border: 1px solid var(--border); border-radius: 6px;
		color: var(--muted); cursor: pointer; font-size: 0.75rem; padding: 2px 8px; opacity: 0.7;
	}
	.del:hover { opacity: 1; border-color: var(--err); color: var(--err); }

	/* Filter-inhoud (gedeeld door rail + bottom-sheet) */
	.fgroup { margin-bottom: 14px; }
	.fglabel {
		margin: 0 0 6px; font-size: 0.72rem; font-weight: 700; text-transform: uppercase;
		letter-spacing: 0.05em; color: var(--muted);
	}
	.chips { display: flex; flex-wrap: wrap; gap: 6px; }
	.chip {
		background: var(--surface); color: var(--muted); border: 1px solid var(--border);
		border-radius: 999px; padding: 5px 12px; font-size: 0.8rem; cursor: pointer;
	}
	.chip:hover { border-color: var(--border-strong); color: var(--text); }
	.chip.on { background: var(--accent); color: var(--accent-ink); border-color: var(--accent); font-weight: 700; }
	.filter-actions { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-top: 6px; }
	.link-btn {
		background: none; border: 0; color: var(--muted); cursor: pointer; font-size: 0.85rem;
		padding: 6px 4px;
	}
	.link-btn:disabled { opacity: 0.4; cursor: default; }
	.apply {
		background: var(--accent); color: var(--accent-ink); border: 0; border-radius: 8px;
		padding: 9px 16px; font-weight: 700; cursor: pointer; font-variant-numeric: tabular-nums;
	}
</style>
