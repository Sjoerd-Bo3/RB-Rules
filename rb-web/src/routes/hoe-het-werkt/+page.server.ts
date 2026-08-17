import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface Ontology {
	identityRule: string;
	nodeTypes: string[];
	edges: {
		type: string;
		from: string;
		to: string;
		description: string;
		inferred: boolean;
	}[];
}

interface Counts {
	cards: number;
	cardsEmbedded: number;
	ruleChunks: number;
	bans: number;
	errata: number;
	interactions: number;
}

/** De uitlegpagina toont de échte ontologie en actuele cijfers, niet een
 * met de hand bijgehouden kopie — zo veroudert de uitleg niet. */
export const load: PageServerLoad = async () => {
	const [ontology, stats] = await Promise.all([
		api<Ontology>('/api/graph/ontology').catch(() => null),
		api<{ count: number; medianMs?: number }>('/api/ask/stats').catch(() => null)
	]);

	// Publieke tellers: afgeleid uit bestaande publieke endpoints, zodat deze
	// pagina geen beheerrechten nodig heeft.
	let counts: Partial<Counts> = {};
	try {
		const [bans, facets] = await Promise.all([
			api<unknown[]>('/api/bans'),
			api<{ mechanics: string[]; sets: unknown[] }>('/api/cards/facets')
		]);
		counts = { bans: bans.length };
		return { ontology, stats, counts, facets };
	} catch {
		return { ontology, stats, counts, facets: null };
	}
};
