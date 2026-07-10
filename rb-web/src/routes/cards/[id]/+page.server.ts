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
}

interface SimilarCard {
	riftboundId: string;
	name: string;
	type: string | null;
	domains: string[];
	energy: number | null;
	might: number | null;
	imageUrl: string | null;
}

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
	return { card, similar };
};
