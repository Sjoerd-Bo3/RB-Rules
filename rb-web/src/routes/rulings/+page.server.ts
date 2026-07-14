import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

// Publieke rulings-databank (#127). Types bij de load die ze gebruikt;
// de browser praat nooit direct met rb-api — deze load is de proxy.
export interface RulingsSectionRef {
	sourceId: string;
	code: string;
}
export interface RulingsSource {
	name: string;
	url: string | null;
	quote: string | null;
	trustTier: number;
}
export interface RulingsItem {
	ref: string;
	kind: 'ruling' | 'claim';
	topic: 'card' | 'mechanic' | 'section' | 'concept' | 'answer';
	topicRef: string | null;
	/** Kaart-onderwerp, geresolved naar de canonieke printing — klikbaar. */
	cardId: string | null;
	question: string | null;
	text: string;
	trustLabel: string;
	provenance: string | null;
	date: string;
	score: number | null;
	sections: RulingsSectionRef[];
	sources: RulingsSource[];
	/** "Waar besloten" (#166) — URL of vrije citatie; alleen op rulings. */
	sourceRef: string | null;
}
interface RulingsResponse {
	items: RulingsItem[];
	total: number;
	page: number;
	pageSize: number;
	degraded: boolean;
}

// Het onderwerp-vocabulaire van RulingsTopics (rb-api); de labels leven in
// +page.svelte. Geen export — +page.server.ts staat alleen load/actions toe.
const TOPIC_KEYS = ['card', 'mechanic', 'section', 'concept', 'answer'];

export const load: PageServerLoad = async ({ url }) => {
	const q = url.searchParams.get('q')?.trim() ?? '';
	const topicRaw = url.searchParams.get('topic')?.trim().toLowerCase() ?? '';
	// Alleen bekende filterwaarden doorsturen — een gemanipuleerde URL levert
	// gewoon de ongefilterde databank op, geen 400.
	const topic = TOPIC_KEYS.includes(topicRaw) ? topicRaw : '';
	const page = Math.max(1, Number(url.searchParams.get('page')) || 1);

	const params = new URLSearchParams();
	if (q) params.set('q', q);
	if (topic) params.set('topic', topic);
	if (page > 1) params.set('page', String(page));

	let data: RulingsResponse | null = null;
	let apiDown = false;
	try {
		data = await api<RulingsResponse>(`/api/rulings?${params}`);
	} catch {
		apiDown = true;
	}

	return {
		q,
		topic,
		page,
		items: data?.items ?? [],
		total: data?.total ?? 0,
		pageSize: data?.pageSize ?? 20,
		degraded: data?.degraded ?? false,
		searching: q !== '',
		apiDown
	};
};
