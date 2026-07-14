import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface DeckLegality {
	status: 'legal' | 'illegal' | 'incomplete';
	issues: { cardCode: string; cardName: string | null; reason: string }[];
	unknownCount: number;
}

export interface DeckSummary {
	id: string;
	name: string | null;
	domains: string[];
	cardCount: number;
	views: number;
	likes: number;
	sourceUrl: string;
	paUpdatedAt: string | null;
	legality: DeckLegality;
}

export interface DeckListResponse {
	items: DeckSummary[];
	total: number;
	page: number;
	pageSize: number;
}

export interface DeckFacets {
	domains: string[];
}

const EMPTY_FACETS: DeckFacets = { domains: [] };
const SORTS = ['recent', 'views', 'likes'] as const;
type Sort = (typeof SORTS)[number];

export const load: PageServerLoad = async ({ url }) => {
	const domain = url.searchParams.get('domain') ?? '';
	const sortParam = url.searchParams.get('sort') ?? '';
	const sort: Sort = (SORTS as readonly string[]).includes(sortParam) ? (sortParam as Sort) : 'recent';
	const page = Math.max(1, Number(url.searchParams.get('page') ?? '1') || 1);

	const params = new URLSearchParams();
	if (domain) params.set('domain', domain);
	if (sort !== 'recent') params.set('sort', sort);
	if (page > 1) params.set('page', String(page));

	try {
		const facetsPromise = api<DeckFacets>('/api/decks/facets').catch(() => EMPTY_FACETS);
		const result = await api<DeckListResponse>(`/api/decks?${params}`);
		return { domain, sort, page, result, facets: await facetsPromise, error: null };
	} catch (e) {
		return {
			domain,
			sort,
			page,
			result: { items: [], total: 0, page, pageSize: 24 } as DeckListResponse,
			facets: EMPTY_FACETS,
			error: `Decks laden mislukt (${e instanceof Error ? e.message : e})`
		};
	}
};
