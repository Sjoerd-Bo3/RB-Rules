import { beforeEach, describe, expect, it, vi } from 'vitest';

// Login-poort (#328), rb-web-kant: de form-action en de proxies zijn de
// server-side poort die de bezoeker raakt — de uitleg-staat op de pagina is
// alleen presentatie. Mutatie-bewijs: haal de cookie-check uit de action of
// een proxy en de bijbehorende anoniem-test hier wordt rood (het request
// bereikt dan rb-api — hier: de gemockte api/fetch).

vi.mock('$lib/api', () => ({
	API_BASE: 'http://rb-api.test',
	api: vi.fn()
}));
vi.mock('$lib/prewarm', () => ({ firePrewarm: vi.fn() }));
// $env/dynamic/private bestaat alleen binnen SvelteKit — buiten de kit-runtime
// mocken (admin.ts leest er het admin-wachtwoord uit; hier irrelevant).
vi.mock('$env/dynamic/private', () => ({ env: {} }));

import { api } from '$lib/api';
import { firePrewarm } from '$lib/prewarm';
import { actions, load } from './+page.server';
import { POST as streamPost } from './stream/+server';
import { GET as explainGet } from '../cards/[id]/explain/[other]/+server';

const apiMock = vi.mocked(api);
const prewarmMock = vi.mocked(firePrewarm);

function cookiesWith(user: string | null) {
	return {
		get: (name: string) => (name === 'rb_user' && user ? user : undefined)
	} as never;
}

function askEvent(user: string | null, fields: Record<string, string> = {}) {
	const form = new FormData();
	form.set('question', 'Wat doet [Deflect]?');
	for (const [k, v] of Object.entries(fields)) form.set(k, v);
	return {
		request: new Request('http://localhost/ask?/ask', { method: 'POST', body: form }),
		getClientAddress: () => '203.0.113.7',
		cookies: cookiesWith(user)
	} as never;
}

beforeEach(() => {
	vi.clearAllMocks();
});

describe('ask-action (vraag, doorvragen, foto — zelfde pad)', () => {
	it('weigert anoniem met 401 zonder rb-api aan te raken', async () => {
		const result = (await actions.ask(askEvent(null))) as {
			status: number;
			data: { error: string };
		};
		expect(result.status).toBe(401);
		expect(result.data.error).toContain('Log in');
		expect(apiMock).not.toHaveBeenCalled();
	});

	it('weigert ook een doorvraag (history mee) anoniem', async () => {
		const result = (await actions.ask(
			askEvent(null, { history: JSON.stringify([{ question: 'q', answer: 'a' }]) })
		)) as { status: number };
		expect(result.status).toBe(401);
		expect(apiMock).not.toHaveBeenCalled();
	});

	it('laat een ingelogde vraag gewoon door naar rb-api (regressie)', async () => {
		apiMock.mockResolvedValueOnce({ answer: 'Oordeel: ja.', citations: [] });
		const result = (await actions.ask(askEvent('sessietoken'))) as Record<string, unknown>;
		expect(result.answer).toBe('Oordeel: ja.');
		expect(apiMock).toHaveBeenCalledWith(
			'/api/ask',
			expect.objectContaining({
				headers: expect.objectContaining({ 'X-User-Token': 'sessietoken' })
			})
		);
	});
});

describe('/ask/stream-proxy', () => {
	it('weigert anoniem met 401 zonder upstream-fetch', async () => {
		const fetchSpy = vi.spyOn(globalThis, 'fetch');
		const res = await streamPost({
			request: new Request('http://localhost/ask/stream', {
				method: 'POST',
				body: JSON.stringify({ question: 'x' })
			}),
			getClientAddress: () => '203.0.113.7',
			cookies: cookiesWith(null)
		} as never);
		expect(res.status).toBe(401);
		expect((await res.json()).error).toContain('Log in');
		expect(fetchSpy).not.toHaveBeenCalled();
		fetchSpy.mockRestore();
	});

	it('stuurt een ingelogde stream door mét sessietoken (regressie)', async () => {
		const upstream = new Response('{"type":"done"}\n', { status: 200 });
		const fetchSpy = vi
			.spyOn(globalThis, 'fetch')
			.mockResolvedValueOnce(upstream as never);
		const res = await streamPost({
			request: new Request('http://localhost/ask/stream', {
				method: 'POST',
				body: JSON.stringify({ question: 'x' })
			}),
			getClientAddress: () => '203.0.113.7',
			cookies: cookiesWith('sessietoken')
		} as never);
		expect(res.status).toBe(200);
		expect(fetchSpy).toHaveBeenCalledWith(
			'http://rb-api.test/api/ask/stream',
			expect.objectContaining({
				headers: expect.objectContaining({ 'X-User-Token': 'sessietoken' })
			})
		);
		fetchSpy.mockRestore();
	});
});

describe('similarity-uitleg-proxy (LLM-pad op de kaartpagina)', () => {
	it('weigert anoniem met 401 zonder rb-api aan te raken', async () => {
		const res = await explainGet({
			params: { id: 'ogn-1', other: 'ogn-2' },
			getClientAddress: () => '203.0.113.7',
			cookies: cookiesWith(null)
		} as never);
		expect(res.status).toBe(401);
		expect(apiMock).not.toHaveBeenCalled();
	});
});

describe('prewarm op de paginalaad', () => {
	it('boot geen SDK-subprocess meer voor anonieme bezoekers', async () => {
		apiMock.mockResolvedValue({ count: 0 });
		await load({
			cookies: cookiesWith(null),
			getClientAddress: () => '203.0.113.7'
		} as never);
		expect(prewarmMock).not.toHaveBeenCalled();
	});

	it('blijft voorverwarmen voor ingelogde bezoekers', async () => {
		apiMock.mockResolvedValue({ count: 0 });
		await load({
			cookies: cookiesWith('sessietoken'),
			getClientAddress: () => '203.0.113.7'
		} as never);
		expect(prewarmMock).toHaveBeenCalledTimes(1);
	});
});
