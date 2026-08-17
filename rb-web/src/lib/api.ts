// Server-side proxy naar rb-api. In compose: http://rb-api:8080.
import { env } from '$env/dynamic/private';

/** Eén bron voor de rb-api-basis-URL (#59: stond dubbel in api.ts en admin.ts). */
export const API_BASE = env.RB_API_URL ?? 'http://localhost:8080';

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
	const res = await fetch(`${API_BASE}${path}`, {
		...init,
		headers: {
			// ASP.NET bindt een JSON-body alleen met expliciete content-type (anders 415).
			...(init.body ? { 'content-type': 'application/json' } : {}),
			...(init.headers ?? {})
		}
	});
	if (!res.ok) throw new Error(`rb-api ${res.status}: ${path}`);
	return res.json() as Promise<T>;
}
