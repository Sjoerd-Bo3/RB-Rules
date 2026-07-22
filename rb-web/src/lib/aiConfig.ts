export const AI_PROVIDER_IDS = ['claude-agent-sdk', 'codex-sdk'] as const;
export type AiProviderId = (typeof AI_PROVIDER_IDS)[number];

export const AI_AUTH_TYPES = ['oauth-token', 'api-key', 'access-token', 'chatgpt-device'] as const;
export type AiAuthType = (typeof AI_AUTH_TYPES)[number];

export const AI_RESOURCE_STATUSES = [
	'unknown',
	'ready',
	'cooldown',
	'quota_exhausted',
	'auth_invalid',
	'disabled'
] as const;
export type AiResourceStatus = (typeof AI_RESOURCE_STATUSES)[number];

export const AI_PROVIDER_STATUSES = ['unconfigured', 'ready', 'degraded', 'unavailable'] as const;
export type AiProviderStatus = (typeof AI_PROVIDER_STATUSES)[number];

export const AI_DEVICE_STATUSES = ['pending', 'complete', 'expired', 'error'] as const;
export type AiDeviceStatus = (typeof AI_DEVICE_STATUSES)[number];

export const AI_CREDENTIAL_MIN_LENGTH = 8;
export const AI_CREDENTIAL_MAX_LENGTH = 32_768;

export function validAiCredentialLength(value: string, optional = false): boolean {
	const length = value.trim().length;
	return optional && length === 0
		? true
		: length >= AI_CREDENTIAL_MIN_LENGTH && length <= AI_CREDENTIAL_MAX_LENGTH;
}

export interface AiModelTarget {
	alias: string;
	provider: AiProviderId;
	model: string;
	capabilities: string[];
}

export interface AiProviderHealth {
	id: AiProviderId;
	configured: boolean;
	configuredAccounts: number;
	availableAccounts: number;
	inFlight: number;
	status: AiProviderStatus;
}

export interface AiPool {
	id: string;
	provider: AiProviderId;
	label: string;
	enabled: boolean;
	priority: number;
	weight: number;
	source: 'managed' | 'environment';
	editable: boolean;
	accountCount: number;
	availableAccounts: number;
	status: AiResourceStatus;
}

export interface AiAccount {
	id: string;
	poolId: string;
	label: string;
	enabled: boolean;
	authType: AiAuthType;
	status: AiResourceStatus;
	lastTestedAt: string | null;
	credentialConfigured: boolean;
	editable: boolean;
}

export interface AiConfiguration {
	generation: number;
	models: AiModelTarget[];
	providers: AiProviderHealth[];
	pools: AiPool[];
	accounts: AiAccount[];
}

export interface AiDeviceLogin {
	accountId: string;
	verificationUri: string;
	userCode: string;
	expiresAt: string | null;
	intervalSeconds: number;
	status: AiDeviceStatus;
}

type JsonObject = Record<string, unknown>;

function object(value: unknown): JsonObject | null {
	return typeof value === 'object' && value !== null && !Array.isArray(value)
		? (value as JsonObject)
		: null;
}

function text(value: unknown, max = 256): string | null {
	if (typeof value !== 'string') return null;
	const trimmed = value.trim();
	return trimmed && trimmed.length <= max ? trimmed : null;
}

function integer(value: unknown, min = 0, max = Number.MAX_SAFE_INTEGER): number | null {
	return typeof value === 'number' && Number.isSafeInteger(value) && value >= min && value <= max
		? value
		: null;
}

function member<T extends readonly string[]>(value: unknown, values: T): T[number] | null {
	return typeof value === 'string' && (values as readonly string[]).includes(value)
		? (value as T[number])
		: null;
}

function list(value: unknown): unknown[] {
	return Array.isArray(value) ? value : [];
}

/**
 * Allowlist projection at the server-to-browser boundary. Provider control data
 * is intentionally rebuilt field by field: an accidentally added upstream
 * credential, token, auth store or diagnostic body can never enter page data.
 */
