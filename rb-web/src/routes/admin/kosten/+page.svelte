<script lang="ts">
	import { enhance } from '$app/forms';
	import { goto } from '$app/navigation';

	let { data, form } = $props();

	interface Totals {
		calls: number;
		inputTokens: number;
		outputTokens: number;
		unpricedCalls: number;
		usd: number;
	}
	interface ModelRow extends Totals { model: string; }
	interface KindRow extends Totals { kind: string; model: string; }
	interface UserRow extends Totals { userId: number; email: string; }
	interface Tariff {
		id: number;
		model: string;
		inputUsdPerMTok: number;
		outputUsdPerMTok: number;
		effectiveFrom: string;
	}
	interface Costs {
		period: string;
		today: Totals;
		days7: Totals;
		days30: Totals;
		userCaused: Totals;
		platformCaused: Totals;
		perModel: ModelRow[];
		platformPerKind: KindRow[];
		topUsers: UserRow[];
		tariffs: Tariff[];
		embeddingsNote: string;
	}

	// SSR rendert de server-load; de poll hieronder legt er verse data
	// overheen. Bij een periode-wissel (nieuwe load-data) begint de poll
	// opnieuw en vervalt de oude live-stand.
	let liveCosts = $state<Costs | null>(null);
	let liveDown = $state<boolean | null>(null);
	let refreshedAt = $state<Date | null>(null);
	const costs = $derived(liveCosts ?? (data.costs as Costs | null));
	const apiDown = $derived(liveDown ?? data.apiDown);

	const period = $derived(data.period);
	const PERIOD_LABELS: Record<string, string> = {
		vandaag: 'Vandaag',
		'7d': 'Laatste 7 dagen',
		'30d': 'Laatste 30 dagen'
	};

	// Live via dezelfde poll-aanpak als de job-voortgang (#328): elke 5s de
	// eigen cookie-beveiligde GET; rb-api-uitval kleurt de statusregel en de
	// volgende poll herstelt vanzelf.
	$effect(() => {
		const p = period;
		let stop = false;
		liveCosts = null;
		liveDown = null;
		const tick = async () => {
			try {
				const r = await fetch(`/admin/kosten?period=${p}`);
				if (!r.ok) throw new Error(String(r.status));
				const fresh = (await r.json()) as Costs;
				if (!stop) {
					liveCosts = fresh;
					liveDown = false;
					refreshedAt = new Date();
				}
			} catch {
				if (!stop) liveDown = true;
			}
		};
		const t = setInterval(tick, 5000);
		return () => {
			stop = true;
			clearInterval(t);
		};
	});

	const nf = new Intl.NumberFormat('nl-NL');
	const fmtTokens = (n: number) => nf.format(n);
	// Schaduwbedragen: klein maar betekenisvol — 2 decimalen boven $1, anders 4.
	const fmtUsd = (n: number) =>
		`$${n.toLocaleString('nl-NL', {
			minimumFractionDigits: 2,
			maximumFractionDigits: n !== 0 && Math.abs(n) < 1 ? 4 : 2
		})}`;
	const fmtDate = (iso: string) => new Date(iso).toLocaleDateString('nl-NL');

	function setPeriod(p: string) {
		void goto(`/admin/kosten?period=${p}`, { keepFocus: true, noScroll: true });
	}
</script>

<svelte:head><title>Kosten — Poracle beheer</title></svelte:head>

