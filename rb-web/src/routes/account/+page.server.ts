import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { api } from '$lib/api';
import { USER_COOKIE, userHeaders } from '$lib/server/user';

// Account met magic-link (#42): e-mail invullen → link in de mail → sessie.
// Wachtwoorden bestaan hier bewust niet.
export interface AccountInfo {
	email: string;
	blocked: boolean;
	dailyQuota: number;
	dailyPhotoQuota: number;
	questionsToday: number;
	photosToday: number;
	createdAt: string;
}

export const load: PageServerLoad = async ({ cookies }) => {
	if (!cookies.get(USER_COOKIE)) return { account: null, apiDown: false };
	try {
		const account = await api<AccountInfo>('/api/auth/me', { headers: userHeaders(cookies) });
		return { account, apiDown: false };
	} catch (e) {
		// 401 = sessie verlopen of ingetrokken: cookie opruimen, opnieuw inloggen.
		if (e instanceof Error && e.message.includes('401')) {
			cookies.delete(USER_COOKIE, { path: '/' });
			return { account: null, apiDown: false };
		}
		return { account: null, apiDown: true };
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
	}
};
