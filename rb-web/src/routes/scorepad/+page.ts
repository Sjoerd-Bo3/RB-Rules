import type { PageLoad } from './$types';
import { parseOptions } from '$lib/scorepad';
import { fromDeckSections, type DeckSectionInput } from '$lib/deckPrefill';

/** Waarom het gekoppelde deck er niet is: 'notfound' = de id staat niet in de
 *  ingest, 'unavailable' = rb-api is niet bereikbaar. */
export type DeckLoadError = 'notfound' | 'unavailable';

// De samenstelling leeft in de query-string (#342): deelbaar, bookmarkbaar en
// headless te printen. Sinds #344 kan er een deck-id in meereizen ('deck='):
// dan haalt de load het deck op via de eigen /scorepad/decks/[id]-proxy — een
// RELATIEVE fetch met de load-fetch, zodat het in SSR én client werkt en de
// browser nooit rechtstreeks met rb-api praat. Mislukt dat (rb-api plat,
// onbekende id), dan blijft de pagina volledig werken met blanco vellen:
// deck null plus een foutvlag voor de nette melding.
export const load: PageLoad = async ({ url, fetch }) => {
	const options = parseOptions(url.searchParams);
	if (options.deckId === null) return { options, deck: null, deckError: null };
	try {
		const res = await fetch(`/scorepad/decks/${options.deckId}`);
		if (!res.ok) {
			const deckError: DeckLoadError = res.status === 404 ? 'notfound' : 'unavailable';
			return { options, deck: null, deckError };
		}
		const d = (await res.json()) as { name: string | null; sections: DeckSectionInput[] };
		return { options, deck: fromDeckSections(d.name, d.sections), deckError: null };
	} catch {
		const deckError: DeckLoadError = 'unavailable';
		return { options, deck: null, deckError };
	}
};
