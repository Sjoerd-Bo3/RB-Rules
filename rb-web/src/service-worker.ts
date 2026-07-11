/// <reference types="@sveltejs/kit" />
/// <reference lib="webworker" />

// PWA (#28): minimale service worker — offline-vriendelijke shell-cache voor
// statische build-assets + web-push voor high-severity wijzigingen.
// Bewust géén cache op API/paginadata: de kern van deze site is actualiteit.
import { build, files, version } from '$service-worker';

const sw = self as unknown as ServiceWorkerGlobalScope;
const CACHE = `rb-rules-${version}`;
const ASSETS = [...build, ...files];

sw.addEventListener('install', (event) => {
	event.waitUntil(caches.open(CACHE).then((c) => c.addAll(ASSETS)).then(() => sw.skipWaiting()));
});

sw.addEventListener('activate', (event) => {
	event.waitUntil(
		caches.keys().then(async (keys) => {
			for (const key of keys) if (key !== CACHE) await caches.delete(key);
			await sw.clients.claim();
		})
	);
});

sw.addEventListener('fetch', (event) => {
	const url = new URL(event.request.url);
	if (event.request.method !== 'GET' || url.origin !== sw.location.origin) return;
	if (!ASSETS.includes(url.pathname)) return; // alleen gebuilde assets uit cache
	event.respondWith(
		caches.open(CACHE).then(async (c) => (await c.match(url.pathname)) ?? fetch(event.request))
	);
});

sw.addEventListener('push', (event) => {
	let data = { title: 'Riftbound Rules', body: 'Er is een wijziging.', url: '/' };
	try {
		data = { ...data, ...event.data?.json() };
	} catch {
		/* payload zonder json */
	}
	event.waitUntil(
		sw.registration.showNotification(data.title, {
			body: data.body,
			icon: '/icon.svg',
			badge: '/icon.svg',
			data: { url: data.url }
		})
	);
});

sw.addEventListener('notificationclick', (event) => {
	event.notification.close();
	const url = (event.notification.data as { url?: string })?.url ?? '/';
	event.waitUntil(sw.clients.openWindow(url));
});
