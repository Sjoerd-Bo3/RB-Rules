import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface PrimerDoc {
	id: number;
	kind: string;
	topic: string;
	title: string;
	body: string;
	sectionRefs: string | null;
	updatedAt: string;
}

// Publieke spelbegrip-pagina (#70): alleen goedgekeurde docs, read-only.
// De API levert ze al in de didactische conceptvolgorde.
export const load: PageServerLoad = async () => {
	try {
		const docs = await api<PrimerDoc[]>('/api/knowledge');
		return { docs: docs.filter((d) => d.kind === 'primer'), apiDown: false };
	} catch {
		return { docs: [] as PrimerDoc[], apiDown: true };
	}
};
