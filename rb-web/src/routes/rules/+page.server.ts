import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface TocSource {
	sourceId: string;
	sourceName: string;
	sections: { code: string; preview: string }[];
}

export const load: PageServerLoad = async () => {
	try {
		const toc = await api<TocSource[]>('/api/rules/toc');
		return { toc, apiDown: false };
	} catch {
		return { toc: [] as TocSource[], apiDown: true };
	}
};
