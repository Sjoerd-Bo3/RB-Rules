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
	correcties: null
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
			default:
				throw error(404, 'Onbekend overzicht');
		}
	} catch {
		return { ...base, apiDown: true, data: null };
	}
};

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
	}
};
