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

export const load: PageServerLoad = async ({ cookies }) => {
	const isAdmin = authed(cookies);
	try {
		const changes = await api<Change[]>('/api/changes');
		return { changes, apiDown: false, isAdmin };
	} catch {
		return { changes: [] as Change[], apiDown: true, isAdmin };
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
