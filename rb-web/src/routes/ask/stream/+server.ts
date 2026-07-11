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
		// De fetch naar rb-api faalde vóór de eerste flush. Cruciaal verschil
		// (review #31): bij een gewéigerde verbinding (rb-api down) is er
		// zeker niets gestart en mag de client automatisch terugvallen; bij
		// elke andere breuk (reset terwijl rb-api al aan het werk was, in het
		// venster vóór het meta-frame) kan er al een LLM-call lopen — dan
		// signaleert `retry: true` dat de client een expliciete
		// "Opnieuw proberen"-knop moet tonen i.p.v. stil dubbel te betalen.
		const codes: string[] = [];
		let cause: unknown = e;
		for (let depth = 0; cause && depth < 5; depth++) {
			const err = cause as { code?: string; errors?: { code?: string }[]; cause?: unknown };
			if (err.code) codes.push(err.code);
			for (const sub of err.errors ?? []) if (sub.code) codes.push(sub.code);
			cause = err.cause;
		}
		const refused = codes.includes('ECONNREFUSED') || codes.includes('ENOTFOUND');
		return json(
			{ error: `rb-api onbereikbaar (${e instanceof Error ? e.message : e})`, retry: !refused },
			{ status: 502 }
		);
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
