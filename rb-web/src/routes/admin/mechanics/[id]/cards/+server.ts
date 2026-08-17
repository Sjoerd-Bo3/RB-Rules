import { json, error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { adminApi, authed } from '$lib/server/admin';

// Bewijs bij een keyword-kandidaat (#123): de kaarten waarop de bracketed
// term voorkomt, lazy opgehaald bij het uitklappen (cookie-beveiligd,
// zelfde patroon als /admin/status).
export const GET: RequestHandler = async ({ cookies, params }) => {
	if (!authed(cookies)) throw error(401, 'Niet ingelogd');
	const id = Number(params.id);
	if (!Number.isInteger(id) || id <= 0) throw error(400, 'Ongeldig id');
	try {
		return json(await adminApi(`/api/admin/mechanics/${id}/cards`));
	} catch {
		throw error(502, 'rb-api niet bereikbaar');
	}
};
