// Herbruikbare change-kaart-helpers (#210, ChangeCard.svelte) — puur en
// los getest gehouden van het component, zelfde patroon als
// answerFormat.ts/ranges.ts (vitest heeft de SvelteKit-plugin niet nodig).

export interface DiffLine {
	text: string;
	kind: 'add' | 'del' | 'head';
}

/** Diff-tekst → regels met kleur: '+'-blok groen, '-'-blok rood. Ongewijzigd
 *  overgenomen uit de publieke feed (#210: nu gedeeld met alle kaart-
 *  contexten in plaats van een losse kopie per pagina). */
export function diffLines(diff: string): DiffLine[] {
	const out: DiffLine[] = [];
	let mode: 'add' | 'del' = 'add';
	for (const line of diff.split('\n')) {
		if (line.startsWith('+')) {
			mode = 'add';
			out.push({ text: line, kind: 'head' });
		} else if (line.startsWith('-')) {
			mode = 'del';
			out.push({ text: line, kind: 'head' });
		} else if (line.trim()) {
			out.push({ text: line.trim(), kind: mode });
		}
	}
	return out;
}

/** Severity → gedeelde badge-klasse uit app.css (#59): dezelfde drieslag als
 *  elders in de app (rules/[code], graph) — hoog rood, gemiddeld geel, de
 *  rest (incl. onbekende waarden) groen. */
export function severityBadgeClass(severity: string): 'err' | 'warn-b' | 'ok-b' {
	if (severity === 'high') return 'err';
	if (severity === 'medium') return 'warn-b';
	return 'ok-b';
}

/** Trust-aanduiding (#210): TrustTier 1 (official) en 2 (partner) zijn beide
 *  redactioneel/officieel geverifieerd vóór publicatie; TrustTier ≥3 is
 *  community (docs/KNOWLEDGE.md-ladder). De kaart toont bewust maar twee
 *  niveaus — een aparte "partner"-label voegt ruis toe zonder dat de lezer
 *  er iets mee kan. */
export function trustLabel(
	trustTier: number | null | undefined
): { label: string; tone: 'official' | 'community' } | null {
	if (trustTier == null) return null;
	return trustTier <= 2
		? { label: 'officieel', tone: 'official' }
		: { label: 'community', tone: 'community' };
}

/** Volledige kaart: "vandaag 14:32" zodra het vandaag is, anders "12 jul,
 *  14:32". Ongewijzigd overgenomen uit de publieke feed. */
export function formatChangeWhen(iso: string): string {
	const d = new Date(iso);
	const days = Math.floor((Date.now() - d.getTime()) / 86_400_000);
	const time = d.toLocaleString('nl-NL', {
		day: 'numeric',
		month: 'short',
		hour: '2-digit',
		minute: '2-digit'
	});
	return days === 0
		? `vandaag ${d.toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' })}`
		: time;
}

/** Compacte kaart (sectie-/bron-dossier): alleen de datum — de context
 *  (welke sectie, welke bron) staat daar al vast. */
export function formatChangeDate(iso: string): string {
	return new Date(iso).toLocaleDateString('nl-NL');
}
