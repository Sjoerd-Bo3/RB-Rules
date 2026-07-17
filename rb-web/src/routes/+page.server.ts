import type { PageServerLoad } from './$types';
import { api } from '$lib/api';
import type { ChangeConfirmation } from '$lib/types';

// Overzicht-dashboard (#214): de root is niet langer de feed (die verhuisde
// naar /wijzigingen) maar een landingsdashboard — zoek-hero, statistiek-tegels,
// recente wijzigingen en "spring naar".
export interface PublicStats {
	cards: number;
	verifiedRulings: number;
	bans: number;
	recentChanges: number;
}

export interface DashChange {
	id: number;
	changeType: string;
	severity: string;
	summary: string | null;
	detectedAt: string;
	sourceName: string;
	domain: string | null;
	confirmedBy: ChangeConfirmation[];
}

const EMPTY_STATS: PublicStats = { cards: 0, verifiedRulings: 0, bans: 0, recentChanges: 0 };

export const load: PageServerLoad = async () => {
	// Beide best-effort: een dood dashboard is nutteloos, maar één dode bron
	// mag de rest niet meeslepen.
	const [stats, changes] = await Promise.all([
		api<PublicStats>('/api/stats').catch(() => EMPTY_STATS),
		api<DashChange[]>('/api/changes').catch(() => [] as DashChange[])
	]);
	return {
		stats,
		recent: changes.slice(0, 4),
		apiDown: changes.length === 0 && stats === EMPTY_STATS
	};
};
