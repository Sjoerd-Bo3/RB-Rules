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
	prev: string | null;
	next: string | null;
}

export const load: PageServerLoad = async ({ params, url }) => {
	const source = url.searchParams.get('source');
	const qs = source ? `?source=${encodeURIComponent(source)}` : '';
	try {
		const section = await api<Section>(
			`/api/rules/section/${encodeURIComponent(params.code)}${qs}`
		);
		return { section };
	} catch {
		throw error(404, `Sectie § ${params.code} niet gevonden`);
	}
};
