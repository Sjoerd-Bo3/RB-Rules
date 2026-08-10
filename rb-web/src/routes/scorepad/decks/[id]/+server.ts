import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { api } from '$lib/api';

// Deck-detail voor de prefill van de deck-/registration-vellen (#344). Alleen
// wat fromDeckSections eet (naam + secties met code/aantal/naam) plus wat
// context; de volledige DeckDetail blijft op /decks/[id]. rb-api plat is een
// verwacht pad: nette fout-body, geen exception naar buiten.

interface DeckDetailUpstream {
	id: string;
	name: string | null;
	domains: string[];
	sourceUrl: string;
	sections: {
		section: string;
		cards: { cardCode: string; quantity: number; cardName: string | null }[];
	}[];
}

/** De api()-helper gooit `rb-api <status>: <pad>` — status terugwinnen zodat
 *  een onbekend deck een 404 blijft en een platte api een 503 wordt. */
function apiStatus(e: unknown): number | null {
	const m = e instanceof Error ? /^rb-api (\d{3}):/.exec(e.message) : null;
	return m ? Number(m[1]) : null;
}

export const GET: RequestHandler = async ({ params }) => {
	try {
		const deck = await api<DeckDetailUpstream>(`/api/decks/${encodeURIComponent(params.id)}`);
		return json({
			id: deck.id,
			name: deck.name,
			domains: deck.domains,
			sourceUrl: deck.sourceUrl,
			sections: deck.sections.map((s) => ({
				section: s.section,
				cards: s.cards.map((c) => ({
					cardCode: c.cardCode,
					quantity: c.quantity,
					cardName: c.cardName
				}))
			}))
		});
	} catch (e) {
		if (apiStatus(e) === 404) return json({ error: 'Deck niet gevonden.' }, { status: 404 });
		return json({ error: 'Deck laden lukt nu niet — rb-api is niet bereikbaar.' }, { status: 503 });
	}
};
