import type { PageLoad } from './$types';
import { parseOptions } from '$lib/scorepad';
import { fromDeckSections, type DeckSectionInput } from '$lib/deckPrefill';

/** Waarom een gekoppeld deck er niet is: 'notfound' = de id staat niet in de
 *  ingest, 'unavailable' = rb-api is niet bereikbaar. */
export type DeckLoadError = 'notfound' | 'unavailable';

/** Laad-resultaat per ingest-bron uit de URL: precies één van deck/error is
 *  gevuld — een gefaalde bron laat de andere bronnen (en de pagina) met rust. */
export interface LoadedDeck {
	id: string;
	deck: ReturnType<typeof fromDeckSections> | null;
	error: DeckLoadError | null;
}

// De samenstelling leeft in de query-string (#342): deelbaar, bookmarkbaar en
// headless te printen. Sinds #346 kunnen er meerdere deck-ids in meereizen
// ('decks='; het oude 'deck=' blijft een parse-alias): de load haalt ze
// PARALLEL op via de eigen /scorepad/decks/[id]-proxy — een RELATIEVE fetch
// met de load-fetch, zodat het in SSR én client werkt en de browser nooit
// rechtstreeks met rb-api praat. Mislukt er één (rb-api plat, onbekende id),
// dan krijgt alleen díe bron een foutvlag en blijven zijn vellen blanco — de
// pagina werkt altijd volledig door.
export const load: PageLoad = async ({ url, fetch }) => {
	const options = parseOptions(url.searchParams);
	// De parser dedupliceert en plafonneert options.decks al (MAX_SHEETS,
	// review #346) — élke id hier is dus uniek en de fan-out is begrensd.
	const decks: LoadedDeck[] = await Promise.all(
		options.decks.map(async (id): Promise<LoadedDeck> => {
			try {
				const res = await fetch(`/scorepad/decks/${id}`);
				if (!res.ok) {
					return { id, deck: null, error: res.status === 404 ? 'notfound' : 'unavailable' };
				}
				const d = (await res.json()) as { name: string | null; sections: DeckSectionInput[] };
				return { id, deck: fromDeckSections(d.name, d.sections), error: null };
			} catch {
				return { id, deck: null, error: 'unavailable' };
			}
		})
	);
	return { options, decks };
};
