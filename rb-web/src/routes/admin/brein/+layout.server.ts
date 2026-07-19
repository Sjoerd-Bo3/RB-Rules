import { redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';
import { authed } from '$lib/server/admin';

// Brein-verkenner (#236): read-only inspectie, admin-gated. Eén auth-guard voor
// de hele subsectie; niet-ingelogd valt terug op het login-scherm van /admin.
export const load: LayoutServerLoad = async ({ cookies }) => {
	if (!authed(cookies)) throw redirect(303, '/admin');
	return {};
};
