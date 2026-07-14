import { json, error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { adminApi, authed } from '$lib/server/admin';

// Bron-dossier (#171): herkomst, opbrengst per soort en verwerkingsstatus,
// lazy opgehaald bij het uitklappen in de bronnentabel (zelfde patroon als
// /admin/mechanics/[id]/cards en /admin/asktraces/[id]).
export const GET: RequestHandler = async ({ cookies, params }) => {
	if (!authed(cookies)) throw error(401, 'Niet ingelogd');
	const id = params.id?.trim();
	if (!id) throw error(400, 'Ongeldig id');
	try {
		return json(await adminApi(`/api/admin/sources/${encodeURIComponent(id)}/dossier`));
	} catch {
		throw error(502, 'rb-api niet bereikbaar');
	}
};
