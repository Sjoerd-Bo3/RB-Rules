import { fail } from '@sveltejs/kit';
import type { Actions, PageServerLoad } from './$types';
import { api } from '$lib/api';

export interface AskStats {
	count: number;
	avgMs?: number;
	medianMs?: number;
	p90Ms?: number;
}

export const load: PageServerLoad = async () => {
	try {
		return { stats: await api<AskStats>('/api/ask/stats') };
	} catch {
		return { stats: { count: 0 } as AskStats };
	}
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
}
interface AskResult {
	answer: string;
	citations: Citation[];
	cards: AskCard[];
	questionType: string;
}

export const actions: Actions = {
	ask: async ({ request }) => {
		const form = await request.formData();
		const question = String(form.get('question') ?? '').trim();
		if (!question) return fail(400, { error: 'Stel eerst een vraag.' });

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
				body: JSON.stringify({ question, images })
			});
			return { question, hadPhoto: Boolean(images), ...result };
		} catch (e) {
			return fail(500, {
				error: `Vraag mislukt (${e instanceof Error ? e.message : e})`,
				question
			});
		}
	},
	// Self-learning (#24): feedback wordt een correctie in de reviewqueue;
	// na verificatie door de beheerder stuurt hij toekomstige antwoorden.
	feedback: async ({ request }) => {
		const form = await request.formData();
		const question = String(form.get('question') ?? '').trim();
		const verdict = String(form.get('verdict') ?? '');
		const text = String(form.get('text') ?? '').trim() || undefined;
		const answer = String(form.get('answer') ?? '');
		let citations: Citation[] = [];
		let cards: AskCard[] = [];
		try {
			citations = JSON.parse(String(form.get('citations') ?? '[]'));
			cards = JSON.parse(String(form.get('cards') ?? '[]'));
		} catch {
			/* corrupt doorgegeven state — dan zonder */
		}
		// Ook bij fouten antwoord+citaties teruggeven, anders verdwijnt het
		// zojuist gegeven antwoord van de pagina.
		if (!question || !['up', 'down'].includes(verdict)) {
			return fail(400, { error: 'Ongeldige feedback.', question, answer, citations, cards });
		}
		try {
			await api('/api/corrections', {
				method: 'POST',
				body: JSON.stringify({ question, verdict, text })
			});
			return { question, answer, citations, cards, feedbackSent: verdict };
		} catch (e) {
			return fail(500, {
				error: `Feedback versturen mislukt (${e instanceof Error ? e.message : e})`,
				question,
				answer,
				citations,
				cards
			});
		}
	}
};
