import { error, fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { adminApi, authed } from '$lib/server/admin';

// Tegel-overzichten (#61): één parametrische route, per kind een eigen fetch.
// Filterwaarden worden hier gevalideerd zodat de UI-chips en de API-parameter
// altijd overeenkomen.
const KIND_FILTERS: Record<string, { allowed: string[]; fallback: string } | null> = {
	kaarten: null,
	embeddings: { allowed: ['embedded', 'unembedded'], fallback: 'embedded' },
	analyse: { allowed: ['mined', 'unmined'], fallback: 'mined' },
	regelsecties: null,
	bans: null,
	errata: null,
	interacties: null,
	wijzigingen: null,
	correcties: null,
	primer: null,
	// Claims (#50/#124): status-chips; leeg = default-weergave (alleen wat
	// aandacht vraagt: te reviewen, niet gearchiveerd). Afgehandeld, archief
	// en alles zitten achter de chips.
	claims: {
		allowed: ['unreviewed', 'accepted', 'rejected', 'superseded', 'archived', 'all'],
		fallback: ''
	},
	// Relatievoorstellen (#116/#124): zelfde chip-patroon als claims.
	relaties: { allowed: ['unreviewed', 'accepted', 'rejected', 'archived', 'all'], fallback: '' },
	// Bronvoorstellen uit de scout (#63): de bestaande statussen zíjn hier het
	// archief (#124, KISS) — default alleen te beoordelen, "all" toont alles.
	voorstellen: { allowed: ['proposed', 'accepted', 'rejected', 'all'], fallback: 'proposed' },
	gaten: null,
	// Piltover Archive-decks (#15): attributie + deep-link per deck.
	decks: null,
	// Set-dekking (#145): per set aanwezige/ontbrekende basisnummers.
	setdekking: null,
	// Gebruikers + kosteninzicht (#42): de chips kiezen de meetperiode.
	gebruikers: { allowed: ['vandaag', '7d', '30d'], fallback: '7d' }
};

export const load: PageServerLoad = async ({ params, url, cookies }) => {
	if (!authed(cookies)) throw redirect(303, '/admin');
	const kind = params.kind;
	// Object.hasOwn: `in` kijkt ook in de prototype-keten ("constructor" → 500).
	if (!Object.hasOwn(KIND_FILTERS, kind)) throw error(404, 'Onbekend overzicht');

	const page = Math.min(100_000, Math.max(1, Math.trunc(Number(url.searchParams.get('page'))) || 1));
	const q = url.searchParams.get('q')?.trim() ?? '';
	const source = url.searchParams.get('source') ?? '';
	const filterRule = KIND_FILTERS[kind];
	const rawFilter = url.searchParams.get('filter') ?? '';
	const filter = filterRule
		? filterRule.allowed.includes(rawFilter) ? rawFilter : filterRule.fallback
		: '';

	const base = { kind, page, q, filter, source, apiDown: false };
	try {
		switch (kind) {
			case 'kaarten':
			case 'embeddings':
			case 'analyse': {
				const qs = new URLSearchParams({ page: String(page) });
				if (filter) qs.set('filter', filter);
				if (q) qs.set('q', q);
				return { ...base, data: await adminApi<unknown>(`/api/admin/overview/cards?${qs}`) };
			}
			case 'regelsecties': {
				const qs = new URLSearchParams({ page: String(page) });
				if (source) qs.set('sourceId', source);
				return { ...base, data: await adminApi<unknown>(`/api/admin/overview/rulechunks?${qs}`) };
			}
			case 'bans':
				return { ...base, data: await adminApi<unknown>('/api/admin/overview/bans') };
			case 'errata':
				return { ...base, data: await adminApi<unknown>('/api/admin/overview/errata') };
			case 'interacties':
				return { ...base, data: await adminApi<unknown>(`/api/admin/overview/interactions?page=${page}`) };
			case 'wijzigingen':
				return { ...base, data: await adminApi<unknown>(`/api/admin/overview/changes?page=${page}`) };
			case 'correcties':
				return { ...base, data: await adminApi<unknown>('/api/admin/corrections') };
			case 'primer':
				return { ...base, data: await adminApi<unknown>('/api/admin/knowledge') };
			case 'claims': {
				const qs = new URLSearchParams({ page: String(page) });
				if (filter) qs.set('status', filter);
				return { ...base, data: await adminApi<unknown>(`/api/admin/overview/claims?${qs}`) };
			}
			case 'relaties': {
				const qs = new URLSearchParams({ page: String(page) });
				if (filter) qs.set('status', filter);
				return { ...base, data: await adminApi<unknown>(`/api/admin/overview/relations?${qs}`) };
			}
			case 'voorstellen': {
				const qs = new URLSearchParams({ page: String(page) });
				// rb-api kent hier geen "all": geen status-parameter = alles.
				if (filter && filter !== 'all') qs.set('status', filter);
				return { ...base, data: await adminApi<unknown>(`/api/admin/overview/proposals?${qs}`) };
			}
			case 'gaten':
				// Kennis-gaten-rapport (#52): vers berekend bij elke aanvraag.
				return { ...base, data: await adminApi<unknown>('/api/admin/overview/gaps') };
			case 'decks':
				return { ...base, data: await adminApi<unknown>(`/api/admin/overview/decks?page=${page}`) };
			case 'setdekking':
				// Set-dekking (#145): exact uit de riftbound-id's afgeleid.
				return { ...base, data: await adminApi<unknown>('/api/admin/overview/setcoverage') };
			case 'gebruikers': {
				const qs = new URLSearchParams({ page: String(page), period: filter });
				return { ...base, data: await adminApi<unknown>(`/api/admin/overview/users?${qs}`) };
			}
			default:
				throw error(404, 'Onbekend overzicht');
		}
	} catch {
		return { ...base, apiDown: true, data: null };
	}
};

// Reviewqueue-acties voor claims en relaties (#124) delen hun vorm: id (+
// eventuele notitie) uit het formulier, POST naar rb-api, en fouten mét
// item-id terug zodat de melding bij het juiste item landt en de getypte
// notitie niet verdwijnt (#42-patroon).
type CookieJar = { get(name: string): string | undefined };

async function reviewDecision(
	cookies: CookieJar,
	request: Request,
	resource: 'claims' | 'relations',
	decision: 'accept' | 'reject'
) {
	if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
	const form = await request.formData();
	const id = Number(form.get('id'));
	const note = String(form.get('note') ?? '').trim();
	try {
		await adminApi(`/api/admin/${resource}/${id}/${decision}`, {
			method: 'POST',
			body: JSON.stringify({ note: note || null })
		});
		return { ok: true };
	} catch (e) {
		return fail(502, { error: e instanceof Error ? e.message : String(e), id });
	}
}

async function itemAction(
	cookies: CookieJar,
	request: Request,
	resource: 'claims' | 'relations',
	action: 'archive' | 'unarchive'
) {
	if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
	const form = await request.formData();
	const id = Number(form.get('id'));
	try {
		await adminApi(`/api/admin/${resource}/${id}/${action}`, { method: 'POST' });
		return { ok: true };
	} catch (e) {
		return fail(502, { error: e instanceof Error ? e.message : String(e), id });
	}
}

async function archiveHandled(cookies: CookieJar, resource: 'claims' | 'relations') {
	if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
	try {
		const r = await adminApi<{ archived: number }>(`/api/admin/${resource}/archive-handled`, {
			method: 'POST'
		});
		return { ok: true, archived: r.archived };
	} catch (e) {
		return fail(502, { error: e instanceof Error ? e.message : String(e) });
	}
}

async function promoteNote(
	cookies: CookieJar,
	request: Request,
	resource: 'claims' | 'relations'
) {
	if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
	const form = await request.formData();
	const id = Number(form.get('id'));
	const note = String(form.get('note') ?? '').trim();
	try {
		const r = await adminApi<{ embedded: boolean }>(
			`/api/admin/${resource}/${id}/promote-note`,
			{ method: 'POST', body: JSON.stringify({ note: note || null }) }
		);
		return { ok: true, promoted: true, embedded: r.embedded, id };
	} catch (e) {
		return fail(502, { error: e instanceof Error ? e.message : String(e), id });
	}
}

// Correcties houden hun verifieer/verwijder-actie ook in het overzicht.
export const actions: Actions = {
	verifyCorrection: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/corrections/${form.get('id')}/verify`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	deleteCorrection: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/corrections/${form.get('id')}`, { method: 'DELETE' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	// Spelbegrip-primer (#70): bewerken her-embedt server-side; de status
	// blijft staan. Fail-paden geven het doc-id mee zodat de fout bij het
	// juiste formulier landt en de paginastate niet verdwijnt.
	saveKnowledge: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		const id = Number(form.get('id'));
		const title = String(form.get('title') ?? '').trim();
		const body = String(form.get('body') ?? '').trim();
		if (!title || !body) return fail(400, { error: 'Titel en tekst mogen niet leeg zijn', id });
		try {
			await adminApi(`/api/admin/knowledge/${id}`, {
				method: 'PATCH',
				body: JSON.stringify({ title, body })
			});
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e), id });
		}
	},
	approveKnowledge: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/knowledge/${form.get('id')}/approve`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	unapproveKnowledge: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/knowledge/${form.get('id')}/unapprove`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	deleteKnowledge: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/knowledge/${form.get('id')}`, { method: 'DELETE' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	// Claims-review (#50/#124): bevestigen maakt een claim retrieval-baar
	// (#51); verwerpen houdt hem uit beeld. Beide nemen de optionele
	// beheerder-notitie mee; bij verwerpen is die de zichtbare reden.
	// Fail-paden dragen het item-id mee zodat de fout bij het juiste item
	// landt en de paginastate (getypte notitie incluis) blijft staan (#42).
	acceptClaim: async ({ request, cookies }) => reviewDecision(cookies, request, 'claims', 'accept'),
	rejectClaim: async ({ request, cookies }) => reviewDecision(cookies, request, 'claims', 'reject'),
	// Archief (#124): uit de default-weergave, terugvindbaar via de chip;
	// status (en dus /ask-deelname of graph-projectie) verandert niet.
	archiveClaim: async ({ request, cookies }) => itemAction(cookies, request, 'claims', 'archive'),
	unarchiveClaim: async ({ request, cookies }) =>
		itemAction(cookies, request, 'claims', 'unarchive'),
	archiveHandledClaims: async ({ cookies }) => archiveHandled(cookies, 'claims'),
	// Notitie → geverifieerde ruling (#124): de uitleg van de beheerder wordt
	// een Correction (bestaand verify-pad) en stuurt voortaan de antwoorden.
	promoteClaimNote: async ({ request, cookies }) => promoteNote(cookies, request, 'claims'),
	// Relatie-review (#116/#124): accepteren maakt het voorstel definitief
	// (mee in de graph zodra ook het kind geaccepteerd is); verwerpen haalt
	// het uit de projectie en voorkomt her-voorstellen.
	acceptRelation: async ({ request, cookies }) =>
		reviewDecision(cookies, request, 'relations', 'accept'),
	rejectRelation: async ({ request, cookies }) =>
		reviewDecision(cookies, request, 'relations', 'reject'),
	archiveRelation: async ({ request, cookies }) =>
		itemAction(cookies, request, 'relations', 'archive'),
	unarchiveRelation: async ({ request, cookies }) =>
		itemAction(cookies, request, 'relations', 'unarchive'),
	archiveHandledRelations: async ({ cookies }) => archiveHandled(cookies, 'relations'),
	promoteRelationNote: async ({ request, cookies }) =>
		promoteNote(cookies, request, 'relations'),
	// Kind-vocabulaire (#116, patroon mechanieken): accepteren laat relaties
	// met dit kind meedoen in de graph-projectie; verwerpen houdt het kind
	// (en nieuwe voorstellen ermee) uit beeld.
	acceptRelationKind: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/relationkinds/${form.get('id')}/accept`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	rejectRelationKind: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/relationkinds/${form.get('id')}/reject`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	// Bronvoorstellen (#63): accepteren zet de bron uitgeschakeld in het
	// register (aanzetten gaat daarna bewust via de bronnen-tabel);
	// verwerpen houdt de URL uit volgende scout-runs.
	acceptProposal: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/proposals/${form.get('id')}/accept`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	rejectProposal: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		try {
			await adminApi(`/api/admin/proposals/${form.get('id')}/reject`, { method: 'POST' });
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e) });
		}
	},
	// Accountbeheer (#42): blokkeren/deblokkeren en quota bijstellen. Fouten
	// dragen het gebruikers-id mee zodat de melding bij de juiste rij landt.
	saveUser: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { error: 'Niet ingelogd' });
		const form = await request.formData();
		const id = Number(form.get('id'));
		const patch: Record<string, unknown> = {};
		if (form.has('blocked')) patch.blocked = form.get('blocked') === 'true';
		if (form.has('dailyQuota')) {
			const q = Number(form.get('dailyQuota'));
			if (!Number.isInteger(q) || q < 0) return fail(400, { error: 'Quotum moet een getal van 0 of hoger zijn', id });
			patch.dailyQuota = q;
		}
		if (form.has('dailyPhotoQuota')) {
			const q = Number(form.get('dailyPhotoQuota'));
			if (!Number.isInteger(q) || q < 0) return fail(400, { error: 'Foto-quotum moet een getal van 0 of hoger zijn', id });
			patch.dailyPhotoQuota = q;
		}
		try {
			await adminApi(`/api/admin/users/${id}`, {
				method: 'PATCH',
				body: JSON.stringify(patch)
			});
			return { ok: true };
		} catch (e) {
			return fail(502, { error: e instanceof Error ? e.message : String(e), id });
		}
	}
};
