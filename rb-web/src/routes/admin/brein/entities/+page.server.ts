import type { PageServerLoad } from './$types';
import { adminApi } from '$lib/server/admin';

const KINDS = ['mechanic', 'keyword', 'concept'];
const STATUSES = ['candidate', 'canonical', 'merged'];

// Entiteiten-verkenner (#236): canonieke entiteiten + alias-lexicon + merge-status.
export const load: PageServerLoad = async ({ url }) => {
	const page = Math.min(100_000, Math.max(1, Math.trunc(Number(url.searchParams.get('page'))) || 1));
	const rawKind = url.searchParams.get('kind') ?? '';
	const rawStatus = url.searchParams.get('status') ?? '';
	const kind = KINDS.includes(rawKind) ? rawKind : '';
	const status = STATUSES.includes(rawStatus) ? rawStatus : '';

	const qs = new URLSearchParams({ page: String(page) });
	if (kind) qs.set('kind', kind);
	if (status) qs.set('status', status);

	try {
		return { data: await adminApi<unknown>(`/api/admin/brein/entities?${qs}`), kind, status, apiDown: false };
	} catch {
		return { data: null, kind, status, apiDown: true };
	}
};
