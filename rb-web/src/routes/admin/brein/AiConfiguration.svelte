<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import type { SubmitFunction } from '@sveltejs/kit';
	import {
		AI_CREDENTIAL_MAX_LENGTH,
		AI_CREDENTIAL_MIN_LENGTH,
		authTypeLabel,
		authTypesFor,
		modelAliasLabel,
		providerLabel,
		type AiConfiguration,
		type AiDeviceLogin,
		type AiDeviceStatus,
		type AiPool,
		type AiProviderId,
		type AiProviderStatus,
		type AiResourceStatus
	} from '$lib/aiConfig';

	interface ManagedSetting {
		key: string;
		label: string;
		effective: string;
		default: string;
		overridden: boolean;
		updatedAt: string | null;
		updatedBy: string | null;
		options: string[] | null;
	}

	interface ActionFeedback {
		aiChanged?: string;
		aiError?: string;
		aiDeviceLogin?: AiDeviceLogin;
		aiDevicePoll?: { status: AiDeviceStatus; pollAfterMs?: number };
	}

	let {
		config,
		settings,
		form,
		deviceLoginPending = false
	}: {
		config: AiConfiguration | null;
		settings: ManagedSetting[];
		form: unknown;
		deviceLoginPending?: boolean;
	} = $props();

	const feedback = $derived((form ?? {}) as ActionFeedback);
	const extractModel = $derived(settings.find((setting) => setting.key === 'brein.extract.model') ?? null);
	const auditModel = $derived(settings.find((setting) => setting.key === 'brein.audit.model') ?? null);
	const deviceFlowVisible = $derived(
		deviceLoginPending
		|| feedback.aiDeviceLogin?.status === 'pending'
		|| feedback.aiDevicePoll?.status === 'pending'
	);

	const refresh: SubmitFunction = () => async ({ update }) => {
		await update();
		await invalidateAll();
	};

	function accountsFor(poolId: string) {
		return config?.accounts.filter((account) => account.poolId === poolId) ?? [];
	}

	function statusLabel(status: AiProviderStatus | AiResourceStatus | AiDeviceStatus): string {
		switch (status) {
			case 'unconfigured': return 'Niet geconfigureerd';
			case 'ready': return 'Gereed';
			case 'degraded': return 'Beperkt';
			case 'unavailable': return 'Niet bereikbaar';
			case 'unknown': return 'Onbekend';
			case 'cooldown': return 'Afkoeling';
			case 'quota_exhausted': return 'Quota verbruikt';
			case 'auth_invalid': return 'Auth ongeldig';
			case 'disabled': return 'Uitgeschakeld';
			case 'pending': return 'Wacht op aanmelding';
			case 'complete': return 'Aanmelding voltooid';
			case 'expired': return 'Aanmelding verlopen';
			case 'error': return 'Aanmelding mislukt';
		}
	}

	function statusTone(status: AiProviderStatus | AiResourceStatus | AiDeviceStatus): string {
		if (status === 'ready' || status === 'complete') return 'ok';
		if (status === 'unavailable' || status === 'auth_invalid' || status === 'error') return 'err';
		if (status === 'degraded' || status === 'cooldown' || status === 'quota_exhausted'
			|| status === 'expired') return 'warn';
		return '';
	}

	function settingMeta(setting: ManagedSetting): string {
		if (!setting.overridden) return `standaard (${modelAliasLabel(setting.default)})`;
		const who = setting.updatedBy ? ` door ${setting.updatedBy}` : '';
		return `beheerd${who} · standaard ${modelAliasLabel(setting.default)}`;
	}

	function testedAt(iso: string | null): string {
		if (!iso) return 'nog niet getest';
		const date = new Date(iso);
		return Number.isNaN(date.getTime())
			? 'testtijd onbekend'
			: `getest ${date.toLocaleString('nl-NL', { dateStyle: 'short', timeStyle: 'short' })}`;
	}

	function sourceLabel(pool: AiPool): string {
		return pool.source === 'environment' ? 'Omgevingsbootstrap' : 'Beheerd';
	}

	function credentialHint(provider: AiProviderId): string {
		return provider === 'claude-agent-sdk'
			? 'OAuth-token of API-key (8–32.768 tekens); wordt na verzenden nooit teruggetoond.'
			: 'Access-token: 8–32.768 tekens. Laat leeg bij ChatGPT-device-login.';
	}
