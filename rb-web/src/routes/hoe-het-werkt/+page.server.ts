import type { PageServerLoad } from './$types';
import { api } from '$lib/api';
import type { PublicStats } from '../+page.server';

// Uitlegpagina (#379): bedoeld om aan anderen te laten zien hoe Poracle van
// bron naar antwoord komt. Twee dingen komen LIVE uit rb-api zodat de uitleg
// niet naast de werkelijkheid kan gaan staan:
//   1. de telstanden (hoeveel er echt in zit),
//   2. de ontologie — klassen, relaties en de afgeleide relaties, rechtstreeks
//      uit OntologySchema (de ÉNE schema-bron).
// Beide best-effort: de uitleg zelf moet leesbaar blijven als rb-api hapert.

export interface OntologyRelation {
	edge: string;
	van: string[];
	naar: string[];
	kardinaliteit: string;
	gereificeerd: boolean;
}

export interface OntologyClass {
	naam: string;
	ouders: string[];
	omschrijving: string;
}

export interface OntologyInference {
	edge: string;
	naam: string;
	familie: string;
	omschrijving: string;
}

export interface Ontology {
	versie: string;
	klassen: OntologyClass[];
	relaties: OntologyRelation[];
	disjuncteParen: string[];
	inferenties: OntologyInference[];
}

const EMPTY_STATS: PublicStats = { cards: 0, verifiedRulings: 0, bans: 0, recentChanges: 0 };

export const load: PageServerLoad = async () => {
	const [stats, ontology] = await Promise.all([
		api<PublicStats>('/api/stats').catch(() => EMPTY_STATS),
		api<Ontology>('/api/brain/ontologie').catch(() => null)
	]);
	return { stats, ontology };
};
