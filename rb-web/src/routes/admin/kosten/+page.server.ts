import { fail, redirect } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { adminApi, authed } from '$lib/server/admin';

// Kosten-paneel (#328): verbruik + schaduwkosten per gebruiker en per
// job-soort. De eerste laad komt server-side; daarna pollt de pagina de
// eigen /admin/kosten-GET (zelfde aanpak als de job-voortgang op /admin).
// PRIVACY: het paneel toont uitsluitend maten, aantallen en bedragen —
// nooit vraaginhoud (die blijft achter de aparte traces-poort).

const PERIODS = ['vandaag', '7d', '30d'];

export const load: PageServerLoad = async ({ url, cookies }) => {
	if (!authed(cookies)) throw redirect(303, '/admin');
	const raw = url.searchParams.get('period') ?? '7d';
	const period = PERIODS.includes(raw) ? raw : '7d';
	try {
		const costs = await adminApi<unknown>(`/api/admin/costs?period=${period}`);
		return { period, costs, apiDown: false };
	} catch {
		return { period, costs: null, apiDown: true };
	}
};

export const actions: Actions = {
	// Nieuwe tariefrij (#328): append-only — een prijswijziging is een nieuwe
	// rij met ingangsdatum, zodat geboekte bedragen reproduceerbaar blijven.
	tariff: async ({ request, cookies }) => {
		if (!authed(cookies)) return fail(401, { tariffError: 'Niet ingelogd' });
		const form = await request.formData();
		const model = String(form.get('model') ?? '').trim();
		const input = Number(String(form.get('input') ?? '').replace(',', '.'));
		const output = Number(String(form.get('output') ?? '').replace(',', '.'));
		const from = String(form.get('from') ?? '').trim();
		if (!model) return fail(400, { tariffError: 'Model is verplicht.' });
		if (!Number.isFinite(input) || !Number.isFinite(output) || input < 0 || output < 0)
			return fail(400, { tariffError: 'Vul geldige prijzen in (USD per miljoen tokens).' });
		try {
			await adminApi('/api/admin/tariffs', {
				method: 'POST',
				body: JSON.stringify({
					model,
					inputUsdPerMTok: input,
					outputUsdPerMTok: output,
					// Leeg = per direct; met datum ook vooraf in te voeren.
					effectiveFrom: from ? new Date(from).toISOString() : undefined
				})
			});
			return { tariffSaved: true };
		} catch (e) {
			return fail(502, {
				tariffError: `Tarief niet opgeslagen (${e instanceof Error ? e.message : e})`
			});
		}
	}
};
