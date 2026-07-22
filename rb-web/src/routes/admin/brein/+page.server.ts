import { dev } from '$app/environment';
import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { adminApi, authed } from '$lib/server/admin';
import {
	AI_AUTH_TYPES,
	AI_CREDENTIAL_MAX_LENGTH,
	AI_CREDENTIAL_MIN_LENGTH,
	AI_DEVICE_STATUSES,
	AI_PROVIDER_IDS,
	sanitizeAiConfiguration,
	sanitizeAiDeviceLogin,
	validAiCredentialLength,
	type AiAuthType,
	type AiProviderId
} from '$lib/aiConfig';

const DEVICE_SESSION_COOKIE = 'rb_ai_device_session';

function formText(form: FormData, name: string, max: number): string | null {
	const value = form.get(name);
	if (typeof value !== 'string') return null;
	const trimmed = value.trim();
	return trimmed && trimmed.length <= max ? trimmed : null;
}

function formInteger(form: FormData, name: string, min: number, max: number): number | null {
	const value = formText(form, name, 16);
	if (value === null || !/^-?\d+$/.test(value)) return null;
	const parsed = Number(value);
	return Number.isSafeInteger(parsed) && parsed >= min && parsed <= max ? parsed : null;
}

function formEnabled(form: FormData): boolean {
	return form.get('enabled') === 'true';
}

function providerId(value: string | null): AiProviderId | null {
	return value && (AI_PROVIDER_IDS as readonly string[]).includes(value)
		? (value as AiProviderId)
		: null;
}

function authType(value: string | null): AiAuthType | null {
	return value && (AI_AUTH_TYPES as readonly string[]).includes(value)
		? (value as AiAuthType)
		: null;
}

function responseText(value: unknown, max: number): string | null {
	if (typeof value !== 'string') return null;
	const trimmed = value.trim();
	return trimmed && trimmed.length <= max ? trimmed : null;
}

function responseObject(value: unknown): Record<string, unknown> | null {
	return typeof value === 'object' && value !== null && !Array.isArray(value)
		? value as Record<string, unknown>
		: null;
}

function validDeviceSessionId(value: unknown): string | null {
	const id = responseText(value, 128);
	return id && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(id)
		? id
		: null;
}

function deviceCookieMaxAge(expiresAt: string | null): number {
	if (!expiresAt) return 15 * 60;
	const seconds = Math.floor((Date.parse(expiresAt) - Date.now()) / 1000);
	return Number.isFinite(seconds) ? Math.max(60, Math.min(30 * 60, seconds)) : 15 * 60;
}

// Brein-overzicht (#236) + cockpit (brein-jobs-ui): tegel-tellingen +
// observability-rollups + de operationele cockpit (per-stap-tellingen,
// laatste-run per brein-job, /ask-retrieval-flag) + de beheerde instellingen
// (#254: de vlaggen die vroeger alleen via de VM-.env te zetten waren).
// Parallelle fetches; brein-uitval → nette lege staat (apiDown). Alle mutaties
// hieronder blijven server-side admin-gated.
export const load: PageServerLoad = async ({ cookies }) => {
	if (!authed(cookies)) throw redirect(303, '/admin');
	try {
		const [counts, observability, cockpit, settings, rawAiConfig] = await Promise.all([
			adminApi<unknown>('/api/admin/brein/overzicht'),
			adminApi<unknown>('/api/admin/brein/observability'),
			adminApi<unknown>('/api/admin/brein/cockpit'),
			adminApi<unknown[]>('/api/admin/settings').catch(() => []),
			adminApi<unknown>('/api/admin/ai-config').catch(() => null)
		]);
		return {
			counts,
			observability,
			cockpit,
			settings,
			aiConfig: sanitizeAiConfiguration(rawAiConfig),
			deviceLoginPending: Boolean(validDeviceSessionId(cookies.get(DEVICE_SESSION_COOKIE))),
			apiDown: false
		};
	} catch {
		return {
			counts: null,
			observability: null,
			cockpit: null,
			settings: [],
			aiConfig: null,
			deviceLoginPending: Boolean(validDeviceSessionId(cookies.get(DEVICE_SESSION_COOKIE))),
			apiDown: true
		};
	}
};

