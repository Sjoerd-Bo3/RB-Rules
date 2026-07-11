import type { Cookies } from '@sveltejs/kit';

// Sessiecookie voor accounts (#42) — zelfde patroon als de admin-cookie:
// httpOnly, alleen server-side gelezen; de browser praat nooit met rb-api.
export const USER_COOKIE = 'rb_user';

/** Header voor rb-api: het sessietoken van de ingelogde bezoeker. Leeg object
 *  indien anoniem — dan geldt bij rb-api de per-IP-rate-limit. */
export const userHeaders = (cookies: Pick<Cookies, 'get'>): Record<string, string> => {
	const token = cookies.get(USER_COOKIE);
	return token ? { 'X-User-Token': token } : {};
};
