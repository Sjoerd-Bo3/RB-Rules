import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface CardHit {
	riftboundId: string;
	name: string;
	type: string | null;
	supertype: string | null;
	rarity: string | null;
	domains: string[];
	energy: number | null;
	might: number | null;
	setId: string | null;
	textPlain: string | null;
	imageUrl: string | null;
	distance?: number;
	variants?: number;
}

export interface Facets {
	sets: { id: string; label: string }[];
	types: string[];
	rarities: string[];
	domains: string[];
	mechanics: string[];
}

const EMPTY_FACETS: Facets = { sets: [], types: [], rarities: [], domains: [], mechanics: [] };
const FILTER_KEYS = ['domain', 'type', 'set', 'rarity', 'mechanic', 'maxEnergy'] as const;

export const load: PageServerLoad = async ({ url }) => {
	const q = url.searchParams.get('q')?.trim() ?? '';
	const filters = Object.fromEntries(
		FILTER_KEYS.map((k) => [k, url.searchParams.get(k) ?? ''])
	) as Record<(typeof FILTER_KEYS)[number], string>;

	const params = new URLSearchParams();
	for (const k of FILTER_KEYS) if (filters[k]) params.set(k, filters[k]);

	try {
		const facetsPromise = api<Facets>('/api/cards/facets').catch(() => EMPTY_FACETS);
		let results: CardHit[];
		let mode: 'semantic' | 'browse';
		if (q) {
			params.set('q', q);
			results = await api<CardHit[]>(`/api/cards/search?${params}`);
			mode = 'semantic';
		} else {
			results = await api<CardHit[]>(`/api/cards?${params}`);
			mode = 'browse';
		}
		return { q, filters, results, mode, facets: await facetsPromise, error: null };
	} catch (e) {
		return {
			q,
			filters,
			results: [] as CardHit[],
			mode: 'browse' as const,
			facets: EMPTY_FACETS,
			error: `Kaarten laden mislukt (${e instanceof Error ? e.message : e})`
		};
	}
};