export const actions: Actions = {
	// Brein-jobs triggeren (brein-jobs-ui): zelfde patroon/409-afhandeling als de
	// job-action op /admin — één job tegelijk (JobRunner-gate). De naam is een
	// hidden field; onbekende jobs geeft rb-api 404 (net zo netjes afgevangen).
	job: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		const name = String(form.get('name') ?? '');
		try {
			await adminApi(`/api/admin/jobs/${encodeURIComponent(name)}`, { method: 'POST' });
			return { started: name };
		} catch (e) {
			return fail(409, { error: e instanceof Error ? e.message : String(e) });
		}
	},

	// Beheerde instellingen (#254): één of meer sleutels tegelijk. De key/value-
	// velden komen paarsgewijs binnen (getAll), zodat het nachtvenster als geheel
	// gaat — rb-api beoordeelt start en eind samen en schrijft alles-of-niets. Een
	// lege waarde betekent "terug naar de standaard". rb-api weigert een onmogelijke
	// combinatie met uitleg; die tonen we letterlijk.
	setting: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		const keys = form.getAll('key').map(String);
		const values = form.getAll('value').map(String);
		if (keys.length === 0) return fail(400, { error: 'Geen instelling opgegeven' });
		try {
			await adminApi('/api/admin/settings', {
				method: 'POST',
				body: JSON.stringify({
					changes: keys.map((key, i) => ({ key, value: values[i] ?? '' })),
					actor: 'beheer'
				})
			});
			return { settingSaved: keys[0] };
		} catch (e) {
			return fail(400, { error: e instanceof Error ? e.message : String(e) });
		}
	},

	aiPoolCreate: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const form = await request.formData();
		const provider = providerId(formText(form, 'provider', 64));
		const label = formText(form, 'label', 80);
		const priority = formInteger(form, 'priority', -100, 100);
		const weight = formInteger(form, 'weight', 1, 100);
		if (!provider || !label || priority === null || weight === null)
			return fail(400, { aiError: 'Controleer de poolnaam, prioriteit en het gewicht.' });
		try {
			await adminApi('/api/admin/ai-config/pools', {
				method: 'POST',
				body: JSON.stringify({ provider, label, enabled: formEnabled(form), priority, weight })
			});
			return { aiChanged: 'Accountpool toegevoegd.' };
		} catch {
			return fail(400, { aiError: 'Accountpool kon niet worden toegevoegd.' });
		}
	},

	aiPoolUpdate: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const form = await request.formData();
		const id = formText(form, 'id', 128);
		const label = formText(form, 'label', 80);
		const priority = formInteger(form, 'priority', -100, 100);
		const weight = formInteger(form, 'weight', 1, 100);
		if (!id || !label || priority === null || weight === null)
			return fail(400, { aiError: 'Controleer de poolinstellingen.' });
		try {
			await adminApi(`/api/admin/ai-config/pools/${encodeURIComponent(id)}`, {
				method: 'PATCH',
				body: JSON.stringify({ label, enabled: formEnabled(form), priority, weight })
			});
			return { aiChanged: 'Accountpool bijgewerkt.' };
		} catch {
			return fail(400, { aiError: 'Accountpool kon niet worden bijgewerkt.' });
		}
	},

	aiPoolDelete: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const id = formText(await request.formData(), 'id', 128);
		if (!id) return fail(400, { aiError: 'Ongeldige accountpool.' });
		try {
			await adminApi(`/api/admin/ai-config/pools/${encodeURIComponent(id)}`, { method: 'DELETE' });
			return { aiChanged: 'Accountpool verwijderd.' };
		} catch {
			return fail(400, { aiError: 'Accountpool kon niet worden verwijderd.' });
		}
	},

	aiAccountCreate: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const form = await request.formData();
		const poolId = formText(form, 'poolId', 128);
		const label = formText(form, 'label', 80);
		const selectedAuthType = authType(formText(form, 'authType', 64));
		const rawCredential = form.get('credential');
		const credential = typeof rawCredential === 'string' ? rawCredential.trim() : '';
		if (!poolId || !label || !selectedAuthType || !validAiCredentialLength(credential, true))
			return fail(400, { aiError: 'Controleer de accountinstellingen.' });
		if (selectedAuthType === 'chatgpt-device' && credential)
			return fail(400, { aiError: 'Gebruik voor device-login geen handmatig credential.' });
		try {
			await adminApi('/api/admin/ai-config/accounts', {
				method: 'POST',
				body: JSON.stringify({
					poolId,
					label,
					authType: selectedAuthType,
					enabled: formEnabled(form),
					...(credential ? { credential } : {})
				})
			});
			return { aiChanged: 'Account toegevoegd.' };
		} catch {
			return fail(400, { aiError: 'Account kon niet worden toegevoegd.' });
		}
	},

	aiAccountUpdate: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const form = await request.formData();
		const id = formText(form, 'id', 128);
		const label = formText(form, 'label', 80);
		if (!id || !label) return fail(400, { aiError: 'Controleer de accountnaam.' });
		try {
			await adminApi(`/api/admin/ai-config/accounts/${encodeURIComponent(id)}`, {
				method: 'PATCH',
				body: JSON.stringify({ label, enabled: formEnabled(form) })
			});
			return { aiChanged: 'Account bijgewerkt.' };
		} catch {
			return fail(400, { aiError: 'Account kon niet worden bijgewerkt.' });
		}
	},

	aiAccountDelete: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const id = formText(await request.formData(), 'id', 128);
		if (!id) return fail(400, { aiError: 'Ongeldig account.' });
		try {
			await adminApi(`/api/admin/ai-config/accounts/${encodeURIComponent(id)}`, { method: 'DELETE' });
			return { aiChanged: 'Account verwijderd.' };
		} catch {
			return fail(400, { aiError: 'Account kon niet worden verwijderd.' });
		}
	},

	aiCredentialReplace: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const form = await request.formData();
		const id = formText(form, 'id', 128);
		const rawCredential = form.get('credential');
		const credential = typeof rawCredential === 'string' ? rawCredential.trim() : '';
		if (!id || !validAiCredentialLength(credential))
			return fail(400, {
				aiError: `Vul een credential van ${AI_CREDENTIAL_MIN_LENGTH} tot en met ${AI_CREDENTIAL_MAX_LENGTH.toLocaleString('nl-NL')} tekens in.`
			});
		try {
			await adminApi(`/api/admin/ai-config/accounts/${encodeURIComponent(id)}/credential`, {
				method: 'PUT',
				body: JSON.stringify({ credential })
			});
			return { aiChanged: 'Credential vervangen.' };
		} catch {
			// Nooit de upstream detailtekst tonen: die mag zelfs bij een defecte
			// providerimplementatie geen ingestuurd credential terugkaatsen.
			return fail(400, { aiError: 'Credential kon niet worden vervangen.' });
		}
	},

	aiAccountTest: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const id = formText(await request.formData(), 'id', 128);
		if (!id) return fail(400, { aiError: 'Ongeldig account.' });
		try {
			await adminApi(`/api/admin/ai-config/accounts/${encodeURIComponent(id)}/test`, {
				method: 'POST'
			});
			return { aiChanged: 'Accounttest afgerond.' };
		} catch {
			return fail(400, { aiError: 'Accounttest is mislukt.' });
		}
	},

	aiDeviceStart: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const accountId = formText(await request.formData(), 'id', 128);
		if (!accountId) return fail(400, { aiError: 'Ongeldig account.' });
		const previousSessionId = validDeviceSessionId(cookies.get(DEVICE_SESSION_COOKIE));
		cookies.delete(DEVICE_SESSION_COOKIE, { path: '/admin/brein' });
		if (previousSessionId) {
			try {
				await adminApi(`/api/admin/ai-config/device-login/${encodeURIComponent(previousSessionId)}`, {
					method: 'DELETE'
				});
			} catch {
				// Een verlopen voorganger hoeft een nieuwe login niet te blokkeren.
			}
		}
		try {
			const raw = await adminApi<unknown>(
				`/api/admin/ai-config/accounts/${encodeURIComponent(accountId)}/device-login`,
				{ method: 'POST' }
			);
			const row = responseObject(raw);
			const sessionId = validDeviceSessionId(row?.sessionId);
			const login = sanitizeAiDeviceLogin(raw, accountId);
			if (!sessionId || !login)
				return fail(502, { aiError: 'Device-login gaf geen veilig bruikbaar antwoord.' });
			cookies.set(DEVICE_SESSION_COOKIE, sessionId, {
				path: '/admin/brein',
				httpOnly: true,
				sameSite: 'strict',
				secure: !dev,
				maxAge: deviceCookieMaxAge(login.expiresAt)
			});
			// sessionId blijft in de HttpOnly-cookie en komt niet in het action-resultaat.
			return { aiDeviceLogin: login };
		} catch {
			return fail(400, { aiError: 'Device-login kon niet worden gestart.' });
		}
	},

	aiDevicePoll: async ({ cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const sessionId = validDeviceSessionId(cookies.get(DEVICE_SESSION_COOKIE));
		if (!sessionId) {
			cookies.delete(DEVICE_SESSION_COOKIE, { path: '/admin/brein' });
			return fail(400, { aiDevicePoll: { status: 'expired' as const } });
		}
		try {
			const raw = await adminApi<unknown>(
				`/api/admin/ai-config/device-login/${encodeURIComponent(sessionId)}`
			);
			const row = responseObject(raw);
			const status = responseText(row?.status, 32);
			if (!status || !(AI_DEVICE_STATUSES as readonly string[]).includes(status))
				throw new Error('onbekende device-status');
			const pollAfterMs = typeof row?.pollAfterMs === 'number'
				&& Number.isSafeInteger(row.pollAfterMs)
				&& row.pollAfterMs >= 1_000
				&& row.pollAfterMs <= 60_000
				? row.pollAfterMs
				: undefined;
			if (status !== 'pending') cookies.delete(DEVICE_SESSION_COOKIE, { path: '/admin/brein' });
			return { aiDevicePoll: { status, ...(pollAfterMs ? { pollAfterMs } : {}) } };
		} catch {
			return fail(400, { aiError: 'Device-loginstatus kon niet worden gecontroleerd.' });
		}
	},

	aiDeviceCancel: async ({ cookies }) => {
		if (!authed(cookies)) return fail(401, { aiError: 'Niet ingelogd' });
		const sessionId = validDeviceSessionId(cookies.get(DEVICE_SESSION_COOKIE));
		cookies.delete(DEVICE_SESSION_COOKIE, { path: '/admin/brein' });
		if (!sessionId) return { aiChanged: 'Er was geen actieve device-login.' };
		try {
			await adminApi(`/api/admin/ai-config/device-login/${encodeURIComponent(sessionId)}`, {
				method: 'DELETE'
			});
			return { aiChanged: 'Device-login geannuleerd.' };
		} catch {
			return fail(400, { aiError: 'Device-login kon niet netjes worden geannuleerd.' });
		}
	}
};
