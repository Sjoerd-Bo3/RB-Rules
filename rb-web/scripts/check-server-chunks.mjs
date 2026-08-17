// Regressiepoort (#140): elke server-chunk moet laden met de node_modules
// van de omgeving waarin dit script draait. In de runtime-stage van de
// Dockerfile zijn dat uitsluitend productie-dependencies — een package dat
// Vite externaliseert maar dat daar ontbreekt (de /ask-500: 'marked'),
// faalt zo de image-build in plaats van de route in productie.
import { readdirSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(process.argv[2] ?? 'build/server');
const files = [];
(function walk(dir) {
	for (const entry of readdirSync(dir)) {
		const p = join(dir, entry);
		if (statSync(p).isDirectory()) walk(p);
		else if (p.endsWith('.js')) files.push(p);
	}
})(root);

let failed = 0;
for (const file of files) {
	try {
		await import(pathToFileURL(file).href);
	} catch (e) {
		failed++;
		console.error(`LAADT NIET: ${file}\n  ${e.message.split('\n')[0]}`);
	}
}
if (failed > 0) {
	console.error(`${failed} van ${files.length} server-chunks laden niet — zie hierboven.`);
	process.exit(1);
}
console.log(`Alle ${files.length} server-chunks laden.`);
