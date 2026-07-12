import { error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface Section {
	code: string;
	sourceId: string;
	sourceName: string;
	sourceUrl: string;
	text: string;
	pdfUrl: string | null;
	page: number | null;
	parents: { code: string; text: string }[];
	prev: string | null;
	next: string | null;
}

// Sectie-dossier (#127): de levende geschiedenis van een regel.
export interface SectionDossier {
	cards: { riftboundId: string; name: string; type: string | null; imageUrl: string | null }[];
	explains: { topic: string; title: string }[];
	claims: {
		id: number;
		statement: string;
		officialStatus: string;
		corroboration: number;
		trustScore: number;
		trustLabel: string;
	}[];
	changes: {
		id: number;
		changeType: string;
		severity: string;
		summary: string | null;
		detectedAt: string;
	}[];
	graphDegraded: boolean;
}

const EMPTY_DOSSIER: SectionDossier = {
	cards: [],
	explains: [],
	claims: [],
	changes: [],
	graphDegraded: false
};

export const load: PageServerLoad = async ({ params, url }) => {
	const source = url.searchParams.get('source');
	const qs = source ? `?source=${encodeURIComponent(source)}` : '';

	let section: Section;
	try {
		section = await api<Section>(`/api/rules/section/${encodeURIComponent(params.code)}${qs}`);
	} catch {
		throw error(404, `Sectie § ${params.code} niet gevonden`);
	}

	// Dossier best-effort: lege hoofdstukken worden verborgen, een haperend
	// dossier mag de regeltekst zelf nooit blokkeren.
	let dossier: SectionDossier = EMPTY_DOSSIER;
	try {
		dossier = await api<SectionDossier>(
			`/api/rules/section/${encodeURIComponent(params.code)}/dossier${qs}`
		);
	} catch {
		// geen dossier — de sectie blijft gewoon leesbaar
	}
	return { section, dossier };
};
