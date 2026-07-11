import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { API_BASE } from '$lib/api';

// Streaming-proxy (#31): de browser praat nooit rechtstreeks met rb-api —
// deze route geeft de NDJSON-stream van POST /api/ask/stream 1-op-1 door.
// De frames worden hier bewust niet geparseerd: doorgeven is genoeg en houdt
// de proxy dom; rb-api valideert het request en bewaakt de rate limit.
export const POST: RequestHandler = async ({ request, getClientAddress }) => {
	let upstream: Response;
	try {
		upstream = await fetch(`${API_BASE}/api/ask/stream`, {
			method: 'POST',
			headers: {
				'content-type': 'application/json',
				'x-client-ip': getClientAddress()
			},
			body: await request.text()
		});
	} catch (e) {
		// rb-api onbereikbaar vóór de eerste byte: nette JSON-fout — de client
		// valt dan terug op de niet-streamende route (er zijn nog geen
		// LLM-kosten gemaakt).
		return json({ error: `rb-api onbereikbaar (${e instanceof Error ? e.message : e})` }, { status: 502 });
	}
	if (!upstream.ok || !upstream.body) {
		// Status doorgeven (o.a. 429 van de llm-rate-limit) zodat de client
		// het onderscheid kan maken tussen "even wachten" en "val terug".
		return json({ error: `rb-api ${upstream.status}` }, { status: upstream.status || 502 });
	}
	return new Response(upstream.body, {
		headers: {
			'content-type': 'application/x-ndjson',
			'cache-control': 'no-store'
		}
	});
};
