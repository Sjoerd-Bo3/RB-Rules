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
}

export const load: PageServerLoad = async ({ url }) => {
	const q = url.searchParams.get('q')?.trim() ?? '';
	const domain = url.searchParams.get('domain') ?? '';
	const maxEnergy = url.searchParams.get('maxEnergy') ?? '';

	if (!q) return { q, domain, maxEnergy, results: [] as CardHit[], error: null };

	try {
		const params = new URLSearchParams({ q });
		if (domain) params.set('domain', domain);
		if (maxEnergy) params.set('maxEnergy', maxEnergy);
		const results = await api<CardHit[]>(`/api/cards/search?${params}`);
		return { q, domain, maxEnergy, results, error: null };
	} catch (e) {
		return {
			q, domain, maxEnergy,
			results: [] as CardHit[],
			error: `Zoeken mislukt — zijn de embeddings al opgebouwd? (${e instanceof Error ? e.message : e})`
		};
	}
};
