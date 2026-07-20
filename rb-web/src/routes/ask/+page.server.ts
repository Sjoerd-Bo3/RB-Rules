import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { api } from '$lib/api';
import { adminApi, authed } from '$lib/server/admin';
import { firePrewarm } from '$lib/prewarm';
import { quotaMessage } from '$lib/quota';
import { USER_COOKIE, userHeaders } from '$lib/server/user';
import type {
	AskCard,
	AskCitation,
	AskClaim,
	AskMisconception,
	AskResult
} from '$lib/types';

// Quota-fouten van rb-api (#42) vertaald naar een bruikbare melding — de
// api()-helper geeft alleen de status door. De teksten staan in $lib/quota,
// gedeeld met het streamingpad (#31).
function quotaError(msg: string, fallback: string): string {
	const status = [429, 401, 403].find((s) => msg.includes(String(s)));
	return (status !== undefined && quotaMessage(status)) || fallback;
}

export interface AskStats {
	count: number;
	avgMs?: number;
	medianMs?: number;
	p90Ms?: number;
	/** Fase-verdeling (#152): gemiddelde per fase over de recentste traces
	 *  mét timings; null/afwezig zolang er nog geen gemeten vragen zijn. */
	phases?: {
		count: number;
		rewriteMs: number;
		embedMs: number;
		retrievalMs: number;
		aiMs: number;
	} | null;
}

/** Aanpak-keuze (#153): het stukje accountinfo dat het vraagformulier nodig
 *  heeft — alleen aanwezig voor een ingelogde bezoeker. */
export interface AskAccount {
	dailyAgenticQuota: number;
	agenticToday: number;
}

/** Eigen ask-geschiedenis (#157): user_id (ingelogd) of ip_hash (anoniem) —
 *  rb-api bepaalt de scope zelf uit de request, hier geen parameter nodig. */
export interface AskHistoryItem {
	id: number;
	question: string;
	createdAt: string;
	questionType: string | null;
	answer: string | null;
	agentic: boolean;
}

export const load: PageServerLoad = async ({ cookies, getClientAddress }) => {
	// Voorverwarmsignaal (#154), fire-and-forget: mag het renderen nooit
	// vertragen of laten falen ($lib/prewarm slikt alles).
	firePrewarm(() =>
		api('/api/ask/prewarm', { method: 'POST', headers: { 'x-client-ip': getClientAddress() } })
	);
	const userAuthHeaders = userHeaders(cookies);
	const headers = { 'x-client-ip': getClientAddress(), ...userAuthHeaders };
	// Duurstatistiek en eigen geschiedenis (#157) parallel — beide best-effort.
	const [stats, askHistory] = await Promise.all([
		api<AskStats>('/api/ask/stats').catch(() => ({ count: 0 }) as AskStats),
		api<AskHistoryItem[]>('/api/ask/history', { headers }).catch(() => [] as AskHistoryItem[])
	]);
	// Ingelogd (#153): dagtegoed voor Grondig ophalen — best-effort, want
	// zonder account werkt de pagina gewoon (dan is er geen keuze en beslist
	// de server sowieso Auto).
	let account: AskAccount | null = null;
	if (cookies.get(USER_COOKIE)) {
		try {
			const me = await api<AskAccount>('/api/auth/me', { headers: userAuthHeaders });
			account = { dailyAgenticQuota: me.dailyAgenticQuota, agenticToday: me.agenticToday };
		} catch {
			account = null;
		}
	}
	// loggedIn voor de privacy-melding in het geschiedenis-paneel (#157) —
	// geen extra accountcall nodig, alleen of er een sessietoken meeging.
	// isAdmin (#166): bepaalt of "Vastleggen als ruling" direct verifieert —
	// zelfde rb_admin-cookiecheck als het beheer, geen extra rb-api-call.
	return {
		stats,
		account,
		askHistory,
		loggedIn: 'X-User-Token' in userAuthHeaders,
		isAdmin: authed(cookies)
	};
};

