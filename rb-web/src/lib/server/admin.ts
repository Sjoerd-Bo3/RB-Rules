import { createHash } from 'node:crypto';
import { env } from '$env/dynamic/private';

const API = env.RB_API_URL ?? 'http://localhost:8080';

export const ADMIN_COOKIE = 'rb_admin';

export const adminToken = () =>
	env.ADMIN_PASSWORD ? createHash('sha256').update(`${env.ADMIN_PASSWORD}::rb-v2`).digest('hex') : null;

export const authed = (cookies: { get(name: string): string | undefined }) => {
	const t = adminToken();
	return t !== null && cookies.get(ADMIN_COOKIE) === t;
};

export async function adminApi<T>(path: string, init: RequestInit = {}): Promise<T> {
	const res = await fetch(`${API}${path}`, {
		...init,
		headers: {
			'content-type': 'application/json',
			'X-Admin-Key': env.ADMIN_PASSWORD ?? '',
			...(init.headers ?? {})
		}
	});
	if (!res.ok) {
		const body = await res.json().catch(() => null);
		throw new Error(
			(body as { error?: string } | null)?.error ?? `rb-api ${res.status}: ${path}`
		);
	}
	return res.json() as Promise<T>;
}