export function sanitizeAiConfiguration(value: unknown): AiConfiguration | null {
	const root = object(value);
	if (!root) return null;

	const models = list(root.models).flatMap((candidate): AiModelTarget[] => {
		const row = object(candidate);
		if (!row) return [];
		const alias = text(row.alias, 64);
		const provider = member(row.provider, AI_PROVIDER_IDS);
		const model = text(row.model, 128);
		if (!alias || !provider || !model) return [];
		const capabilities = list(row.capabilities)
			.map((entry) => text(entry, 64))
			.filter((entry): entry is string => entry !== null);
		return [{ alias, provider, model, capabilities }];
	});

	const providers = list(root.providers).flatMap((candidate): AiProviderHealth[] => {
		const row = object(candidate);
		if (!row) return [];
		const id = member(row.id, AI_PROVIDER_IDS);
		const configuredAccounts = integer(row.configuredAccounts);
		const availableAccounts = integer(row.availableAccounts);
		const inFlight = integer(row.inFlight);
		const status = member(row.status, AI_PROVIDER_STATUSES);
		if (!id || configuredAccounts === null || availableAccounts === null || inFlight === null || !status)
			return [];
		return [{
			id,
			configured: row.configured === true,
			configuredAccounts,
			availableAccounts,
			inFlight,
			status
		}];
	});

	const pools = list(root.pools).flatMap((candidate): AiPool[] => {
		const row = object(candidate);
		if (!row) return [];
		const id = text(row.id, 128);
		const provider = member(row.provider, AI_PROVIDER_IDS);
		const label = text(row.label, 80);
		const priority = integer(row.priority, -100, 100);
		const weight = integer(row.weight, 1, 100);
		const source = member(row.source, ['managed', 'environment'] as const);
		const accountCount = integer(row.accountCount);
		const availableAccounts = integer(row.availableAccounts);
		const status = member(row.status, AI_RESOURCE_STATUSES);
		if (!id || !provider || !label || priority === null || weight === null || !source
			|| accountCount === null || availableAccounts === null || !status) return [];
		return [{
			id,
			provider,
			label,
			enabled: row.enabled === true,
			priority,
			weight,
			source,
			editable: row.editable === true,
			accountCount,
			availableAccounts,
			status
		}];
	});

	const poolIds = new Set(pools.map((pool) => pool.id));
	const accounts = list(root.accounts).flatMap((candidate): AiAccount[] => {
		const row = object(candidate);
		if (!row) return [];
		const id = text(row.id, 128);
		const poolId = text(row.poolId, 128);
		const label = text(row.label, 80);
		const authType = member(row.authType, AI_AUTH_TYPES);
		const status = member(row.status, AI_RESOURCE_STATUSES);
		const lastTestedAt = row.lastTestedAt === undefined || row.lastTestedAt === null
			? null
			: text(row.lastTestedAt, 64);
		if (!id || !poolId || !poolIds.has(poolId) || !label || !authType || !status
			|| (row.lastTestedAt !== undefined && row.lastTestedAt !== null && lastTestedAt === null)) return [];
		return [{
			id,
			poolId,
			label,
			enabled: row.enabled === true,
			authType,
			status,
			lastTestedAt,
			credentialConfigured: row.credentialConfigured === true,
			editable: row.editable === true
		}];
	});

	return {
		generation: integer(root.generation) ?? 0,
		models,
		providers,
		pools,
		accounts
	};
}

function isOfficialCodexAuthHost(host: string): boolean {
	return host === 'openai.com' || host.endsWith('.openai.com')
		|| host === 'chatgpt.com' || host.endsWith('.chatgpt.com');
}

/** Whitelist the only device-login fields that may cross into an action result. */
export function sanitizeAiDeviceLogin(value: unknown, accountId: string): AiDeviceLogin | null {
	const row = object(value);
	if (!row) return null;
	const verificationUri = text(row.verificationUri, 512);
	const userCode = text(row.userCode, 64);
	const intervalSeconds = integer(row.intervalSeconds, 1, 60) ?? 5;
	if (!verificationUri || !userCode || !/^[A-Z0-9-]+$/i.test(userCode)) return null;
	let safeVerificationUri: string;
	try {
		const uri = new URL(verificationUri);
		if (
			uri.protocol !== 'https:'
			|| !isOfficialCodexAuthHost(uri.hostname.toLowerCase())
			|| uri.username
			|| uri.password
		) return null;
		// Alleen de publieke verificatieroute mag de browser bereiken. Zo kan een
		// onverwachte upstream-query of fragment nooit als credential in de UI lekken.
		uri.search = '';
		uri.hash = '';
		safeVerificationUri = uri.toString();
	} catch {
		return null;
	}
	const expiresAt = row.expiresAt === undefined || row.expiresAt === null
		? null
		: text(row.expiresAt, 64);
	if (row.expiresAt !== undefined && row.expiresAt !== null && expiresAt === null) return null;
	return {
		accountId,
		verificationUri: safeVerificationUri,
		userCode,
		expiresAt,
		intervalSeconds,
		status: 'pending'
	};
}

export function authTypesFor(provider: AiProviderId): readonly AiAuthType[] {
	return provider === 'claude-agent-sdk'
		? ['oauth-token', 'api-key']
		: ['access-token', 'chatgpt-device'];
}

export function providerLabel(provider: AiProviderId): string {
	return provider === 'claude-agent-sdk' ? 'Claude Agent SDK' : 'Codex SDK';
}

export function authTypeLabel(authType: AiAuthType): string {
	switch (authType) {
		case 'oauth-token': return 'OAuth-token';
		case 'api-key': return 'API-key';
		case 'access-token': return 'Access token';
		case 'chatgpt-device': return 'ChatGPT-device-login';
	}
}

export function modelAliasLabel(alias: string): string {
	switch (alias) {
		case 'sonnet': return 'Claude Sonnet';
		case 'opus': return 'Claude Opus';
		case 'fable': return 'Claude Fable';
		case 'codex': return 'Codex';
		default: return alias;
	}
}
