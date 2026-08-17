import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface GraphData {
	center: { riftboundId: string; name: string; imageUrl: string | null; domains: string[] };
	mechanics: { mechanic: string; cards: { riftboundId: string; name: string; imageUrl: string | null }[] }[];
	interactions: { otherId: string; otherName: string; kind: string }[];
}

// Brein-API-responses (#108, contracten uit docs/BRAIN.md §2.3). Types bij
// de load die ze gebruikt; de browser praat nooit direct met rb-api — deze
// load is de proxy.
export interface BrainSearchItem {
	ref: string;
	layer: string;
	title: string | null;
	snippet: string | null;
	score: number;
	trustLabel: string;
}
interface BrainSearchResponse {
	results: BrainSearchItem[];
	degraded: boolean;
}
export interface BrainNode {
	ref: string;
	kind: string;
	layer: string;
	trustLabel: string;
	props: Record<string, unknown>;
}
export interface BrainNeighbor {
	ref: string;
	name: string | null;
	edge: string;
	richting: string;
	props: Record<string, unknown> | null;
}
interface BrainNeighborsResponse {
	ref: string;
	neighbors: BrainNeighbor[];
}
export interface BrainView {
	node: BrainNode;
	neighbors: BrainNeighbor[];
	/** Neo4j-degradatie of ref-zonder-graph-knoop: de knoop (Postgres) blijft tonen. */
	graphError: string | null;
}

/** De api()-helper gooit `rb-api <status>: <pad>` — hier de status terugwinnen
 * zodat 404 (onbekende ref) en 503 (graph plat) elk hun eigen boodschap krijgen. */
function apiStatus(e: unknown): number | null {
	const m = e instanceof Error ? /^rb-api (\d{3}):/.exec(e.message) : null;
	return m ? Number(m[1]) : null;
}

export const load: PageServerLoad = async ({ url }) => {
	const card = url.searchParams.get('card');
	const ref = url.searchParams.get('ref')?.trim() || null;
	const q = url.searchParams.get('q')?.trim() ?? '';

	// Zonder kaart/ref: zoekmodus. Kaart-kandidaten (bestaand) en brein-zoek
	// over alle kennislagen (#108) parallel; beide best-effort.
	let candidates: { riftboundId: string; name: string; imageUrl: string | null }[] = [];
	let brainResults: BrainSearchItem[] = [];
	let brainDegraded = false;
	if (!card && !ref && q) {
		const [cardsR, brainR] = await Promise.allSettled([
			api<{ riftboundId: string; name: string; imageUrl: string | null }[]>(
				`/api/cards?q=${encodeURIComponent(q)}`
			),
			api<BrainSearchResponse>(`/api/brain/search?q=${encodeURIComponent(q)}&take=5`)
		]);
		if (cardsR.status === 'fulfilled') candidates = cardsR.value;
		if (brainR.status === 'fulfilled') {
			brainResults = brainR.value.results;
			brainDegraded = brainR.value.degraded;
		}
	}

	// Kaart-verkenning (bestaand pad, ongewijzigd).
	let graph: GraphData | null = null;
	let error: string | null = null;
	if (card) {
		try {
			graph = await api<GraphData>(`/api/graph/neighbors?card=${encodeURIComponent(card)}`);
		} catch {
			error = 'Kaart niet gevonden of rb-api niet bereikbaar.';
		}
	}

	// Brein-verkenning (#108): knoop (Postgres) + buren (Neo4j) parallel.
	// Fouten per kant: zonder Neo4j blijft de knoop gewoon leesbaar.
	let brain: BrainView | null = null;
	let brainError: string | null = null;
	if (!card && ref) {
		const enc = encodeURIComponent(ref);
		const [nodeR, nbR] = await Promise.allSettled([
			api<BrainNode>(`/api/brain/node/${enc}`),
			api<BrainNeighborsResponse>(`/api/brain/neighbors/${enc}?take=24`)
		]);
		if (nodeR.status === 'fulfilled') {
			let neighbors: BrainNeighbor[] = [];
			let graphError: string | null = null;
			if (nbR.status === 'fulfilled') {
				neighbors = nbR.value.neighbors;
			} else {
				const status = apiStatus(nbR.reason);
				graphError =
					status === 404
						? 'Deze knoop staat (nog) niet in de kennisgraaf — draai de graph-job in het beheer.'
						: status === 400
							? 'Deze knoopsoort heeft geen graph-buren (dynamische relaties zijn altijd edges, nooit een eigen knoop).'
							: 'Graph niet beschikbaar — buren vragen een draaiende Neo4j; de knoop zelf komt uit Postgres.';
			}
			brain = { node: nodeR.value, neighbors, graphError };
		} else {
			const status = apiStatus(nodeR.reason);
			brainError =
				status === 404
					? `Onbekende ref: ${ref}`
					: status === 400
						? `Ongeldige ref: ${ref} — verwacht kind:key, bv. mechanic:Deflect of section:core-rules-pdf/101.2`
						: 'rb-api niet bereikbaar.';
		}
	}

	return { card, ref, q, candidates, brainResults, brainDegraded, graph, error, brain, brainError };
};
