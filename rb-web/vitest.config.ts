import path from 'node:path';
import { defineConfig } from 'vitest/config';

// Bewust los van vite.config.ts: de unit-tests zijn pure TypeScript-helpers
// ($lib) en hebben de SvelteKit-plugin niet nodig.
export default defineConfig({
	// $lib-alias zoals SvelteKit hem kent: de login-poort-tests (#328) laden
	// route-modules (+page.server/+server) die uit $lib importeren.
	resolve: { alias: { $lib: path.resolve(import.meta.dirname, 'src/lib') } },
	test: { include: ['src/**/*.test.ts'] }
});
