import { fail } from '@sveltejs/kit';
import type { Actions } from './$types';
import { api } from '$lib/api';

interface Citation {
	n: number;
	sourceName: string;
	url: string;
	section: string | null;
	trust: number;
}
interface AskResult {
	answer: string;
	citations: Citation[];
}

export const actions: Actions = {
	ask: async ({ request }) => {
		const form = await request.formData();
		const question = String(form.get('question') ?? '').trim();
		if (!question) return fail(400, { error: 'Stel eerst een vraag.' });
		try {
			const result = await api<AskResult>('/api/ask', {
				method: 'POST',
				body: JSON.stringify({ question })
			});
			return { question, ...result };
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
		try {
			citations = JSON.parse(String(form.get('citations') ?? '[]'));
		} catch {
			citations = [];
		}
		// Ook bij fouten antwoord+citaties teruggeven, anders verdwijnt het
		// zojuist gegeven antwoord van de pagina.
		if (!question || !['up', 'down'].includes(verdict)) {
			return fail(400, { error: 'Ongeldige feedback.', question, answer, citations });
		}
		try {
			await api('/api/corrections', {
				method: 'POST',
				body: JSON.stringify({ question, verdict, text })
			});
			return { question, answer, citations, feedbackSent: verdict };
		} catch (e) {
			return fail(500, {
				error: `Feedback versturen mislukt (${e instanceof Error ? e.message : e})`,
				question,
				answer,
				citations
			});
		}
	}
};
