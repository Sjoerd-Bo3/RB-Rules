import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface TocSource {
	sourceId: string;
	sourceName: string;
	sections: { code: string; preview: string }[];
}

export interface RuleSearchHit {
	id: number;
	sourceId: string;
	sectionCode: string;
	page: number | null;
	snippet: string;
	fileUrl: string | null;
}

export const load: PageServerLoad = async ({ url }) => {
	const q = url.searchParams.get('q')?.trim() ?? '';

	// Hybride zoeken (#72) parallel aan de boom; een zoekfout laat de boom
	// gewoon staan (fail-paden behouden de bestaande paginastate).
	const [tocRes, searchRes] = await Promise.allSettled([
		api<TocSource[]>('/api/rules/toc'),
		q
			? api<RuleSearchHit[]>(`/api/rules/search?q=${encodeURIComponent(q)}&limit=15`)
			: Promise.resolve(null)
	]);

	return {
		toc: tocRes.status === 'fulfilled' ? tocRes.value : ([] as TocSource[]),
		apiDown: tocRes.status === 'rejected',
		q,
		results: searchRes.status === 'fulfilled' ? searchRes.value : null,
		searchFailed: q !== '' && searchRes.status === 'rejected'
	};
};
