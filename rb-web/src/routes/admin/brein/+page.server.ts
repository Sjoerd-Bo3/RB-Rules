import type { PageServerLoad } from './$types';
import { adminApi } from '$lib/server/admin';

// Brein-overzicht (#236): tegel-tellingen + observability-rollups. Read-only,
// twee parallelle fetches; brein-uitval → nette lege staat (apiDown).
export const load: PageServerLoad = async () => {
	try {
		const [counts, observability] = await Promise.all([
			adminApi<unknown>('/api/admin/brein/overzicht'),
			adminApi<unknown>('/api/admin/brein/observability')
		]);
		return { counts, observability, apiDown: false };
	} catch {
		return { counts: null, observability: null, apiDown: true };
	}
};
