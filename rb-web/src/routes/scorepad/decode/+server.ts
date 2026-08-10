import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { API_BASE } from '$lib/api';

// Deck-code-decode voor de scorepad-prefill (#344). Zelfde patroon als de
// decode-action op /decks (#264): directe fetch in plaats van de api()-helper,
// want juist de uitleg uit rb-api's Problem-detail ("Ongeldig teken 'x' in de
// deck-code.") is hier het nuttige antwoord. rb-api plat is een verwacht pad:
// 503 met een nette melding, geen exception naar buiten.

export const POST: RequestHandler = async ({ request }) => {
	let code = '';
	try {
		const body: unknown = await request.json();
		if (body && typeof body === 'object' && 'code' in body && typeof body.code === 'string') {
			code = body.code.trim();
		}
	} catch {
		// Geen/kapotte JSON-body: valt door naar de lege-code-melding.
	}
	if (!code) return json({ error: 'Plak eerst een deck-code.' }, { status: 400 });

	let res: Response;
	try {
		res = await fetch(`${API_BASE}/api/decks/decode`, {
			method: 'POST',
			headers: { 'content-type': 'application/json' },
			body: JSON.stringify({ code })
		});
	} catch {
		return json({ error: 'Deck-code lezen lukt nu niet — rb-api is niet bereikbaar.' }, { status: 503 });
	}

	if (!res.ok) {
		const problem: unknown = await res.json().catch(() => null);
		const detail =
			problem && typeof problem === 'object' && 'detail' in problem && typeof problem.detail === 'string'
				? problem.detail
				: `Deck-code lezen mislukt (rb-api ${res.status}).`;
		return json({ error: detail }, { status: res.status === 400 ? 400 : 502 });
	}

	// DecodedDeck gaat één-op-één door: sections[] heeft precies de vorm die
	// fromDeckSections eet, en cardCount/unknownCount voeden de melding in de UI.
	return json(await res.json());
};
