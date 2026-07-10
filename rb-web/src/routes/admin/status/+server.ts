import { json, error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { adminApi, authed } from '$lib/server/admin';

// Live status-feed voor de admin-UI (client-side polling, cookie-beveiligd).
export const GET: RequestHandler = async ({ cookies }) => {
	if (!authed(cookies)) throw error(401, 'Niet ingelogd');
	try {
		return json(await adminApi('/api/admin/status'));
	} catch {
		throw error(502, 'rb-api niet bereikbaar');
	}
};
