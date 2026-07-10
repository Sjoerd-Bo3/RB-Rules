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
	default: async ({ request }) => {
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
	}
};
