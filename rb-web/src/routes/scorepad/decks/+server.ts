import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { api } from '$lib/api';

// Deck-zoeklijst voor de scorepad-assembler (#344). De browser praat nooit
// rechtstreeks met rb-api; deze proxy geeft alleen de compacte velden door
// die de deck-kiezer toont. rb-api plat is een verwacht pad: 503 met een
// nette melding, nooit een exception naar buiten.

interface DeckListUpstream {
	items: { id: string; name: string | null; domains: string[]; cardCount: number }[];
	total: number;
	page: number;
	pageSize: number;
}

export const GET: RequestHandler = async ({ url }) => {
	const q = url.searchParams.get('q')?.trim() ?? '';
	const page = Math.max(1, Number(url.searchParams.get('page') ?? '1') || 1);

	const params = new URLSearchParams();
	if (q) params.set('q', q);
	if (page > 1) params.set('page', String(page));
	const qs = params.toString();

	try {
		const result = await api<DeckListUpstream>(`/api/decks${qs ? `?${qs}` : ''}`);
		return json({
			items: result.items.map((d) => ({
				id: d.id,
				name: d.name,
				domains: d.domains,
				cardCount: d.cardCount
			})),
			total: result.total,
			page: result.page,
			pageSize: result.pageSize
		});
	} catch {
		return json({ error: 'Decks zoeken lukt nu niet — rb-api is niet bereikbaar.' }, { status: 503 });
	}
};
