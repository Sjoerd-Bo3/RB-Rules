import { error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface DeckLegalityIssue {
	cardCode: string;
	cardName: string | null;
	reason: string;
}

export interface DeckLegality {
	status: 'legal' | 'illegal' | 'incomplete';
	issues: DeckLegalityIssue[];
	unknownCount: number;
}

export interface DeckCardRow {
	cardCode: string;
	quantity: number;
	canonicalRiftboundId: string | null;
	cardName: string | null;
	imageUrl: string | null;
	/** Presentatie per kaart (#269/#270): battlefields zijn liggend. */
	imageWidth: number | null;
	imageHeight: number | null;
	imageAltText: string | null;
	imageColorPrimary: string | null;
}

export interface DeckSection {
	section: string;
	cards: DeckCardRow[];
}

export interface DeckDetail {
	id: string;
	name: string | null;
	domains: string[];
	sourceUrl: string;
	paCreatedAt: string | null;
	paUpdatedAt: string | null;
	views: number;
	likes: number;
	sections: DeckSection[];
	legality: DeckLegality;
}

export const load: PageServerLoad = async ({ params }) => {
	try {
		const deck = await api<DeckDetail>(`/api/decks/${encodeURIComponent(params.id)}`);
		return { deck };
	} catch {
		throw error(404, 'Deck niet gevonden');
	}
};
