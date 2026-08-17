import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { api } from '$lib/api';
import { USER_COOKIE, userHeaders } from '$lib/server/user';

// Lazy-proxy voor de LLM-uitleg (#30) — pas opgehaald als de bezoeker erom
// vraagt; rb-api cachet het resultaat per kaartpaar.
export const GET: RequestHandler = async ({ params, getClientAddress, cookies }) => {
	// Login-poort (#328): ook deze uitleg is een LLM-call. rb-api weigert
	// anoniem sowieso (defense-in-depth); hier komt de nette melding vandaan.
	if (!cookies.get(USER_COOKIE)) {
		return json({ error: 'Log in om AI-uitleg op te vragen.' }, { status: 401 });
	}
	try {
		return json(
			await api<{ explanation: string; cached: boolean }>(
				`/api/cards/${encodeURIComponent(params.id)}/similar/${encodeURIComponent(params.other)}/explain`,
				{ headers: { 'x-client-ip': getClientAddress(), ...userHeaders(cookies) } }
			)
		);
	} catch (e) {
		return json(
			{ error: e instanceof Error ? e.message : String(e) },
			{ status: 502 }
		);
	}
};
