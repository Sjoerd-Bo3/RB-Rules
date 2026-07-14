import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { api } from '$lib/api';
import { USER_COOKIE, userHeaders } from '$lib/server/user';

// Account (#42/#109): passkey is de primaire login (geen mailafhankelijkheid),
// de magic-link blijft als secundair pad. Wachtwoorden bestaan hier bewust niet.
export interface AccountInfo {
	email: string;
	blocked: boolean;
	dailyQuota: number;
	dailyPhotoQuota: number;
	/** Grondig-dagquotum (#153): zelf geforceerde agentic-vragen per dag. */
	dailyAgenticQuota: number;
	questionsToday: number;
	photosToday: number;
	agenticToday: number;
	createdAt: string;
}

export interface PasskeyInfo {
	id: number;
	name: string;
	createdAt: string;
	lastUsedAt: string | null;
}

export const load: PageServerLoad = async ({ cookies }) => {
	if (!cookies.get(USER_COOKIE)) return { account: null, passkeys: [], apiDown: false };
	try {
		const account = await api<AccountInfo>('/api/auth/me', { headers: userHeaders(cookies) });
		// Best-effort: zonder passkey-lijst is de accountpagina nog steeds
		// bruikbaar (de beheersectie meldt dan gewoon "geen passkeys").
		const passkeys = await api<PasskeyInfo[]>('/api/auth/passkeys', {
			headers: userHeaders(cookies)
		}).catch(() => [] as PasskeyInfo[]);
		return { account, passkeys, apiDown: false };
	} catch (e) {
		// 401 = sessie verlopen of ingetrokken: cookie opruimen, opnieuw inloggen.
		if (e instanceof Error && e.message.includes('401')) {
			cookies.delete(USER_COOKIE, { path: '/' });
			return { account: null, passkeys: [], apiDown: false };
		}
		return { account: null, passkeys: [], apiDown: true };
	}
};

export const actions: Actions = {
	login: async ({ request, getClientAddress }) => {
		const form = await request.formData();
		const email = String(form.get('email') ?? '').trim();
		if (!email) return fail(400, { error: 'Vul een e-mailadres in.', email });
		try {
			const r = await api<{ ok: boolean; devLink: string | null }>('/api/auth/request', {
				method: 'POST',
				headers: { 'x-client-ip': getClientAddress() },
				body: JSON.stringify({ email })
			});
			// devLink bestaat alleen in Development (flow testen zonder mailserver).
			return { sent: true, email, devLink: r.devLink ?? null };
		} catch (e) {
			const msg = e instanceof Error ? e.message : String(e);
			if (msg.includes('429'))
				return fail(429, { error: 'Te veel inlogpogingen — probeer het over een kwartier opnieuw.', email });
			if (msg.includes('400')) return fail(400, { error: 'Dat lijkt geen geldig e-mailadres.', email });
			if (msg.includes('503'))
				return fail(503, { error: 'Inloggen is tijdelijk niet beschikbaar — probeer het later opnieuw.', email });
			return fail(502, { error: `Aanvragen mislukt (${msg})`, email });
		}
	},
	logout: async ({ cookies }) => {
		try {
			await api('/api/auth/logout', { method: 'POST', headers: userHeaders(cookies) });
		} catch {
			// Sessie server-side intrekken is best-effort; de cookie gaat sowieso weg.
		}
		cookies.delete(USER_COOKIE, { path: '/' });
		return { loggedOut: true };
	},
	// Passkey verwijderen (#109). De bevestiging (inclusief de waarschuwing
	// bij de laatste passkey) zit in de UI; rb-api verwijdert alleen keys van
	// het eigen account.
	removePasskey: async ({ request, cookies }) => {
		const form = await request.formData();
		const id = Number(form.get('id'));
		if (!Number.isInteger(id) || id <= 0)
			return fail(400, { passkeyError: 'Ongeldige passkey — herlaad de pagina.' });
		try {
			await api(`/api/auth/passkeys/${id}`, {
				method: 'DELETE',
				headers: userHeaders(cookies)
			});
			return { removed: true };
		} catch (e) {
			const msg = e instanceof Error ? e.message : String(e);
			if (msg.includes('401'))
				return fail(401, { passkeyError: 'Je sessie is verlopen — log opnieuw in.' });
			return fail(502, { passkeyError: `Verwijderen mislukt (${msg}) — probeer het opnieuw.` });
		}
	}
};
