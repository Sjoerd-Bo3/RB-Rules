import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { API_BASE } from '$lib/api';
import { userHeaders } from '$lib/server/user';

// Options-stap van een WebAuthn-ceremonie (#109). De browser praat nooit
// direct met rb-api (conventie); bewust een kale fetch in plaats van de
// api()-helper, omdat de specifieke foutmelding van rb-api ("dit adres heeft
// al een account") hier mét statuscode moet doorkomen.
export const POST: RequestHandler = async ({ request, cookies, getClientAddress, fetch }) => {
	const body = await request.json().catch(() => null);
	const kind = body?.kind === 'login' ? 'login' : 'register';
	try {
		const res = await fetch(`${API_BASE}/api/auth/passkey/${kind}/options`, {
			method: 'POST',
			headers: {
				'content-type': 'application/json',
				'x-client-ip': getClientAddress(),
				...userHeaders(cookies)
			},
			body: JSON.stringify({ email: typeof body?.email === 'string' ? body.email : null })
		});
		// De rate-limiter (429) antwoordt zonder body — geef daar een
		// bruikbare melding voor terug in plaats van een parse-fout.
		if (res.status === 429)
			return json(
				{ error: 'Te veel passkey-pogingen — probeer het over een kwartier opnieuw.' },
				{ status: 429 }
			);
		const payload = await res
			.json()
			.catch(() => ({ error: 'Onleesbaar antwoord van de server — probeer het opnieuw.' }));
		return json(payload, { status: res.status });
	} catch (e) {
		return json(
			{ error: `De server is even niet bereikbaar (${e instanceof Error ? e.message : e}).` },
			{ status: 502 }
		);
	}
};
