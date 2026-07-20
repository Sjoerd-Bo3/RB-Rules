import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { api, API_BASE } from '$lib/api';

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

export interface DeckCardFilter {
	canonicalId: string;
	name: string | null;
}

export interface DeckListResponse {
	items: DeckSummary[];
	total: number;
	page: number;
	pageSize: number;
	cardFilter: DeckCardFilter | null;
}

export interface DeckFacets {
	domains: string[];
}

/** Eén kaartregel uit een gedecodeerde deck-code (#264) — dezelfde vorm als
 * op de deckdetailpagina: niet-gekoppelde regels dragen alleen de rauwe
 * kaartcode. */
export interface DecodedCard {
	cardCode: string;
	quantity: number;
	canonicalRiftboundId: string | null;
	cardName: string | null;
	imageUrl: string | null;
}
export interface DecodedDeck {
	sections: { section: string; cards: DecodedCard[] }[];
	cardCount: number;
	unknownCount: number;
	legality: DeckLegality;
}

const EMPTY_FACETS: DeckFacets = { domains: [] };
const SORTS = ['recent', 'views', 'likes'] as const;
type Sort = (typeof SORTS)[number];
// Alleen bekende filterwaarden reizen door naar rb-api; een gemanipuleerde
// URL levert gewoon de ongefilterde lijst op, geen 400 (zelfde afspraak als
// het onderwerp-filter op /rulings).
const LEGALITIES = ['legal', 'illegal', 'incomplete'] as const;

export const load: PageServerLoad = async ({ url }) => {
	const domain = url.searchParams.get('domain') ?? '';
	const sortParam = url.searchParams.get('sort') ?? '';
	const sort: Sort = (SORTS as readonly string[]).includes(sortParam) ? (sortParam as Sort) : 'recent';
	const page = Math.max(1, Number(url.searchParams.get('page') ?? '1') || 1);
	// Deep-link vanaf de kaartpagina (#15 spoor B → A): filter op één kaart.
	const card = url.searchParams.get('card') ?? '';
	const legalityParam = url.searchParams.get('legality') ?? '';
	const legality = (LEGALITIES as readonly string[]).includes(legalityParam) ? legalityParam : '';
	const q = url.searchParams.get('q')?.trim() ?? '';

	const params = new URLSearchParams();
	if (domain) params.set('domain', domain);
	if (sort !== 'recent') params.set('sort', sort);
	if (page > 1) params.set('page', String(page));
	if (card) params.set('card', card);
	if (legality) params.set('legality', legality);
	if (q) params.set('q', q);

	try {
		const facetsPromise = api<DeckFacets>('/api/decks/facets').catch(() => EMPTY_FACETS);
		const result = await api<DeckListResponse>(`/api/decks?${params}`);
		return { domain, sort, page, card, legality, q, result, facets: await facetsPromise, error: null };
	} catch (e) {
		return {
			domain,
			sort,
			page,
			card,
			legality,
			q,
			result: { items: [], total: 0, page, pageSize: 24, cardFilter: null } as DeckListResponse,
			facets: EMPTY_FACETS,
			error: `Decks laden mislukt (${e instanceof Error ? e.message : e})`
		};
	}
};

export const actions: Actions = {
	// Deck-code-import (#264). De api()-helper gooit alleen "rb-api <status>",
	// maar juist de uitleg uit rb-api ("Ongeldig teken 'x' in de deck-code.")
	// is hier het nuttige antwoord — vandaar een directe fetch met het
	// Problem-detail eruit gehaald.
	decode: async ({ request, fetch }) => {
		const form = await request.formData();
		const code = String(form.get('code') ?? '').trim();
		if (!code) return fail(400, { code: '', decodeError: 'Plak eerst een deck-code.' });

		let res: Response;
		try {
			res = await fetch(`${API_BASE}/api/decks/decode`, {
				method: 'POST',
				headers: { 'content-type': 'application/json' },
				body: JSON.stringify({ code })
			});
		} catch (e) {
			return fail(502, {
				code,
				decodeError: `Deck-code lezen mislukt (${e instanceof Error ? e.message : e})`
			});
		}

		if (!res.ok) {
			const problem: unknown = await res.json().catch(() => null);
			const detail =
				problem && typeof problem === 'object' && 'detail' in problem && typeof problem.detail === 'string'
					? problem.detail
					: `Deck-code lezen mislukt (rb-api ${res.status}).`;
			return fail(res.status === 400 ? 400 : 502, { code, decodeError: detail });
		}

		return { code, decoded: (await res.json()) as DecodedDeck, decodeError: null };
	}
};
