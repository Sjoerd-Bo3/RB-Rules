import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { api } from '$lib/api';
import { adminApi, authed } from '$lib/server/admin';

export interface Change {
	id: number;
	sourceId: string;
	changeType: string;
	severity: string;
	summary: string | null;
	meaning: string | null;
	diff: string | null;
	detectedAt: string;
	sourceName: string;
	sourceUrl: string;
	trustTier: number;
}

/** Aankomende set (#52): bekend via de releasedatum uit de kaart-sync. */
export interface UpcomingSet {
	setId: string;
	name: string;
	publishedOn: string;
	cardCount: number | null;
}

export const load: PageServerLoad = async ({ cookies }) => {
	const isAdmin = authed(cookies);
	try {
		const [changes, upcoming] = await Promise.all([
			api<Change[]>('/api/changes'),
			// Signaal, geen kernfunctie: zonder dit blijft de feed gewoon werken.
			api<UpcomingSet[]>('/api/sets/upcoming').catch(() => [] as UpcomingSet[])
		]);
		return { changes, upcoming, apiDown: false, isAdmin };
	} catch {
		return { changes: [] as Change[], upcoming: [] as UpcomingSet[], apiDown: true, isAdmin };
	}
};

export const actions: Actions = {
	// Feed-curatie voor ingelogde beheerders: ruis weghalen.
	delete: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/changes/${form.get('id')}`, { method: 'DELETE' });
			return { ok: true };
		} catch (e) {
			return fail(500, { error: e instanceof Error ? e.message : String(e) });
		}
	}
};
