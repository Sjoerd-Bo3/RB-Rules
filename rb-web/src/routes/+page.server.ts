import type { PageServerLoad } from './$types';
import { api } from '$lib/api';

interface Change {
	id: number;
	sourceId: string;
	changeType: string;
	severity: string;
	summary: string | null;
	meaning: string | null;
	detectedAt: string;
}

export const load: PageServerLoad = async () => {
	try {
		const changes = await api<Change[]>('/api/changes');
		return { changes, apiDown: false };
	} catch {
		return { changes: [] as Change[], apiDown: true };
	}
};
