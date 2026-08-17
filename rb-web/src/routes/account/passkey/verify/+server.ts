import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { API_BASE } from '$lib/api';
import { USER_COOKIE, userHeaders } from '$lib/server/user';

// Verify-stap van een WebAuthn-ceremonie (#109). Bij een geslaagde login of
// registratie zet dit dezelfde httpOnly rb_user-cookie als de magic-link-
// verify; het sessietoken zelf bereikt de browser-JavaScript nooit.
export const POST: RequestHandler = async ({ request, cookies, getClientAddress, fetch }) => {
	const body = await request.json().catch(() => null);
	const kind = body?.kind === 'login' ? 'login' : 'register';
	try {
		const res = await fetch(`${API_BASE}/api/auth/passkey/${kind}/verify`, {
			method: 'POST',
			headers: {
				'content-type': 'application/json',
				'x-client-ip': getClientAddress(),
				...userHeaders(cookies)
			},
			body: JSON.stringify({ token: body?.token ?? '', response: body?.response ?? null })
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
		if (!res.ok)
			return json(
				{ error: payload?.error ?? `Passkey-verificatie mislukt (rb-api ${res.status}).` },
				{ status: res.status }
			);
		// token is null bij "extra passkey bij bestaand account" — dan loopt
		// de huidige sessie gewoon door en blijft de cookie staan.
		if (typeof payload?.token === 'string' && payload.token) {
			cookies.set(USER_COOKIE, payload.token, {
				path: '/',
				httpOnly: true,
				sameSite: 'lax',
				maxAge: 60 * 60 * 24 * 90 // gelijk aan de sessieduur in rb-api
			});
		}
		return json({ ok: true, email: payload?.email ?? null });
	} catch (e) {
		return json(
			{ error: `De server is even niet bereikbaar (${e instanceof Error ? e.message : e}).` },
			{ status: 502 }
		);
	}
};