<main>
	<h1>Kosten</h1>
	<p class="meta intro">
		AI-verbruik per gebruiker en per job-soort, live bijgewerkt. Alle bedragen
		zijn <strong>schaduwkosten</strong>: tokens &times; het API-tarief van dat
		moment. We betalen een abonnement — dit is inzicht, geen factuur.
	</p>

	<p class="livebar">
		<span class="dot" class:down={apiDown} aria-hidden="true"></span>
		{#if apiDown}
			rb-api niet bereikbaar — laatste bekende stand blijft staan.
		{:else if refreshedAt}
			Live — ververst {refreshedAt.toLocaleTimeString('nl-NL')}.
		{:else}
			Live — eerste verversing volgt binnen 5s.
		{/if}
	</p>

	{#if costs}
		<section class="totals">
			{#each [
				{ label: 'Vandaag', t: costs.today },
				{ label: '7 dagen', t: costs.days7 },
				{ label: '30 dagen', t: costs.days30 }
			] as blok (blok.label)}
				<div class="panel stat">
					<span class="meta">{blok.label}</span>
					<strong class="tnum">{fmtUsd(blok.t.usd)}</strong>
					<span class="meta tnum">{fmtTokens(blok.t.inputTokens)} in / {fmtTokens(blok.t.outputTokens)} uit</span>
					<span class="meta tnum">{blok.t.calls} {blok.t.calls === 1 ? 'call' : 'calls'}{blok.t.unpricedCalls > 0 ? ` · ${blok.t.unpricedCalls} zonder meting` : ''}</span>
				</div>
			{/each}
		</section>

		<div class="chips" role="group" aria-label="Meetperiode">
			{#each ['vandaag', '7d', '30d'] as p (p)}
				<button type="button" class="chip" class:active={period === p} onclick={() => setPeriod(p)}>
					{PERIOD_LABELS[p]}
				</button>
			{/each}
		</div>

		<section class="split">
			<div class="panel stat">
				<span class="meta">Gebruikers-veroorzaakt ({PERIOD_LABELS[period]})</span>
				<strong class="tnum">{fmtUsd(costs.userCaused.usd)}</strong>
				<span class="meta tnum">{costs.userCaused.calls} vragen · {fmtTokens(costs.userCaused.inputTokens)} in / {fmtTokens(costs.userCaused.outputTokens)} uit</span>
			</div>
			<div class="panel stat">
				<span class="meta">Platform-veroorzaakt ({PERIOD_LABELS[period]})</span>
				<strong class="tnum">{fmtUsd(costs.platformCaused.usd)}</strong>
				<span class="meta tnum">{costs.platformCaused.calls} runs · {fmtTokens(costs.platformCaused.inputTokens)} in / {fmtTokens(costs.platformCaused.outputTokens)} uit</span>
			</div>
		</section>

		<section class="panel">
			<h2>Top-gebruikers <span class="meta">({PERIOD_LABELS[period]})</span></h2>
			{#if costs.topUsers.length === 0}
				<p class="meta">Nog geen gemeterd gebruik in deze periode.</p>
			{:else}
				<div class="table-wrap">
					<table>
						<thead><tr><th>Account</th><th>Vragen</th><th>Tokens in / uit</th><th>Schaduwkosten</th></tr></thead>
						<tbody>
							{#each costs.topUsers as u (u.userId)}
								<tr>
									<td>{u.email}</td>
									<td class="tnum">{u.calls}</td>
									<td class="tnum">{fmtTokens(u.inputTokens)} / {fmtTokens(u.outputTokens)}</td>
									<td class="tnum">{fmtUsd(u.usd)}{u.unpricedCalls > 0 ? ` (+${u.unpricedCalls} zonder meting)` : ''}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
			<p class="meta small">
				Vragen van vóór de tokenmeting tellen als "zonder meting" — het bedrag is een ondergrens, geen schatting.
			</p>
		</section>

		<section class="panel">
			<h2>Per model <span class="meta">({PERIOD_LABELS[period]})</span></h2>
			{#if costs.perModel.length === 0}
				<p class="meta">Nog geen gemeterd gebruik in deze periode.</p>
			{:else}
				<div class="table-wrap">
					<table>
						<thead><tr><th>Model</th><th>Calls</th><th>Tokens in / uit</th><th>Schaduwkosten</th></tr></thead>
						<tbody>
							{#each costs.perModel as m (m.model)}
								<tr>
									<td>{m.model}</td>
									<td class="tnum">{m.calls}</td>
									<td class="tnum">{fmtTokens(m.inputTokens)} / {fmtTokens(m.outputTokens)}</td>
									<td class="tnum">{fmtUsd(m.usd)}{m.unpricedCalls > 0 ? ` (+${m.unpricedCalls} zonder meting)` : ''}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		</section>

		<section class="panel">
			<h2>Platform per job-soort <span class="meta">({PERIOD_LABELS[period]})</span></h2>
			{#if costs.platformPerKind.length === 0}
				<p class="meta">Nog geen platform-runs (mining, audit, primer) in deze periode.</p>
			{:else}
				<div class="table-wrap">
					<table>
						<thead><tr><th>Job-soort</th><th>Model</th><th>Runs</th><th>Tokens in / uit</th><th>Schaduwkosten</th></tr></thead>
						<tbody>
							{#each costs.platformPerKind as k (k.kind + k.model)}
								<tr>
									<td>{k.kind}</td>
									<td>{k.model}</td>
									<td class="tnum">{k.calls}</td>
									<td class="tnum">{fmtTokens(k.inputTokens)} / {fmtTokens(k.outputTokens)}</td>
									<td class="tnum">
										{#if k.unpricedCalls === k.calls}
											<span class="unknown">geen meting</span>
										{:else}
											{fmtUsd(k.usd)}{k.unpricedCalls > 0 ? ` (+${k.unpricedCalls} zonder meting)` : ''}
										{/if}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
			<p class="meta small">
				De interactie-audit geeft (nog) geen token-usage terug op het losse
				rb-ai-pad: die runs staan hier eerlijk als "geen meting", nooit als $0.
			</p>
		</section>

		<section class="panel embeddings">
			<h2>Embeddings</h2>
			<p class="meta">{costs.embeddingsNote}</p>
		</section>

		<section class="panel">
			<h2>Schaduwtarieven <span class="meta">(USD per miljoen tokens, append-only)</span></h2>
			<div class="table-wrap">
				<table>
					<thead><tr><th>Model</th><th>Input</th><th>Output</th><th>Geldig vanaf</th></tr></thead>
					<tbody>
						{#each costs.tariffs as t (t.id)}
							<tr>
								<td>{t.model}</td>
								<td class="tnum">${t.inputUsdPerMTok}</td>
								<td class="tnum">${t.outputUsdPerMTok}</td>
								<td class="tnum">{fmtDate(t.effectiveFrom)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			<form method="POST" action="?/tariff" use:enhance class="tariff-form">
				<label>Model <input name="model" placeholder="claude-sonnet-4-6" required /></label>
				<label>Input $/MTok <input name="input" inputmode="decimal" placeholder="3" required /></label>
				<label>Output $/MTok <input name="output" inputmode="decimal" placeholder="15" required /></label>
				<label>Vanaf <input name="from" type="date" /></label>
				<button type="submit">Tarief toevoegen</button>
			</form>
			{#if form?.tariffError}<p class="warn">{form.tariffError}</p>{/if}
			{#if form?.tariffSaved}<p class="okmsg">Tarief toegevoegd — nieuwe metingen boeken tegen de nieuwe rij.</p>{/if}
			<p class="meta small">
				Prijzen wijzigen? Voeg een nieuwe rij toe met een ingangsdatum — eerder
				geboekte metingen houden hun eigen tariefversie en blijven exact
				reproduceerbaar.
			</p>
		</section>
	{:else}
		<section class="panel">
			<p class="warn">rb-api is niet bereikbaar — het kostenoverzicht kan nu niet geladen worden.</p>
		</section>
	{/if}
</main>

<style>
	main { max-width: 980px; margin: 0 auto; padding: 24px 20px; }
	.intro { max-width: 64ch; }
	.livebar {
		display: inline-flex; align-items: center; gap: 8px;
		font-size: 0.85rem; color: var(--muted); margin: 4px 0 14px;
	}
	.dot { width: 8px; height: 8px; border-radius: 50%; background: var(--ok, #3fa76e); }
	.dot.down { background: var(--warn, #c94f4f); }
	.totals, .split {
		display: grid; gap: 12px; margin-bottom: 14px;
		grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
	}
	.stat { display: flex; flex-direction: column; gap: 4px; padding: 14px 16px; }
	.stat strong { font-size: 1.4rem; }
	.chips { display: flex; flex-wrap: wrap; gap: 8px; margin: 4px 0 14px; }
	.chip {
		background: var(--surface); color: var(--muted); border: 1px solid var(--border);
		border-radius: 999px; padding: 6px 14px; font-size: 0.85rem; cursor: pointer;
	}
	.chip.active { border-color: var(--accent); color: var(--accent); font-weight: 600; }
	section.panel { padding: 16px; margin-bottom: 14px; }
	h2 { margin: 0 0 10px; font-size: 1.05rem; }
	.meta { color: var(--muted); }
	.small { font-size: 0.85rem; }
	.unknown { color: var(--muted); font-style: italic; }
	.warn { color: var(--warn, #c94f4f); }
	.okmsg { color: var(--ok, #3fa76e); font-size: 0.9rem; }
	.tariff-form {
		display: flex; flex-wrap: wrap; gap: 10px; align-items: end; margin-top: 12px;
	}
	.tariff-form label {
		display: flex; flex-direction: column; gap: 4px; font-size: 0.85rem;
		color: var(--muted);
	}
	.tariff-form input {
		background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;
		font-size: 1rem; min-width: 0; width: 160px; max-width: 100%;
	}
	.tariff-form button {
		background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 9px 16px; font-weight: 600; cursor: pointer;
	}
	@media (max-width: 480px) {
		.tariff-form label { width: 100%; }
		.tariff-form input { width: 100%; }
	}
</style>
