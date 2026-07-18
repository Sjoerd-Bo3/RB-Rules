import type { LayoutServerLoad } from './$types';
import { authed } from '$lib/server/admin';

// Beheer-shell (#214 redesign): de admin-routes krijgen een eigen shell
// (admin/+layout.svelte) los van de publieke zijbalk. De layout hoeft alleen
// te weten óf er is ingelogd — dan toont hij de volledige nav; zo niet, alleen
// de merk-chrome rond het login-scherm. Tel-badges komen uit de al geladen
// page-data (status.counts / sources), niet uit een extra fetch hier.
export const load: LayoutServerLoad = async ({ cookies }) => {
	return { authed: authed(cookies) };
};
