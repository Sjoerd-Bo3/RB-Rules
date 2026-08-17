import { json, error } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { api } from '$lib/api';
import type { CardDetail } from '$lib/types';
import {
	cardMeta,
	nodeLabel,
	nodeSummary,
	refKey,
	refKind,
	truncate,
	type GraphDossier,
	type GraphNodeDetail
} from '$lib/graphNode';

// Knoop-detail voor de brein-verkenner (#252). De hover-preview en de
// detailweergave ónder de graaf laden hier client-side hun gegevens — de
// browser praat nooit rechtstreeks met rb-api, dus deze proxy is de weg.

interface BrainNodeResponse {
	ref: string;
	kind: string;
	layer: string;
	trustLabel: string;
	props: Record<string, unknown>;
}

/** Alleen wat het compacte dossier-blok toont; de rest van
 *  `/api/cards/{id}/dossier` (relaties, deck-populariteit) blijft op de
 *  kaartpagina. */
interface CardDossierResponse {
	rulings: { id: number; question: string | null; text: string; date: string }[];
	claims: { id: number; statement: string; trustLabel: string }[];
	banHistory: { format: string; kind: string; effectiveFrom: string | null }[];
}

const DOSSIER_TAKE = 3;

function briefDossier(d: CardDossierResponse): GraphDossier | null {
	const has = d.rulings.length || d.claims.length || d.banHistory.length;
	if (!has) return null;
	return {
		rulings: d.rulings.slice(0, DOSSIER_TAKE).map((r) => ({
			id: r.id,
			question: r.question,
			text: truncate(r.text, 280),
			date: r.date
		})),
		claims: d.claims.slice(0, DOSSIER_TAKE).map((c) => ({
			id: c.id,
			statement: truncate(c.statement, 280),
			trustLabel: c.trustLabel
		})),
		banHistory: d.banHistory,
		rulingTotal: d.rulings.length,
		claimTotal: d.claims.length
	};
}

/** De api()-helper gooit `rb-api <status>: <pad>` — status terugwinnen zodat
 *  een onbekende ref een 404 blijft en een platte api een 502 wordt. */
function apiStatus(e: unknown): number | null {
	const m = e instanceof Error ? /^rb-api (\d{3}):/.exec(e.message) : null;
	return m ? Number(m[1]) : null;
}

export const GET: RequestHandler = async ({ url }) => {
	const ref = url.searchParams.get('ref')?.trim();
	if (!ref || !ref.includes(':')) {
		throw error(400, 'ref ontbreekt of is ongeldig — verwacht kind:key, bv. card:OGN-001');
	}

	// Kaartknoop: `/api/cards/{id}` draagt de afbeelding en de volledige
	// kaartfeiten die de brein-projectie niet heeft. Het dossier komt
	// parallel en is best-effort — zonder dossier blijft de kaart staan.
	if (refKind(ref) === 'card') {
		const id = encodeURIComponent(refKey(ref));
		const [cardR, dossierR] = await Promise.allSettled([
			api<CardDetail>(`/api/cards/${id}`),
			api<CardDossierResponse>(`/api/cards/${id}/dossier`)
		]);
		if (cardR.status === 'fulfilled') {
			const card = cardR.value;
			return json({
				ref: `card:${card.riftboundId}`,
				kind: 'card',
				layer: 'cards',
				label: card.name,
				summary: cardMeta(card) || null,
				imageUrl: card.imageUrl,
				trustLabel: 'officieel (kaartdata)',
				card,
				dossier: dossierR.status === 'fulfilled' ? briefDossier(dossierR.value) : null,
				props: null
			} satisfies GraphNodeDetail);
		}
		// Kaart niet gevonden: val door naar de brein-knoop — die resolvet een
		// variant-printing alsnog naar de canonieke kaart.
	}

	try {
		const node = await api<BrainNodeResponse>(`/api/brain/node/${encodeURIComponent(ref)}`);
		return json({
			ref: node.ref,
			kind: node.kind,
			layer: node.layer,
			label: nodeLabel(node.kind, node.ref, node.props),
			summary: nodeSummary(node.kind, node.props),
			imageUrl: null,
			trustLabel: node.trustLabel,
			card: null,
			dossier: null,
			props: node.props
		} satisfies GraphNodeDetail);
	} catch (e) {
		const status = apiStatus(e);
		if (status === 404) throw error(404, `Onbekende knoop: ${ref}`);
		if (status === 400) throw error(400, `Ongeldige ref: ${ref}`);
		throw error(502, 'rb-api niet bereikbaar');
	}
};
