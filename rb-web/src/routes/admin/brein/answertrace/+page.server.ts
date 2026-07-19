import type { PageServerLoad } from './$types';
import { adminApi } from '$lib/server/admin';

// AnswerTrace-viewer (#236): recente traces + het herspeelbare detail (?id=ULID).
// Read-only; de detail toont de dragende subgraaf/paden + trust-toen + epoch.
export const load: PageServerLoad = async ({ url }) => {
	const id = url.searchParams.get('id')?.trim() ?? '';
	try {
		const list = await adminApi<unknown>('/api/admin/brein/answertraces');
		let detail: unknown = null;
		if (id) {
			try {
				detail = await adminApi<unknown>(`/api/admin/brein/answertrace/${encodeURIComponent(id)}`);
			} catch {
				detail = null;
			}
		}
		return { list, detail, id, apiDown: false };
	} catch {
		return { list: null, detail: null, id, apiDown: true };
	}
};
