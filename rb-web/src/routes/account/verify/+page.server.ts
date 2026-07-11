import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { api } from '$lib/api';
import { USER_COOKIE } from '$lib/server/user';

// De link uit de mail is een GET; het token wordt pas verzilverd met de
// bevestigingsknop (POST). Zo consumeert een mail-scanner die links
// voor-opent de eenmalige token niet.
export const load: PageServerLoad = async ({ url }) => ({
	token: url.searchParams.get('token') ?? ''
});

export const actions: Actions = {
	confirm: async ({ request, cookies, getClientAddress }) => {
		const form = await request.formData();
		const token = String(form.get('token') ?? '');
		if (!token) return fail(400, { error: 'De inloglink is onvolledig — vraag een nieuwe aan.' });
		try {
			const r = await api<{ token: string; email: string; expiresAt: string }>('/api/auth/verify', {
				method: 'POST',
				headers: { 'x-client-ip': getClientAddress() },
				body: JSON.stringify({ token })
			});
			cookies.set(USER_COOKIE, r.token, {
				path: '/',
				httpOnly: true,
				sameSite: 'lax',
				maxAge: 60 * 60 * 24 * 90 // gelijk aan de sessieduur in rb-api
			});
		} catch (e) {
			const msg = e instanceof Error ? e.message : String(e);
			return fail(400, {
				error: msg.includes('400')
					? 'De inloglink is ongeldig of verlopen — vraag een nieuwe aan via de accountpagina.'
					: `Inloggen mislukt (${msg})`
			});
		}
		redirect(303, '/account');
	}
};
