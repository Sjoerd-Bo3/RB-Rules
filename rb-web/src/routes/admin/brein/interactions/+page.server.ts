import type { PageServerLoad } from './$types';
import { adminApi } from '$lib/server/admin';

const STATUSES = ['candidate', 'verified', 'promoted', 'rejected', 'model_hypothesized_unruled'];

// Interacties-verkenner (#236): gereïficeerde interacties + condities + tier +
// provenance-keten. De keten wordt server-side opgehaald voor de geselecteerde
// interactie (?sel=interaction:ID) — read-only, geen client-fetch.
export const load: PageServerLoad = async ({ url }) => {
	const page = Math.min(100_000, Math.max(1, Math.trunc(Number(url.searchParams.get('page'))) || 1));
	const rawStatus = url.searchParams.get('status') ?? '';
	const status = STATUSES.includes(rawStatus) ? rawStatus : '';
	const sel = url.searchParams.get('sel')?.trim() ?? '';

	const qs = new URLSearchParams({ page: String(page) });
	if (status) qs.set('status', status);

	try {
		const dataset = await adminApi<unknown>(`/api/admin/brein/interactions?${qs}`);
		let chain: unknown = null;
		if (sel) {
			try {
				chain = await adminApi<unknown>(`/api/admin/brein/assertions/${encodeURIComponent(sel)}`);
			} catch {
				chain = null;
			}
		}
		return { data: dataset, chain, sel, status, apiDown: false };
	} catch {
		return { data: null, chain: null, sel, status, apiDown: true };
	}
};
