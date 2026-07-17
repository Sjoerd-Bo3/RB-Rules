<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import ChangeCard from '$lib/ChangeCard.svelte';

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
				// Server heeft de registratie niet — browser-abonnement terugdraaien.
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

	// Aankomende set (#52): releasedatum → "31 juli 2026 (over 20 dagen)".
	function fmtRelease(publishedOn: string): string {
		const d = new Date(publishedOn);
		const days = Math.ceil((d.getTime() - Date.now()) / 86_400_000);
		const date = d.toLocaleDateString('nl-NL', { day: 'numeric', month: 'long', year: 'numeric' });
		return days === 1 ? `${date} (morgen)` : `${date} (over ${days} dagen)`;
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

	<!-- Aankomende-set-signaal (#52): zichtbaar zodra de kaart-sync een
	     releasedatum in de toekomst kent; kaarten zijn tot die dag niet legaal. -->
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
			{#if data.isAdmin}
				<ChangeCard change={c}>
					{#snippet actions()}
						<form method="POST" action="?/delete" use:enhance={() => async ({ update }) => { await update(); await invalidateAll(); }}>
							<input type="hidden" name="id" value={c.id} />
							<!-- Delete-cascade (#206, finding 9): een primaire verwijderen
							     neemt haar bevestigingen mee — het is hetzelfde event. -->
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
	main { max-width: 860px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: var(--accent); }
	.hero { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; flex-wrap: wrap; }
	.push-toggle {
		background: transparent; color: var(--muted); border: 1px solid var(--border);
		border-radius: 999px; padding: 7px 14px; font-size: 0.82rem; cursor: pointer;
		margin-top: 14px; white-space: nowrap;
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
	.filters { display: flex; flex-wrap: wrap; gap: 6px 14px; margin: 14px 0 4px; }
	.fgroup { display: flex; flex-wrap: wrap; gap: 6px; }
	.chip {
		background: var(--surface); color: var(--muted); border: 1px solid var(--border);
		border-radius: 999px; padding: 3px 12px; font-size: 0.8rem; cursor: pointer;
	}
	.chip.on { background: var(--accent); color: var(--accent-ink); border-color: var(--accent); font-weight: 700; }
	.chip.clear { border-style: dashed; }
	.chip.sev-high { color: var(--err); }
	.chip.sev-medium { color: var(--warn); }
	.count { font-size: 0.8rem; margin: 4px 0 12px; }
	.warn { color: var(--err); }
	/* Delete-knop (#206, admin-actieslot): compact, rustig totdat je hem
	   nodig hebt — pas bij hover valt hij op als destructieve actie. */
	.del {
		background: none; border: 1px solid var(--border); border-radius: 6px;
		color: var(--muted); cursor: pointer; font-size: 0.75rem; padding: 2px 8px; opacity: 0.7;
	}
	.del:hover { opacity: 1; border-color: var(--err); color: var(--err); }
</style>
