import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { api } from '$lib/api';

// Lazy-proxy voor de LLM-uitleg (#30) — pas opgehaald als de bezoeker erom
// vraagt; rb-api cachet het resultaat per kaartpaar.
export const GET: RequestHandler = async ({ params, getClientAddress }) => {
	try {
		return json(
			await api<{ explanation: string; cached: boolean }>(
				`/api/cards/${encodeURIComponent(params.id)}/similar/${encodeURIComponent(params.other)}/explain`,
				{ headers: { 'x-client-ip': getClientAddress() } }
			)
		);
	} catch (e) {
		return json(
			{ error: e instanceof Error ? e.message : String(e) },
			{ status: 502 }
		);
	}
};