</script>

<section class="ai-config" aria-labelledby="ai-config-title">
	<header class="ai-head">
		<div>
			<h2 id="ai-config-title">AI-configuratie</h2>
			<p>Gesloten modelroutes, providerstatus en quota-bewuste accountpools. Credentials zijn write-only.</p>
		</div>
		{#if config}<span class="generation">configuratie {config.generation}</span>{/if}
	</header>

	{#if feedback.aiChanged}<p class="ai-notice ok">{feedback.aiChanged}</p>{/if}
	{#if feedback.aiError}<p class="ai-notice err">{feedback.aiError}</p>{/if}

	{#if extractModel || auditModel}
		<div class="routes">
			{#each [extractModel, auditModel].filter((setting): setting is ManagedSetting => setting !== null) as setting (setting.key)}
				<form method="POST" action="?/setting" use:enhance={refresh} class="route-card">
					<div>
						<strong>{setting.key === 'brein.extract.model' ? 'Extractiemodel' : 'Auditmodel'}</strong>
						<span>{setting.key === 'brein.extract.model'
							? 'Interacties en mechanic-predicaten'
							: 'Onafhankelijke steekproef-audit'}</span>
					</div>
					<input type="hidden" name="key" value={setting.key} />
					<select name="value" aria-label={setting.label}>
						{#each setting.options ?? [] as alias (alias)}
							<option value={alias} selected={alias === setting.effective}>{modelAliasLabel(alias)}</option>
						{/each}
					</select>
					<button class="primary">Opslaan</button>
					<small>{settingMeta(setting)}</small>
				</form>
			{/each}
		</div>
	{/if}

	{#if !config}
		<p class="empty">De live AI-configuratie is niet bereikbaar. De modelinstellingen hierboven blijven beschikbaar.</p>
	{:else}
		<div class="summary-grid">
			<section class="panel" aria-labelledby="providers-title">
				<h3 id="providers-title">Providers</h3>
				<div class="provider-list">
					{#each config.providers as provider (provider.id)}
						<div class="provider-row">
							<div>
								<strong>{providerLabel(provider.id)}</strong>
								<span>{provider.availableAccounts} van {provider.configuredAccounts} accounts beschikbaar · {provider.inFlight} actief</span>
							</div>
							<span class="status {statusTone(provider.status)}">{statusLabel(provider.status)}</span>
						</div>
					{/each}
					{#if config.providers.length === 0}<p class="empty compact">Geen providerstatus ontvangen.</p>{/if}
				</div>
			</section>

			<section class="panel" aria-labelledby="models-title">
				<h3 id="models-title">Modelcatalogus</h3>
				<div class="table-wrap">
					<table>
						<thead><tr><th>Alias</th><th>Provider</th><th>Model</th><th>Kan</th></tr></thead>
						<tbody>
							{#each config.models as target (target.alias)}
								<tr>
									<td><strong>{modelAliasLabel(target.alias)}</strong></td>
									<td>{providerLabel(target.provider)}</td>
									<td><code>{target.model}</code></td>
									<td>{target.capabilities.length ? target.capabilities.join(', ') : '—'}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			</section>
		</div>

		<div class="pools-head">
			<div>
				<h3>Accountpools</h3>
				<p>Hogere prioriteit gaat voor; gewicht verdeelt calls tussen pools met dezelfde prioriteit.</p>
			</div>
			<details class="add-control">
				<summary>Pool toevoegen</summary>
				<form method="POST" action="?/aiPoolCreate" use:enhance={refresh} class="form-grid">
					<label>Provider
						<select name="provider" required>
							<option value="claude-agent-sdk">Claude Agent SDK</option>
							<option value="codex-sdk">Codex SDK</option>
						</select>
					</label>
					<label>Naam <input name="label" maxlength="80" required /></label>
					<label>Prioriteit <input name="priority" type="number" min="-100" max="100" value="0" required /></label>
					<label>Gewicht <input name="weight" type="number" min="1" max="100" value="1" required /></label>
					<label>Status
						<select name="enabled"><option value="true">Actief</option><option value="false">Uitgeschakeld</option></select>
					</label>
					<button class="primary">Toevoegen</button>
				</form>
			</details>
		</div>

		<div class="pool-list">
			{#each config.pools as pool (pool.id)}
				<article class="pool">
					<header class="pool-head">
						<div>
							<div class="titleline">
								<h4>{pool.label}</h4>
								<span class="source">{sourceLabel(pool)}</span>
							</div>
							<p>{providerLabel(pool.provider)} · prioriteit {pool.priority} · gewicht {pool.weight} · {pool.availableAccounts}/{pool.accountCount} beschikbaar</p>
						</div>
						<span class="status {statusTone(pool.status)}">{statusLabel(pool.status)}</span>
					</header>

					{#if pool.editable}
						<details class="pool-settings">
							<summary>Poolinstellingen</summary>
							<form method="POST" action="?/aiPoolUpdate" use:enhance={refresh} class="form-grid">
								<input type="hidden" name="id" value={pool.id} />
								<label>Naam <input name="label" maxlength="80" value={pool.label} required /></label>
								<label>Prioriteit <input name="priority" type="number" min="-100" max="100" value={pool.priority} required /></label>
								<label>Gewicht <input name="weight" type="number" min="1" max="100" value={pool.weight} required /></label>
								<label>Status
									<select name="enabled" value={String(pool.enabled)}>
										<option value="true">Actief</option><option value="false">Uitgeschakeld</option>
									</select>
								</label>
								<button class="primary">Opslaan</button>
							</form>
							<form
								method="POST"
								action="?/aiPoolDelete"
								use:enhance={refresh}
								onsubmit={(event) => { if (!confirm('Deze accountpool en zijn beheerde accounts verwijderen?')) event.preventDefault(); }}
							>
								<input type="hidden" name="id" value={pool.id} />
								<button class="danger">Pool verwijderen</button>
							</form>
						</details>
					{/if}

					<div class="accounts">
						{#each accountsFor(pool.id) as account (account.id)}
							<section class="account">
								<header>
									<div>
										<strong>{account.label}</strong>
										<span>{authTypeLabel(account.authType)} · {account.credentialConfigured ? 'auth ingesteld' : 'auth ontbreekt'} · {testedAt(account.lastTestedAt)}</span>
									</div>
									<span class="status {statusTone(account.status)}">{statusLabel(account.status)}</span>
								</header>

								{#if account.editable}
									<div class="account-actions">
										<form method="POST" action="?/aiAccountTest" use:enhance={refresh}>
											<input type="hidden" name="id" value={account.id} />
											<button class="secondary">Verbinding testen</button>
										</form>
										{#if account.authType === 'chatgpt-device'}
											<form method="POST" action="?/aiDeviceStart" use:enhance={refresh}>
												<input type="hidden" name="id" value={account.id} />
												<button class="secondary">Device-login starten</button>
											</form>
										{/if}
									</div>

									<details>
										<summary>Account beheren</summary>
										<form method="POST" action="?/aiAccountUpdate" use:enhance={refresh} class="form-grid compact-grid">
											<input type="hidden" name="id" value={account.id} />
											<label>Naam <input name="label" maxlength="80" value={account.label} required /></label>
											<label>Status
												<select name="enabled" value={String(account.enabled)}>
													<option value="true">Actief</option><option value="false">Uitgeschakeld</option>
												</select>
											</label>
											<button class="primary">Opslaan</button>
										</form>

										{#if account.authType !== 'chatgpt-device'}
											<form method="POST" action="?/aiCredentialReplace" use:enhance={refresh} class="credential-form">
												<input type="hidden" name="id" value={account.id} />
												<label>Credential vervangen
													<input
														name="credential"
														type="password"
														autocomplete="new-password"
														minlength={AI_CREDENTIAL_MIN_LENGTH}
														maxlength={AI_CREDENTIAL_MAX_LENGTH}
														required
													/>
												</label>
												<button class="secondary">Vervangen</button>
												<small>Write-only: de bestaande en nieuwe waarde worden nooit teruggetoond.</small>
											</form>
										{/if}

										<form
											method="POST"
											action="?/aiAccountDelete"
											use:enhance={refresh}
											onsubmit={(event) => { if (!confirm('Dit beheerde AI-account verwijderen?')) event.preventDefault(); }}
										>
											<input type="hidden" name="id" value={account.id} />
											<button class="danger">Account verwijderen</button>
										</form>
									</details>
								{/if}
							</section>
						{/each}

						{#if accountsFor(pool.id).length === 0}<p class="empty compact">Nog geen accounts in deze pool.</p>{/if}

						{#if pool.editable}
							<details class="add-account">
								<summary>Account toevoegen</summary>
								<form method="POST" action="?/aiAccountCreate" use:enhance={refresh} class="form-grid">
									<input type="hidden" name="poolId" value={pool.id} />
									<label>Naam <input name="label" maxlength="80" required /></label>
									<label>Authmethode
										<select name="authType" required>
											{#each authTypesFor(pool.provider) as method (method)}
												<option value={method}>{authTypeLabel(method)}</option>
											{/each}
										</select>
									</label>
									<label class="wide">Credential, indien van toepassing
										<input
											name="credential"
											type="password"
											autocomplete="new-password"
											minlength={AI_CREDENTIAL_MIN_LENGTH}
											maxlength={AI_CREDENTIAL_MAX_LENGTH}
										/>
										<small>{credentialHint(pool.provider)}</small>
									</label>
									<label>Status
										<select name="enabled"><option value="true">Actief</option><option value="false">Uitgeschakeld</option></select>
									</label>
									<button class="primary">Toevoegen</button>
								</form>
							</details>
						{/if}
					</div>
				</article>
			{/each}
			{#if config.pools.length === 0}<p class="empty">Nog geen bootstrap- of beheerde pools gevonden.</p>{/if}
		</div>

		{#if feedback.aiDeviceLogin}
			<section class="device-flow" aria-live="polite">
				<h3>Codex-device-login</h3>
				<p>Open de officiële OpenAI-aanmeldpagina en voer deze eenmalige code in:</p>
				<div class="device-code"><code>{feedback.aiDeviceLogin.userCode}</code></div>
				<a href={feedback.aiDeviceLogin.verificationUri} target="_blank" rel="noopener noreferrer">Open {feedback.aiDeviceLogin.verificationUri}</a>
				{#if feedback.aiDeviceLogin.expiresAt}<small>Geldig tot {new Date(feedback.aiDeviceLogin.expiresAt).toLocaleString('nl-NL')}.</small>{/if}
			</section>
		{/if}

		{#if feedback.aiDevicePoll}
			<p class="device-status status {statusTone(feedback.aiDevicePoll.status)}" aria-live="polite">
				{statusLabel(feedback.aiDevicePoll.status)}
			</p>
		{/if}
		{#if deviceFlowVisible}
			<div class="device-actions">
				<form method="POST" action="?/aiDevicePoll" use:enhance={refresh} class="device-poll">
					<button class="secondary">Aanmeldstatus controleren</button>
				</form>
				<form method="POST" action="?/aiDeviceCancel" use:enhance={refresh} class="device-poll">
					<button class="danger">Aanmelding annuleren</button>
				</form>
			</div>
		{/if}
	{/if}
</section>

<style>
	.ai-config {
		margin-top: 20px;
		padding: 16px;
		border: 1px solid var(--border-strong);
		border-radius: var(--radius-lg, 13px);
		background: var(--surface);
	}
	.ai-head, .pools-head, .pool-head, .provider-row, .account > header {
		display: flex;
		align-items: flex-start;
		justify-content: space-between;
		gap: 12px;
		flex-wrap: wrap;
	}
	h2, h3, h4 { margin: 0; }
	.ai-head h2 { font-size: 1.05rem; }
	.ai-head p, .pools-head p, .pool-head p {
		margin: 4px 0 0;
		font-size: 0.8rem;
		line-height: 1.45;
		color: var(--muted);
	}
	.generation, .source {
		padding: 3px 8px;
		border: 1px solid var(--border);
		border-radius: 999px;
		font-size: 0.68rem;
		color: var(--muted);
		white-space: nowrap;
	}
	.ai-notice {
		margin: 12px 0 0;
		padding: 9px 11px;
		border-radius: var(--radius-md, 8px);
		font-size: 0.82rem;
	}
	.ai-notice.ok { background: var(--ok-soft); color: var(--ok); }
	.ai-notice.err { background: var(--err-soft); color: var(--err); }
	.routes {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
		gap: 10px;
		margin-top: 14px;
	}
	.route-card {
		display: grid;
		grid-template-columns: minmax(120px, 1fr) auto auto;
		align-items: center;
		gap: 9px;
		padding: 11px;
		border: 1px solid var(--border);
		border-radius: var(--radius-md, 8px);
		background: var(--surface-deep);
	}
	.route-card div { display: flex; flex-direction: column; gap: 2px; }
	.route-card span, .route-card small { color: var(--muted); font-size: 0.72rem; }
	.route-card small { grid-column: 1 / -1; }
	.summary-grid {
		display: grid;
		grid-template-columns: minmax(240px, 0.8fr) minmax(360px, 1.2fr);
		gap: 12px;
		margin-top: 14px;
	}
	.panel, .pool {
		border: 1px solid var(--border);
		border-radius: var(--radius-md, 8px);
		background: var(--surface-deep);
	}
	.panel { padding: 12px; min-width: 0; }
	.panel h3 { margin-bottom: 9px; font-size: 0.88rem; }
	.provider-list { display: flex; flex-direction: column; gap: 8px; }
	.provider-row {
		align-items: center;
		padding-top: 8px;
		border-top: 1px solid var(--border);
	}
	.provider-row:first-child { padding-top: 0; border-top: 0; }
	.provider-row div, .account > header div { display: flex; min-width: 0; flex-direction: column; gap: 2px; }
	.provider-row span:not(.status), .account > header div span { color: var(--muted); font-size: 0.72rem; }
	.status {
		display: inline-flex;
		align-items: center;
		width: fit-content;
		padding: 3px 8px;
		border-radius: 999px;
		background: var(--surface);
		color: var(--muted);
		font-size: 0.68rem;
		font-weight: 650;
		white-space: nowrap;
	}
	.status.ok { background: var(--ok-soft); color: var(--ok); }
	.status.warn { background: var(--warn-soft); color: var(--warn); }
	.status.err { background: var(--err-soft); color: var(--err); }
	.table-wrap { overflow-x: auto; }
	table { width: 100%; border-collapse: collapse; font-size: 0.74rem; }
	th, td { padding: 6px 8px; border-bottom: 1px solid var(--border); text-align: left; vertical-align: top; }
	th { color: var(--muted); font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.04em; }
	code { overflow-wrap: anywhere; }
	.pools-head { align-items: flex-end; margin-top: 18px; }
	.pools-head h3 { font-size: 0.95rem; }
	details > summary {
		cursor: pointer;
		color: var(--muted);
		font-size: 0.76rem;
		font-weight: 650;
	}
	.add-control { text-align: right; }
	.add-control[open] { width: min(100%, 680px); }
	.add-control .form-grid { margin-top: 9px; text-align: left; }
	.pool-list { display: flex; flex-direction: column; gap: 12px; margin-top: 10px; }
	.pool { padding: 13px; }
	.titleline { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
	.pool-head h4 { font-size: 0.9rem; }
	.pool-settings { margin-top: 10px; }
	.pool-settings > form { margin-top: 9px; }
	.accounts { display: flex; flex-direction: column; gap: 8px; margin-top: 12px; }
	.account {
		padding: 10px;
		border: 1px solid var(--border);
		border-radius: var(--radius-md, 8px);
		background: var(--surface);
	}
	.account > header { align-items: center; }
	.account-actions { display: flex; gap: 7px; flex-wrap: wrap; margin-top: 8px; }
	.account details { margin-top: 9px; padding-top: 8px; border-top: 1px solid var(--border); }
	.account details > form { margin-top: 9px; }
	.add-account { padding-top: 2px; }
	.form-grid {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(130px, 1fr));
		align-items: end;
		gap: 8px;
		padding: 10px;
		border: 1px solid var(--border);
		border-radius: var(--radius-md, 8px);
		background: var(--surface-deep);
	}
	.compact-grid { padding: 0; border: 0; background: transparent; }
	.form-grid .wide { grid-column: span 2; }
	label { display: flex; flex-direction: column; gap: 4px; color: var(--muted); font-size: 0.7rem; font-weight: 600; }
	input, select {
		box-sizing: border-box;
		width: 100%;
		min-width: 0;
		padding: 7px 9px;
		border: 1px solid var(--border);
		border-radius: var(--radius-md, 8px);
		background: var(--surface);
		color: var(--text);
		font: inherit;
		font-size: 16px;
	}
	button {
		font: inherit;
		font-size: 0.76rem;
		font-weight: 650;
		padding: 7px 12px;
		border-radius: 999px;
		cursor: pointer;
	}
	button.primary { border: 1px solid transparent; background: var(--accent); color: var(--accent-ink); }
	button.secondary { border: 1px solid var(--border-strong); background: var(--surface); color: var(--text); }
	button.danger { border: 1px solid var(--err); background: transparent; color: var(--err); }
	button:hover { filter: brightness(0.96); }
	.credential-form {
		display: grid;
		grid-template-columns: minmax(180px, 1fr) auto;
		align-items: end;
		gap: 8px;
	}
	.credential-form small { grid-column: 1 / -1; color: var(--muted); font-size: 0.68rem; }
	label small { color: var(--muted); font-weight: 400; }
	.empty { margin: 14px 0 0; padding: 12px; color: var(--muted); font-size: 0.8rem; border: 1px dashed var(--border); border-radius: var(--radius-md, 8px); }
	.empty.compact { margin: 0; padding: 8px; }
	.device-flow {
		margin-top: 14px;
		padding: 12px;
		border: 1px solid var(--accent);
		border-radius: var(--radius-md, 8px);
		background: var(--surface-deep);
	}
	.device-flow h3 { font-size: 0.9rem; }
	.device-flow p { margin: 5px 0 9px; color: var(--muted); font-size: 0.8rem; }
	.device-code code { display: inline-block; padding: 7px 10px; border: 1px solid var(--border); border-radius: 6px; font-size: 1rem; letter-spacing: 0.08em; }
	.device-flow a { display: block; margin-top: 9px; color: var(--accent); overflow-wrap: anywhere; }
	.device-flow small { display: block; margin-top: 7px; color: var(--muted); }
	.device-status { margin-top: 10px; }
	.device-actions { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 8px; }
	.device-poll { margin: 0; }

	@media (max-width: 760px) {
		.summary-grid { grid-template-columns: 1fr; }
		.route-card { grid-template-columns: 1fr; }
		.route-card small { grid-column: auto; }
		.form-grid .wide { grid-column: auto; }
		.credential-form { grid-template-columns: 1fr; }
		.credential-form small { grid-column: auto; }
		.add-control, .add-control[open] { width: 100%; text-align: left; }
	}
</style>
