// Knoop-helpers voor de brein-verkenner (#252). Puur en unit-getest: dezelfde
// label-/samenvattings-logica draait server-side in de `/graph/node`-proxy
// (hover-preview + detailweergave) en client-side in de graaf zelf.
import type { CardDetail } from '$lib/types';

/** Compacte dossier-projectie voor de detailweergave onder de graaf. Bewust
 *  niet het volledige `/api/cards/{id}/dossier`: de graaf toont relaties al
 *  als edges en de deck-populariteit hoort op de kaartpagina thuis. */
export interface GraphDossier {
	rulings: { id: number; question: string | null; text: string; date: string }[];
	claims: { id: number; statement: string; trustLabel: string }[];
	banHistory: { format: string; kind: string; effectiveFrom: string | null }[];
	/** Aantallen vóór afkapping — "3 van 12" blijft eerlijk. */
	rulingTotal: number;
	claimTotal: number;
}

/** Alles wat de hovercard én het detailpaneel van één knoop nodig hebben.
 *  `card`/`dossier` zijn alleen gevuld voor kaartknopen; `props` alleen voor
 *  de overige soorten (dan rendert het paneel de brein-projectie). */
export interface GraphNodeDetail {
	ref: string;
	kind: string;
	layer: string | null;
	label: string;
	/** Korte uitleg voor de hover-preview (afgekapt op zinsgrens-ish). */
	summary: string | null;
	imageUrl: string | null;
	trustLabel: string | null;
	card: CardDetail | null;
	dossier: GraphDossier | null;
	props: Record<string, unknown> | null;
}

export function refKind(ref: string): string {
	const i = ref.indexOf(':');
	return i > 0 ? ref.slice(0, i) : '';
}

export function refKey(ref: string): string {
	const i = ref.indexOf(':');
	return i > 0 ? ref.slice(i + 1) : ref;
}

/** Knoop-ref voor een kaart; de graph-view kent kaarten als kaal riftboundId. */
export const cardRef = (riftboundId: string) => `card:${riftboundId}`;
export const mechanicRef = (name: string) => `mechanic:${name}`;

const LABEL_FIELDS = ['name', 'title', 'cardName', 'question', 'statement', 'topic'] as const;

/** Leesbare titel van een brein-knoop uit zijn props (BrainService-projectie). */
export function nodeLabel(
	kind: string,
	ref: string,
	props: Record<string, unknown> | null | undefined
): string {
	const p = props ?? {};
	if (kind === 'section' && typeof p.code === 'string' && p.code) return `§${p.code}`;
	for (const key of LABEL_FIELDS) {
		const v = p[key];
		if (typeof v === 'string' && v.length > 0) return v;
	}
	return ref;
}

// Volgorde is betekenisvol: de meest verklarende tekst wint. `meaning` (change)
// vóór `summary`, en de kale bron-`text` pas als er geen synthese is.
const SUMMARY_FIELDS = [
	'meaning',
	'summary',
	'statement',
	'newText',
	'body',
	'text',
	'question',
	'description'
] as const;

/** Kapt af op een woordgrens; nooit midden in een woord. */
export function truncate(s: string, max: number): string {
	const clean = s.replace(/\s+/g, ' ').trim();
	if (clean.length <= max) return clean;
	const cut = clean.slice(0, max);
	const space = cut.lastIndexOf(' ');
	return (space > max * 0.5 ? cut.slice(0, space) : cut).trimEnd() + '…';
}

/** Korte uitleg bij een knoop voor de hover-preview. Null als de knoop niets
 *  te vertellen heeft (dan toont de hovercard alleen label + soort). */
export function nodeSummary(
	kind: string,
	props: Record<string, unknown> | null | undefined,
	max = 220
): string | null {
	const p = props ?? {};
	// Facetten (mechanic/domain/tag/set) dragen geen tekst maar wél een
	// telling — die ís de uitleg die je bij hover wilt zien.
	if (typeof p.cardCount === 'number') {
		const n = p.cardCount;
		const kaarten = `${n} ${n === 1 ? 'kaart' : 'kaarten'}`;
		if (kind === 'mechanic') return `Mechaniek op ${kaarten}`;
		if (kind === 'domain') return `Domein van ${kaarten}`;
		if (kind === 'tag') return `Tag op ${kaarten}`;
		if (kind === 'set') return `Set met ${kaarten}`;
		return kaarten;
	}
	for (const key of SUMMARY_FIELDS) {
		const v = p[key];
		if (typeof v === 'string' && v.trim().length > 0) return truncate(v, max);
	}
	return null;
}

/** Meta-regel onder de kaartnaam in preview en detail: type · domeinen · stats. */
export function cardMeta(card: {
	supertype: string | null;
	type: string | null;
	domains: string[];
	energy: number | null;
	might: number | null;
}): string {
	const parts: string[] = [];
	const t = [card.supertype, card.type].filter(Boolean).join(' ');
	if (t) parts.push(t);
	if (card.domains.length) parts.push(card.domains.join('/'));
	if (card.energy !== null) parts.push(`Energy ${card.energy}`);
	if (card.might !== null) parts.push(`Might ${card.might}`);
	return parts.join(' · ');
}

/** Doorklik naar de bestaande schermen per knoopsoort — het brein is de kaart,
 *  dit zijn de wegen terug naar de inhoud. */
export function nodeLinks(ref: string, kind: string): { href: string; label: string }[] {
	const key = refKey(ref);
	switch (kind) {
		case 'card':
			return [
				{ href: `/cards/${key}`, label: 'Naar kaartpagina' },
				{
					href: `/graph?card=${encodeURIComponent(key)}`,
					label: 'Kaart-verkenning (mechanieken en interacties)'
				}
			];
		case 'section': {
			const code = key.includes('/') ? key.slice(key.indexOf('/') + 1) : key;
			return [{ href: `/rules/${encodeURIComponent(code)}`, label: 'Lees in de regels-browser' }];
		}
		case 'mechanic':
			return [
				{ href: `/cards?mechanic=${encodeURIComponent(key)}`, label: 'Alle kaarten met deze mechaniek' }
			];
		case 'concept':
			return [{ href: '/primer', label: 'Naar de game-primer' }];
		default:
			return [];
	}
}
