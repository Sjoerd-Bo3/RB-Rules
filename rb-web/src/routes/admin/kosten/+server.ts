import { json, error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { adminApi, authed } from '$lib/server/admin';

// Live poll-endpoint voor het kosten-paneel (#328) — zelfde aanpak als
// /admin/status: client-side polling, cookie-beveiligd, browser praat nooit
// rechtstreeks met rb-api.
export const GET: RequestHandler = async ({ url, cookies }) => {
	if (!authed(cookies)) throw error(401, 'Niet ingelogd');
	const raw = url.searchParams.get('period') ?? '7d';
	const period = ['vandaag', '7d', '30d'].includes(raw) ? raw : '7d';
	try {
		return json(await adminApi(`/api/admin/costs?period=${period}`));
	} catch {
		throw error(502, 'rb-api niet bereikbaar');
	}
};
