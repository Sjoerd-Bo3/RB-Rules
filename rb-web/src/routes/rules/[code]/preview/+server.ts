import { json, error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { api } from '$lib/api';
import type { Section } from '../+page.server';

// Compacte sectie-preview voor de hover-popover op §-chips (#370). De browser
// praat nooit rechtstreeks met rb-api; dit is dezelfde bron als de
// sectiepagina hiernaast (`/api/rules/section/{code}`), alleen teruggebracht
// tot wat de popover toont. Geen ?source-doorgifte: de chips dragen die niet,
// en de popover toont de sectie zoals /rules/{code} hem standaard kiest.

/** De api()-helper gooit `rb-api <status>: <pad>` — status terugwinnen zodat
 *  een onbekende sectie een 404 blijft en een platte api een 502 wordt
 *  (zelfde patroon als graph/node/+server.ts). */
function apiStatus(e: unknown): number | null {
	const m = e instanceof Error ? /^rb-api (\d{3}):/.exec(e.message) : null;
	return m ? Number(m[1]) : null;
}

export const GET: RequestHandler = async ({ params }) => {
	try {
		const s = await api<Section>(`/api/rules/section/${encodeURIComponent(params.code)}`);
		return json({
			code: s.code,
			text: s.text,
			parents: s.parents,
			sourceName: s.sourceName
		});
	} catch (e) {
		if (apiStatus(e) === 404) throw error(404, `Sectie § ${params.code} niet gevonden`);
		throw error(502, 'rb-api niet bereikbaar');
	}
};
