import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { adminApi, authed } from '$lib/server/admin';

// Brein-overzicht (#236) + cockpit (brein-jobs-ui): tegel-tellingen +
// observability-rollups + de operationele cockpit (per-stap-tellingen,
// laatste-run per brein-job, /ask-retrieval-flag) + de beheerde instellingen
// (#254: de vlaggen die vroeger alleen via de VM-.env te zetten waren).
// Read-only, parallelle fetches; brein-uitval → nette lege staat (apiDown).
export const load: PageServerLoad = async () => {
	try {
		const [counts, observability, cockpit, settings] = await Promise.all([
			adminApi<unknown>('/api/admin/brein/overzicht'),
			adminApi<unknown>('/api/admin/brein/observability'),
			adminApi<unknown>('/api/admin/brein/cockpit'),
			adminApi<unknown[]>('/api/admin/settings').catch(() => [])
		]);
		return { counts, observability, cockpit, settings, apiDown: false };
	} catch {
		return { counts: null, observability: null, cockpit: null, settings: [], apiDown: true };
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
	},

	// Beheerde instellingen (#254): één of meer sleutels tegelijk. De key/value-
	// velden komen paarsgewijs binnen (getAll), zodat het nachtvenster als geheel
	// gaat — rb-api beoordeelt start en eind samen en schrijft alles-of-niets. Een
	// lege waarde betekent "terug naar de standaard". rb-api weigert een onmogelijke
	// combinatie met uitleg; die tonen we letterlijk.
	setting: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		const keys = form.getAll('key').map(String);
		const values = form.getAll('value').map(String);
		if (keys.length === 0) return fail(400, { error: 'Geen instelling opgegeven' });
		try {
			await adminApi('/api/admin/settings', {
				method: 'POST',
				body: JSON.stringify({
					changes: keys.map((key, i) => ({ key, value: values[i] ?? '' })),
					actor: 'beheer'
				})
			});
			return { settingSaved: keys[0] };
		} catch (e) {
			return fail(400, { error: e instanceof Error ? e.message : String(e) });
		}
	}
};
