import type { PageLoad } from './$types';
import { parseOptions } from '$lib/scorepad';

// Volledig client-side feature (#342): geen rb-api nodig. De samenstelling
// leeft in de query-string, zodat een pad-configuratie deelbaar en
// bookmarkbaar is — en headless te printen voor voorbeeld-PDF's.
export const load: PageLoad = ({ url }) => ({ options: parseOptions(url.searchParams) });
