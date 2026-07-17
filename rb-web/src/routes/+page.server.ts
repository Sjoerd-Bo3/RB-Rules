import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { api } from '$lib/api';
import { adminApi, authed } from '$lib/server/admin';

/** Bevestiging (#206): een secundaire change (andere bron, zelfde
 *  gebeurtenis) genest onder de primaire — SourceUrl is Source.Url, een
 *  geregistreerde bron-kolom (zelfde vertrouwen als sourceUrl hieronder,
 *  geen aparte UrlGuard-sanitize nodig). */
export interface ChangeConfirmation {
	id: number;
	sourceId: string;
	sourceName: string;
	sourceUrl: string;
	trustTier: number;
	summary: string | null;
	detectedAt: string;
}

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
	/** #206: leeg tenzij andere bronnen hetzelfde gebeurtenis bevestigden. */
	confirmedBy: ChangeConfirmation[];
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
