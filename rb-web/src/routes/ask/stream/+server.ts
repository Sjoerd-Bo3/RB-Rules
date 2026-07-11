import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { API_BASE } from '$lib/api';
import { userHeaders } from '$lib/server/user';

// Streaming-proxy (#31): de browser praat nooit rechtstreeks met rb-api —
// deze route geeft de NDJSON-stream van POST /api/ask/stream 1-op-1 door.
// De frames worden hier bewust niet geparseerd: doorgeven is genoeg en houdt
// de proxy dom; rb-api valideert het request en bewaakt rate limit én
// account-quota (#42) — daarom reist het sessietoken hier net als bij de
// niet-streamende action mee.
export const POST: RequestHandler = async ({ request, getClientAddress, cookies }) => {
	let upstream: Response;
	try {
		upstream = await fetch(`${API_BASE}/api/ask/stream`, {
			method: 'POST',
			headers: {
				'content-type': 'application/json',
				'x-client-ip': getClientAddress(),
				...userHeaders(cookies)
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
		// Status doorgeven (o.a. 429/401/403 van rate-limit en quota-poort)
		// zodat de client het onderscheid kan maken tussen "even wachten",
		// "log in" en "val terug".
		return json({ error: `rb-api ${upstream.status}` }, { status: upstream.status || 502 });
	}
	return new Response(upstream.body, {
		headers: {
			'content-type': 'application/x-ndjson',
			'cache-control': 'no-store'
		}
	});
};
