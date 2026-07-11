import { json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import { api } from '$lib/api';

// Proxy: de browser kan rb-api niet direct bereiken; deze route stuurt
// push-registraties door. GET geeft de VAPID public key (of 404 = uit).
export const GET: RequestHandler = async () => {
	try {
		return json(await api<{ publicKey: string }>('/api/push/vapid'));
	} catch {
		return json({ error: 'push niet geconfigureerd' }, { status: 404 });
	}
};

export const POST: RequestHandler = async ({ request }) => {
	const body = (await request.json()) as {
		action: 'subscribe' | 'unsubscribe';
		endpoint: string;
		p256dh?: string;
		auth?: string;
	};
	const path = body.action === 'unsubscribe' ? '/api/push/unsubscribe' : '/api/push/subscribe';
	try {
		await api(path, {
			method: 'POST',
			body: JSON.stringify(
				body.action === 'unsubscribe'
					? { endpoint: body.endpoint }
					: { endpoint: body.endpoint, p256dh: body.p256dh, auth: body.auth }
			)
		});
		return json({ ok: true });
	} catch (e) {
		return json({ error: e instanceof Error ? e.message : String(e) }, { status: 502 });
	}
};
