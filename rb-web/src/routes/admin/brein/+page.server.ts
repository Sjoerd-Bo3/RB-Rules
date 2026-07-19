import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { adminApi, authed } from '$lib/server/admin';

// Brein-overzicht (#236) + cockpit (brein-jobs-ui): tegel-tellingen +
// observability-rollups + de operationele cockpit (per-stap-tellingen,
// laatste-run per brein-job, /ask-retrieval-flag). Read-only, parallelle
// fetches; brein-uitval → nette lege staat (apiDown).
export const load: PageServerLoad = async () => {
	try {
		const [counts, observability, cockpit] = await Promise.all([
			adminApi<unknown>('/api/admin/brein/overzicht'),
			adminApi<unknown>('/api/admin/brein/observability'),
			adminApi<unknown>('/api/admin/brein/cockpit')
		]);
		return { counts, observability, cockpit, apiDown: false };
	} catch {
		return { counts: null, observability: null, cockpit: null, apiDown: true };
	}
};

export const actions: Actions = {
	// Brein-jobs triggeren (brein-jobs-ui): zelfde patroon/409-afhandeling als de
	// job-action op /admin — één job tegelijk (JobRunner-gate). De naam is een
	// hidden field; onbekende jobs geeft rb-api 404 (net zo netjes afgevangen).
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
	}
};
