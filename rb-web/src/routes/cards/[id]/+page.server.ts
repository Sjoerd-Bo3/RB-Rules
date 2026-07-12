import { error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface CardDetail {
	riftboundId: string;
	name: string;
	type: string | null;
	supertype: string | null;
	rarity: string | null;
	domains: string[];
	energy: number | null;
	might: number | null;
	power: number | null;
	setId: string | null;
	setLabel: string | null;
	collectorNumber: number | null;
	textPlain: string | null;
	imageUrl: string | null;
	tags: string[];
	mechanics: string[] | null;
	triggers: string[] | null;
	effects: string[] | null;
	banned: boolean;
	errataText: string | null;
	variantOf: string | null;
	/** Set-legaliteit (#22): releasedatum van de set (yyyy-mm-dd) of null. */
	legalFrom: string | null;
	legality: 'legal' | 'upcoming' | 'announced';
	versions: {
		riftboundId: string;
		setId: string | null;
		setLabel: string | null;
		rarity: string | null;
		collectorNumber: number | null;
		imageUrl: string | null;
	}[];
}

interface Interaction {
	otherId: string;
	otherName: string;
	kind: string;
	explanation: string;
}

interface SimilarCard {
	riftboundId: string;
	name: string;
	type: string | null;
	domains: string[];
	energy: number | null;
	might: number | null;
	imageUrl: string | null;
	similarity: number;
	sharedMechanics: string[];
	sharedDomains: string[];
	sameType: boolean;
}

interface CardRules {
	errata: { newText: string; sourceUrl: string | null; detectedAt: string }[];
	relevantRules: { section: string; snippet: string; sourceName: string; url: string }[];
}

// Kaart-dossier (#127): rulings, claims, brein-relaties en ban-historie.
export interface CardDossier {
	rulings: {
		id: number;
		question: string | null;
		text: string;
		provenance: string | null;
		date: string;
		sections: { sourceId: string; code: string }[];
	}[];
	claims: {
		id: number;
		statement: string;
		corroboration: number;
		trustScore: number;
		officialStatus: string;
		trustLabel: string;
		sources: { name: string; url: string | null; quote: string | null; trustTier: number }[];
	}[];
	relations: {
		otherRef: string;
		otherName: string | null;
		kind: string;
		explanation: string;
		status: string;
		trust: number;
		richting: string;
	}[];
	banHistory: {
		kind: string;
		format: string;
		effectiveFrom: string | null;
		sourceUrl: string;
		detectedAt: string;
	}[];
}

const EMPTY_DOSSIER: CardDossier = { rulings: [], claims: [], relations: [], banHistory: [] };

export const load: PageServerLoad = async ({ params }) => {
	let card: CardDetail;
	try {
		card = await api<CardDetail>(`/api/cards/${encodeURIComponent(params.id)}`);
	} catch {
		throw error(404, 'Kaart niet gevonden');
	}

	let similar: SimilarCard[] = [];
	try {
		similar = await api<SimilarCard[]>(`/api/cards/${encodeURIComponent(params.id)}/similar?limit=8`);
	} catch {
		// geen embeddings? dan gewoon geen 'vergelijkbaar'-sectie
	}

	let interactions: Interaction[] = [];
	try {
		interactions = await api<Interaction[]>(
			`/api/cards/${encodeURIComponent(params.id)}/interactions`
		);
	} catch {
		// nog geen interacties gemined
	}

	let rules: CardRules = { errata: [], relevantRules: [] };
	try {
		rules = await api<CardRules>(`/api/cards/${encodeURIComponent(params.id)}/rules`);
	} catch {
		// regels-index nog niet gedraaid — sectie gewoon verbergen
	}

	// Kaart-dossier (#127) — best-effort: lege hoofdstukken worden verborgen.
	let dossier: CardDossier = EMPTY_DOSSIER;
	try {
		dossier = await api<CardDossier>(`/api/cards/${encodeURIComponent(params.id)}/dossier`);
	} catch {
		// dossier niet beschikbaar — de kaartfeiten blijven gewoon werken
	}
	return { card, similar, interactions, rules, dossier };
};
