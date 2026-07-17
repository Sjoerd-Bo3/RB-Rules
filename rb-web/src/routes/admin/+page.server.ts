import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { env } from '$env/dynamic/private';
import { ADMIN_COOKIE, adminApi, adminToken, authed } from '$lib/server/admin';

export const load: PageServerLoad = async ({ cookies }) => {
	if (!authed(cookies))
		return {
			authed: false, sources: [], status: null, corrections: [],
			askTraces: [], knowledge: [], mechanics: [], upcoming: [], feeds: [], paths: [], drift: null
		};
	try {
		const [sources, status, corrections, askTraces, knowledge, mechanics, upcoming, feeds, paths, drift] =
			await Promise.all([
				// Bronnenlijst (#180): admin-endpoint i.p.v. het publieke
				// /api/sources — dat laatste verbergt genegeerde bronnen nu
				// (standaardlijst); dit beheerscherm heeft ze allebei nodig
				// (incl. de negeer-kandidaat-vlag), gefilterd client-side.
				adminApi<unknown[]>('/api/admin/sources'),
				adminApi<unknown>('/api/admin/status'),
				adminApi<unknown[]>('/api/admin/corrections').catch(() => []),
				adminApi<unknown[]>('/api/admin/asktraces').catch(() => []),
				adminApi<unknown[]>('/api/admin/knowledge').catch(() => []),
				// Mechaniek-kandidaten (#52): reviewqueue voor het vocabulaire.
				adminApi<unknown[]>('/api/admin/mechanics').catch(() => []),
				// Aankomende sets (#52): publiek endpoint, hier als beheersignaal.
				adminApi<unknown[]>('/api/sets/upcoming').catch(() => []),
				// Bron-feeds (#167): alleen de naam/id nodig voor "stamt van: …"
				// bij de bronnentabel hieronder; het volledige beheer zit op
				// /admin/overview/feeds.
				adminApi<unknown[]>('/api/admin/overview/feeds').catch(() => []),
				// Paden (#190): geordende jobs die vanzelf doorstromen.
				adminApi<unknown[]>('/api/admin/paths').catch(() => []),
				// Graph-drift (#214 overzicht): loopt de Neo4j-projectie achter op
				// Postgres? Zelfde bron als het kennis-gaten-rapport; hier alleen de
				// drift-tak voor de Overzicht-tabel. Parallel opgehaald, dus geen
				// extra wachttijd; AI/graph weg = null en de tabel degradeert netjes.
				adminApi<{ drift: unknown }>('/api/admin/overview/gaps')
					.then((g) => g.drift)
					.catch(() => null)
			]);
		return {
			authed: true, sources, status, corrections, askTraces,
			knowledge, mechanics, upcoming, feeds, paths, drift, apiDown: false
		};
	} catch {
		return {
			authed: true, sources: [], status: null, corrections: [],
			askTraces: [], knowledge: [], mechanics: [], upcoming: [], feeds: [], paths: [], drift: null, apiDown: true
		};
	}
};

export const actions: Actions = {
	login: async ({ request, cookies }) => {
		const form = await request.formData();
		if (!env.ADMIN_PASSWORD || form.get('password') !== env.ADMIN_PASSWORD) {
			return fail(401, { error: 'Onjuist wachtwoord' });
		}
		cookies.set(ADMIN_COOKIE, adminToken()!, {
			path: '/',
			httpOnly: true,
			sameSite: 'lax',
			maxAge: 60 * 60 * 24 * 30
		});
		return { ok: true };
	},
	logout: async ({ cookies }) => {
		cookies.delete(ADMIN_COOKIE, { path: '/' });
		return { ok: true };
	},
	job: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		const name = String(form.get('name') ?? '');
		try {
			await adminApi(`/api/admin/jobs/${encodeURIComponent(name)}`, { method: 'POST' });
			return { started: name };
		} catch (e) {
			return fail(409, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	// Paden (#190): zelfde TryStart-conflictgedrag als losse jobs — één pad of
	// job tegelijk (JobRunner-gate), dus dezelfde 409-afhandeling.
	path: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		const name = String(form.get('name') ?? '');
		try {
			await adminApi(`/api/admin/paths/${encodeURIComponent(name)}`, { method: 'POST' });
			return { started: name };
		} catch (e) {
			return fail(409, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	verifyCorrection: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/corrections/${form.get('id')}/verify`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	rejectCorrection: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			// Zacht afwijzen (#177): rejected tombstone i.p.v. verwijderen, zodat
			// een volgende clarify-mining-run het item niet opnieuw aanmaakt.
			await adminApi(`/api/admin/corrections/${form.get('id')}/reject`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	deleteCorrection: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/corrections/${form.get('id')}`, { method: 'DELETE' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	approveKnowledge: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/knowledge/${form.get('id')}/approve`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	deleteKnowledge: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/knowledge/${form.get('id')}`, { method: 'DELETE' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	// Mechaniek-vocabulaire (#52): accepteren = vocabulaire + re-mine van de
	// betrokken kaarten; verwerpen = term komt niet opnieuw de queue in.
	acceptMechanic: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/mechanics/${form.get('id')}/accept`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	rejectMechanic: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/mechanics/${form.get('id')}/reject`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	toggle: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/sources/${form.get('id')}`, {
				method: 'PATCH',
				body: JSON.stringify({ enabled: form.get('enabled') === 'true' })
			});
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	// Negeren met reden (#180): los van `toggle` (Enabled, "tijdelijk uit") —
	// een bewuste, blijvende beoordeling. Reden is optioneel.
	ignoreSource: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/sources/${form.get('id')}/ignore`, {
				method: 'POST',
				body: JSON.stringify({ reason: form.get('reason') || null })
			});
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	unignoreSource: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/sources/${form.get('id')}/unignore`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	// Bron-dossier (#171): re-triggerknop — draait scan (+ classify/claims
	// zoals de scan dat altijd al deed) opnieuw voor déze ene bron. Synchroon
	// (geen JobRunner-queue, bestaand endpoint) — het id komt terug zodat de
	// melding en het opnieuw laden van het dossier bij de juiste rij landen.
	rescanSource: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		const id = String(form.get('id') ?? '');
		try {
			await adminApi(`/api/admin/scan?sourceId=${encodeURIComponent(id)}`, { method: 'POST' });
			return { ok: true, rescanned: id };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e), id });
		}
	}
};
