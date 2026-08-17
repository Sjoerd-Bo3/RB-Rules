import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface GraphNode {
	id: string;
	label: string;
}

export interface GraphData {
	center: GraphNode;
	domains: string[];
	mechanics: { mechanic: string; cards: GraphNode[] }[];
	/** Afgeleide kaart↔regel-relaties (GOVERNED_BY) — alleen uit Neo4j. */
	rules: { code: string; snippet: string | null; via: string | null }[];
	interactions: { otherId: string; otherName: string; kind: string }[];
	facts: { kind: string; label: string }[];
	/** Welke engine antwoordde: 'neo4j' (volledige graaf) of 'postgres'. */
	source: string;
}

export const load: PageServerLoad = async ({ url }) => {
	const card = url.searchParams.get('card');
	const q = url.searchParams.get('q')?.trim() ?? '';

	// Zonder kaart: zoekmodus. Met q: kandidaten tonen om te kiezen.
	let candidates: { riftboundId: string; name: string; imageUrl: string | null }[] = [];
	if (!card && q) {
		try {
			candidates = await api<{ riftboundId: string; name: string; imageUrl: string | null }[]>(
				`/api/cards?q=${encodeURIComponent(q)}`
			);
		} catch {
			candidates = [];
		}
	}

	let graph: GraphData | null = null;
	let error: string | null = null;
	if (card) {
		try {
			graph = await api<GraphData>(`/api/graph/neighbors?card=${encodeURIComponent(card)}`);
		} catch {
			error = 'Kaart niet gevonden of rb-api niet bereikbaar.';
		}
	}
	return { card, q, candidates, graph, error };
};
