import { defineConfig } from 'vitest/config';

// Bewust los van vite.config.ts: de unit-tests zijn pure TypeScript-helpers
// ($lib) en hebben de SvelteKit-plugin niet nodig.
export default defineConfig({
	test: { include: ['src/**/*.test.ts'] }
});
