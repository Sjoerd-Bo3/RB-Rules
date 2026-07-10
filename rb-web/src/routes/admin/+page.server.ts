import { createHash } from 'node:crypto';
import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { env } from '$env/dynamic/private';

const API = env.RB_API_URL ?? 'http://localhost:8080';
const COOKIE = 'rb_admin';

const token = () =>
	env.ADMIN_PASSWORD ? createHash('sha256').update(`${env.ADMIN_PASSWORD}::rb-v2`).digest('hex') : null;

const authed = (cookies: { get(name: string): string | undefined }) => {
	const t = token();
	return t !== null && cookies.get(COOKIE) === t;
};

async function adminApi<T>(path: string, init: RequestInit = {}): Promise<T> {
	const res = await fetch(`${API}${path}`, {
		...init,
		headers: {
			'content-type': 'application/json',
			'X-Admin-Key': env.ADMIN_PASSWORD ?? '',
			...(init.headers ?? {})
		}
	});
	if (!res.ok) throw new Error(`rb-api ${res.status}: ${path}`);
	return res.json() as Promise<T>;
}

export const load: PageServerLoad = async ({ cookies }) => {
	if (!authed(cookies)) return { authed: false, sources: [], logs: [] };
	try {
		const [sources, logs] = await Promise.all([
			adminApi<unknown[]>('/api/sources'),
			adminApi<unknown[]>('/api/admin/logs')
		]);
		return { authed: true, sources, logs, apiDown: false };
	} catch {
		return { authed: true, sources: [], logs: [], apiDown: true };
	}
};

export const actions: Actions = {
	login: async ({ request, cookies }) => {
		const form = await request.formData();
		if (!env.ADMIN_PASSWORD || form.get('password') !== env.ADMIN_PASSWORD) {
			return fail(401, { error: 'Onjuist wachtwoord' });
		}
		cookies.set(COOKIE, token()!, {
			path: '/',
			httpOnly: true,
			sameSite: 'lax',
			maxAge: 60 * 60 * 24 * 30
		});
		return { ok: true };
	},
	logout: async ({ cookies }) => {
		cookies.delete(COOKIE, { path: '/' });
		return { ok: true };
	},
	scan: async ({ cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const results = await adminApi<unknown[]>('/api/admin/scan', { method: 'POST' });
		return { scanned: results };
	},
	cardsync: async ({ cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const result = await adminApi<unknown>('/api/admin/cards/sync', { method: 'POST' });
		return { cardsync: result };
	},
	toggle: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		await adminApi(`/api/admin/sources/${form.get('id')}`, {
			method: 'PATCH',
			body: JSON.stringify({ enabled: form.get('enabled') === 'true' })
		});
		return { ok: true };
	}
};
