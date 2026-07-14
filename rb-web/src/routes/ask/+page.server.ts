import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { api } from '$lib/api';
import { firePrewarm } from '$lib/prewarm';
import { quotaMessage } from '$lib/quota';
import { USER_COOKIE, userHeaders } from '$lib/server/user';

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

export const load: PageServerLoad = async ({ cookies, getClientAddress }) => {
	// Voorverwarmsignaal (#154), fire-and-forget: mag het renderen nooit
	// vertragen of laten falen ($lib/prewarm slikt alles).
	firePrewarm(() =>
		api('/api/ask/prewarm', { method: 'POST', headers: { 'x-client-ip': getClientAddress() } })
	);
	let stats: AskStats;
	try {
		stats = await api<AskStats>('/api/ask/stats');
	} catch {
		stats = { count: 0 };
	}
	// Ingelogd (#153): dagtegoed voor Grondig ophalen — best-effort, want
	// zonder account werkt de pagina gewoon (dan is er geen keuze en beslist
	// de server sowieso Auto).
	let account: AskAccount | null = null;
	if (cookies.get(USER_COOKIE)) {
		try {
			const me = await api<AskAccount>('/api/auth/me', { headers: userHeaders(cookies) });
			account = { dailyAgenticQuota: me.dailyAgenticQuota, agenticToday: me.agenticToday };
		} catch {
			account = null;
		}
	}
	return { stats, account };
};

interface Citation {
	n: number;
	sourceName: string;
	url: string;
	section: string | null;
	trust: number;
	text: string | null;
	pdfUrl: string | null;
	page: number | null;
	parents: { code: string; text: string }[] | null;
}
export interface AskCard {
	riftboundId: string;
	name: string;
	type: string | null;
	supertype: string | null;
	domains: string[];
	energy: number | null;
	might: number | null;
	textPlain: string | null;
	mechanics: string[] | null;
	imageUrl: string | null;
	banned: boolean;
	/** Set-legaliteit (#68): label voor kaarten uit een nog niet verschenen set. */
	setName: string | null;
	legalFrom: string | null;
	legality: 'legal' | 'upcoming' | 'announced';
}
/** Community-consensus (#51): geaccepteerde claims die als interpretatielaag
 *  meegingen — apart blok onder het antwoord, met trust-label en bronnen. */
export interface AskClaimSource {
	sourceName: string;
	url: string;
}
export interface AskClaim {
	topicType: string;
	topicRef: string;
	statement: string;
	corroboration: number;
	trustScore: number;
	officialStatus: string;
	sources: AskClaimSource[];
}
/** Misvattingen-kanaal (#125): verworpen community-claims mét officiële
 *  weerlegging — het misvatting-blok toont beide bewijzen (community-citaat
 *  met bron-link én de weerlegging, met §-link waar herleidbaar). */
export interface AskMisconceptionSource {
	sourceName: string;
	url: string;
	quote: string | null;
}
export interface AskMisconception {
	topicType: string;
	topicRef: string;
	statement: string;
	rebuttal: string;
	rebuttalSection: string | null;
	sources: AskMisconceptionSource[];
}
interface AskResult {
	answer: string;
	citations: Citation[];
	cards: AskCard[];
	questionType: string;
	claims: AskClaim[] | null;
	misconceptions: AskMisconception[] | null;
	/** Aanpak-terugmelding (#153): welke aanpak het werd en waarom die
	 *  eventueel afwijkt van de keuze (machine-sleutel, zie $lib/approach). */
	approach: string | null;
	approachReason: string | null;
}

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
		let citations: Citation[] = [];
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
	}
};