export const actions: Actions = {
	ask: async ({ request, getClientAddress, cookies }) => {
		const form = await request.formData();
		const question = String(form.get('question') ?? '').trim();
		if (!question) return fail(400, { error: 'Stel eerst een vraag.' });

		// Doorvragen (#41): eerdere rondes reizen mee als JSON.
		let history: { question: string; answer: string }[] = [];
		try {
			history = JSON.parse(String(form.get('history') ?? '[]'));
		} catch {
			history = [];
		}
		history = history.slice(-3);

		// Aanpak-keuze (#153): reist als request-veld mee; rb-api is de
		// meester (anoniem of onbekende waarde = Auto), dus hier geen poort.
		const approach = String(form.get('approach') ?? '').trim() || undefined;

		// Optionele board-state-foto — client verkleint al, dit is de vangrail.
		let images: { mediaType: string; data: string }[] | undefined;
		const photo = form.get('photo');
		if (photo instanceof File && photo.size > 0) {
			if (photo.size > 6_000_000) {
				return fail(400, { error: 'Foto is te groot (max 6 MB).', question });
			}
			const buf = Buffer.from(await photo.arrayBuffer());
			images = [{ mediaType: photo.type || 'image/jpeg', data: buf.toString('base64') }];
		}

		try {
			const result = await api<AskResult>('/api/ask', {
				method: 'POST',
				// Ingelogd (#42): sessietoken mee — dan telt de vraag tegen het
				// eigen dagquotum in plaats van de anonieme IP-limiet.
				headers: { 'x-client-ip': getClientAddress(), ...userHeaders(cookies) },
				body: JSON.stringify({
					question,
					images,
					history: history.length ? history : undefined,
					approach
				})
			});
			return { question, history, hadPhoto: Boolean(images), ...result };
		} catch (e) {
			const msg = e instanceof Error ? e.message : String(e);
			return fail(500, {
				error: quotaError(msg, `Vraag mislukt (${msg})`),
				question,
				history
			});
		}
	},
	// Self-learning (#24): feedback wordt een correctie in de reviewqueue;
	// na verificatie door de beheerder stuurt hij toekomstige antwoorden.
	feedback: async ({ request, getClientAddress, cookies }) => {
		const form = await request.formData();
		const question = String(form.get('question') ?? '').trim();
		const verdict = String(form.get('verdict') ?? '');
		const text = String(form.get('text') ?? '').trim() || undefined;
		const answer = String(form.get('answer') ?? '');
		let citations: AskCitation[] = [];
		let cards: AskCard[] = [];
		let claims: AskClaim[] = [];
		let misconceptions: AskMisconception[] = [];
		try {
			citations = JSON.parse(String(form.get('citations') ?? '[]'));
			cards = JSON.parse(String(form.get('cards') ?? '[]'));
			claims = JSON.parse(String(form.get('claims') ?? '[]'));
			misconceptions = JSON.parse(String(form.get('misconceptions') ?? '[]'));
		} catch {
			/* corrupt doorgegeven state — dan zonder */
		}
		// Ook bij fouten antwoord+citaties teruggeven, anders verdwijnt het
		// zojuist gegeven antwoord van de pagina.
		if (!question || !['up', 'down'].includes(verdict)) {
			return fail(400, {
				error: 'Ongeldige feedback.',
				question,
				answer,
				citations,
				cards,
				claims,
				misconceptions
			});
		}
		try {
			await api('/api/corrections', {
				method: 'POST',
				headers: { 'x-client-ip': getClientAddress(), ...userHeaders(cookies) },
				body: JSON.stringify({ question, verdict, text })
			});
			return { question, answer, citations, cards, claims, misconceptions, feedbackSent: verdict };
		} catch (e) {
			const msg = e instanceof Error ? e.message : String(e);
			return fail(500, {
				error: quotaError(msg, `Feedback versturen mislukt (${msg})`),
				question,
				answer,
				citations,
				cards,
				claims,
				misconceptions
			});
		}
	},
	// In-chat ruling vastleggen (#166): autoriteit bepaalt de route — rb-api
	// beslist server-authoritatief (X-Admin-Key resp. X-User-Token), hier
	// alleen welke credentials meegaan. Anoniem: de knop is al niet zichtbaar
	// (+page.svelte), en rb-api wijst het sowieso af (401).
	ruling: async ({ request, cookies, getClientAddress }) => {
		const form = await request.formData();
		const statement = String(form.get('statement') ?? '').trim();
		const scope = String(form.get('scope') ?? 'answer').trim();
		const topicRef = String(form.get('topicRef') ?? '').trim() || undefined;
		const sourceRef = String(form.get('sourceRef') ?? '').trim();
		const question = String(form.get('question') ?? '').trim() || undefined;
		const answer = String(form.get('answer') ?? '');
		let citations: AskCitation[] = [];
		let cards: AskCard[] = [];
		let claims: AskClaim[] = [];
		let misconceptions: AskMisconception[] = [];
		try {
			citations = JSON.parse(String(form.get('citations') ?? '[]'));
			cards = JSON.parse(String(form.get('cards') ?? '[]'));
			claims = JSON.parse(String(form.get('claims') ?? '[]'));
			misconceptions = JSON.parse(String(form.get('misconceptions') ?? '[]'));
		} catch {
			/* corrupt doorgegeven state — dan zonder */
		}
		// Het antwoord blijft zichtbaar ná deze action (zelfde reden als
		// feedback hierboven): zonder deze velden terug te geven verdwijnt het
		// zojuist gegeven antwoord van de pagina.
		const context = { question, answer, citations, cards, claims, misconceptions };

		if (!statement) return fail(400, { rulingError: 'Vul een uitspraak in.', ...context });
		if (!sourceRef) {
			return fail(400, {
				rulingError: 'Een bronverwijzing (waar besloten) is verplicht.',
				...context
			});
		}
		if ((scope === 'card' || scope === 'rule_section') && !topicRef) {
			return fail(400, {
				rulingError:
					scope === 'card' ? 'Kies een kaart voor deze scope.' : 'Kies een §-sectie voor deze scope.',
				...context
			});
		}

		const admin = authed(cookies);
		const loggedIn = Boolean(cookies.get(USER_COOKIE));
		if (!admin && !loggedIn) {
			return fail(401, {
				rulingError: 'Log in (of als beheerder) om een ruling vast te leggen.',
				...context
			});
		}

		const body = JSON.stringify({ statement, scope, topicRef, sourceRef, question });
		try {
			const result = admin
				? await adminApi<{ verified: boolean }>('/api/ask/ruling', { method: 'POST', body })
				: await api<{ verified: boolean }>('/api/ask/ruling', {
						method: 'POST',
						headers: { 'x-client-ip': getClientAddress(), ...userHeaders(cookies) },
						body
					});
			return { rulingSaved: true, rulingVerified: result.verified, ...context };
		} catch (e) {
			const msg = e instanceof Error ? e.message : String(e);
			return fail(500, {
				rulingError: quotaError(msg, `Vastleggen mislukt (${msg})`),
				...context
			});
		}
	}
};
