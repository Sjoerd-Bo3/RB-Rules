import type { PageServerLoad } from './$types';
import { adminApi } from '$lib/server/admin';

const STATUSES = ['open', 'reviewed', 'resolved', 'dismissed'];

// Conflicts-verkenner (#236): reasoning-tegenspraken + routering (misconception/
// reviewqueue/escalation). Read-only; het misvattingen-kanaal is de subset met
// channel = misconception.
export const load: PageServerLoad = async ({ url }) => {
	const page = Math.min(100_000, Math.max(1, Math.trunc(Number(url.searchParams.get('page'))) || 1));
	const rawStatus = url.searchParams.get('status') ?? '';
	const status = STATUSES.includes(rawStatus) ? rawStatus : '';

	const qs = new URLSearchParams({ page: String(page) });
	if (status) qs.set('status', status);

	try {
		return { data: await adminApi<unknown>(`/api/admin/brein/conflicts?${qs}`), status, apiDown: false };
	} catch {
		return { data: null, status, apiDown: true };
	}
};
