import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { API_BASE } from '$lib/api';

// On-demand deck-fetch voor de scorepad (#346): plakt een speler een
// Piltover-link die nog niet in de ingest zit, dan haalt POST /api/decks/fetch
// dat ene deck van de publieke /decks/view/{uuid}-pagina (robots-afspraak en
// rate-limit zitten in rb-api — hier alleen doorgeven). Zelfde patroon als
// /scorepad/decode: directe fetch in plaats van de api()-helper, want juist
// rb-api's Problem-detail ("Deck bestaat niet (meer) op Piltover Archive.")
// is hier het nuttige antwoord. rb-api plat is een verwacht pad: nette
// fout-body, nooit een exception naar buiten.

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

export const POST: RequestHandler = async ({ request, getClientAddress }) => {
	let id = '';
	try {
		const body: unknown = await request.json();
		if (body && typeof body === 'object' && 'id' in body && typeof body.id === 'string') {
			id = body.id.trim();
		}
	} catch {
		// Geen/kapotte JSON-body: valt door naar de lege-id-melding.
	}
	if (!id) return json({ error: 'Geen deck-id meegegeven.' }, { status: 400 });

	let res: Response;
	try {
		res = await fetch(`${API_BASE}/api/decks/fetch`, {
			method: 'POST',
			// X-Client-Ip is verplicht voor de 'deckfetch'-rate-limit (review
			// #346): zonder ziet rb-api alleen het interne compose-IP en wordt
			// "per bezoeker" één gedeelde bucket voor de hele site — zelfde
			// patroon als de andere rate-limited proxies.
			headers: { 'content-type': 'application/json', 'x-client-ip': getClientAddress() },
			body: JSON.stringify({ id })
		});
	} catch {
		return json({ error: 'Ophalen kan nu niet — rb-api is niet bereikbaar.' }, { status: 503 });
	}

	if (!res.ok) {
		const problem: unknown = await res.json().catch(() => null);
		const detail =
			problem && typeof problem === 'object' && 'detail' in problem && typeof problem.detail === 'string'
				? problem.detail
				: `Deck ophalen mislukt (rb-api ${res.status}).`;
		// 400 (geen uuid) en 404 (weg op PA) behouden hun betekenis voor de UI;
		// al het andere is upstream-storing.
		const status = res.status === 400 ? 400 : res.status === 404 ? 404 : 502;
		return json({ error: detail }, { status });
	}

	// Afslanken naar exact de vorm van /scorepad/decks/[id], zodat de pagina
	// beide antwoorden identiek kan verwerken (naam + secties voor de prefill).
	const deck = (await res.json()) as DeckDetailUpstream;
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
};
