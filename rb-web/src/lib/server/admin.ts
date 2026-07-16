import { createHash } from 'node:crypto';
import { env } from '$env/dynamic/private';
import { API_BASE as API } from '$lib/api';

export const ADMIN_COOKIE = 'rb_admin';

export const adminToken = () =>
	env.ADMIN_PASSWORD ? createHash('sha256').update(`${env.ADMIN_PASSWORD}::rb-v2`).digest('hex') : null;

export const authed = (cookies: { get(name: string): string | undefined }) => {
	const t = adminToken();
	return t !== null && cookies.get(ADMIN_COOKIE) === t;
};

/** Fout van rb-api mét statuscode (#199 review-fix): acties die op een
 *  specifieke status moeten reageren (409 = TOCTOU-fence op de bulk-actie)
 *  kunnen die hier aflezen; bestaande catch-blokken zien gewoon een Error. */
export class AdminApiError extends Error {
	constructor(
		message: string,
		public readonly status: number
	) {
		super(message);
	}
}

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
		throw new AdminApiError(
			(body as { error?: string } | null)?.error ?? `rb-api ${res.status}: ${path}`,
			res.status
		);
	}
	return res.json() as Promise<T>;
}
