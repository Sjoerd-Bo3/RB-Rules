// Antwoordformat-helpers (#69): de citatielijst onder het antwoord is de
// enige plek met regelsecties. citationEssence maakt de dichtgeklapte citatie
// leesbaar (eerste zin als één-regel-essentie); stripDuplicateRuleRefs is het
// vangnet voor als het model tóch een "Regelbasis"-blok of §-tabel produceert
// die de citatielijst alleen dubbelt.

export interface CitationRef {
	n: number;
	section: string | null;
}

/** Kleur-niveau van het zekerheidslabel in de oordeel-banner. #51 breidt het
 *  vocabulaire uit met "Community-consensus (N bronnen)" — een eigen niveau,
 *  visueel onderscheiden van officieel bevestigd/afgeleid. */
export function certaintyLevel(
	zekerheid: string | null | undefined
): 'ok' | 'warn' | 'community' | 'unsure' {
	const z = (zekerheid ?? '').toLowerCase();
	if (z.startsWith('bevestigd')) return 'ok';
	if (z.startsWith('afgeleid')) return 'warn';
	if (z.startsWith('community')) return 'community';
	return 'unsure';
}

/** Streaming (#31): markdown/widget-parsing is pas zinvol op afgeronde
 *  regels. Splits het groeiende antwoord op de laatste newline: `settled`
 *  gaat door AnswerView (stripDuplicateRuleRefs, widgets, markdown), de
 *  `tail` — de nog binnenstromende regel — rendert als kale tekst. Een
 *  widget-marker aan het staart-einde wordt verborgen — half ([[rule:46…)
 *  én compleet ([[rule:466.2.c]], wachtend op de newline die hem settelt):
 *  dat is machine-syntax, geen leestekst (review-fix). */
export function splitSettled(text: string): { settled: string; tail: string } {
	const i = text.lastIndexOf('\n');
	const settled = i < 0 ? '' : text.slice(0, i + 1);
	const tail = (i < 0 ? text : text.slice(i + 1)).replace(/\[\[[^\]]*(\]\]?)?$/, '');
	return { settled, tail };
}

const stripMd = (s: string): string =>
	s
		.replace(/\[([^\]]*)\]\([^)]*\)/g, '$1') // [tekst](url) → tekst
		.replace(/^#{1,6}\s+/gm, '')
		.replace(/[*_`]/g, '');

/** Eén-regel-essentie van een regelsectie: de eerste zin van de tekst,
 *  markdown gestript, afgekapt op maxLen tekens met ellipsis. */
export function citationEssence(text: string | null | undefined, maxLen = 110): string | null {
	if (!text) return null;
	const plain = stripMd(text).replace(/\s+/g, ' ').trim();
	if (!plain) return null;
	// Zinseinde = .!? gevolgd door spatie + niet-kleine-letter (of tekst-einde),
	// zodat §-codes (466.2.c) en afkortingen (e.g.) de zin niet afbreken.
	const m = plain.match(/^[\s\S]*?[.!?](?=\s+[^a-z]|$)/);
	let out = (m ? m[0] : plain).trim();
	if (out.length > maxLen) out = out.slice(0, maxLen - 1).trimEnd() + '…';
	return out;
}

/** Verwijdert een "Regelbasis"-blok of losse markdown-tabel die alleen
 *  verwijzingen ([n], §-codes, [[rule:…]]) bevat die al in de citatielijst
 *  staan. Lopende tekst en verwijzingen naar onbekende secties blijven
 *  altijd staan — bij twijfel niets weghalen. */
export function stripDuplicateRuleRefs(answer: string, citations: CitationRef[]): string {
	if (!answer || citations.length === 0) return answer;
	// Sectiecodes genormaliseerd zonder §-prefix — de parser slaat ze zonder
	// op, maar wees robuust voor beide vormen.
	const sections = new Set(
		citations.flatMap((c) =>
			c.section ? [c.section.replace(/^§\s*/, '').trim().toLowerCase()] : []
		)
	);
	const numbers = new Set(citations.map((c) => c.n));

	// Telt bekende verwijzingen in een regel; null zodra er een verwijzing
	// naar iets búiten de citatielijst staat (weglaten = informatieverlies).
	const knownRefs = (line: string): number | null => {
		let found = 0;
		let unknown = false;
		const count = (known: boolean): string => {
			if (known) found++;
			else unknown = true;
			return ' ';
		};
		const rest = line
			.replace(/\[\[rule:([^\]]+)\]\]/gi, (_, c: string) =>
				count(sections.has(c.trim().toLowerCase()))
			)
			.replace(/§\s*([0-9][0-9a-z.]*)/gi, (_, c: string) =>
				count(sections.has(c.replace(/\.+$/, '').toLowerCase()))
			)
			.replace(/\[(\d{1,2})\]/g, (_, n: string) => count(numbers.has(Number(n))));
		// Kale sectiecodes (minstens één punt) tellen alleen mee als ze in de
		// citatielijst staan — losse getallen zijn geen verwijzing.
		for (const m of rest.matchAll(/\b\d{1,4}(?:\.\d+)+(?:\.[a-z])?\b/g))
			if (sections.has(m[0])) found++;
		return unknown ? null : found;
	};

	const isBlank = (t: string) => t.trim() === '';
	const isTableRow = (t: string) => t.trim().startsWith('|');
	const isTableSeparator = (t: string) => /^\s*\|[\s|:-]*-[\s|:-]*\|?\s*$/.test(t);
	const isListy = (t: string) => /^\s*([-*+]\s|\d{1,2}[.)]\s|\||\[\[rule:)/.test(t);
	// Blok-eindes: een markdown-kop of een regel die met een **Label:** begint.
	const isHeadingLine = (t: string) => /^\s*#{1,6}\s+/.test(t) || /^\s*\*\*[^*]+:?\*\*/.test(t);
	const stripDecor = (t: string) => t.replace(/[#*_`]/g, '').trim().replace(/:+$/, '').trim();
	const isRuleBasisHeading = (t: string) => stripDecor(t).toLowerCase() === 'regelbasis';

	// Puur verwijzingsblok = alleen lijst-/tabelregels met bekende citaties
	// (plus hooguit één tabel-kopregel en scheidingsregels). Eén regel echte
	// lopende tekst of één onbekende verwijzing → blok blijft staan.
	const isPureRefBlock = (block: string[]): boolean => {
		let refLines = 0;
		let headerRows = 0;
		for (const line of block) {
			if (isBlank(line) || isTableSeparator(line)) continue;
			const refs = knownRefs(line);
			if (refs === null) return false;
			if (refs > 0 && isListy(line)) {
				refLines++;
				continue;
			}
			if (refs === 0 && isTableRow(line) && headerRows === 0) {
				headerRows++; // "| § | Bron |"-kopregel
				continue;
			}
			return false;
		}
		return refLines > 0;
	};

	const lines = answer.split('\n');
	const remove = new Array<boolean>(lines.length).fill(false);

	// 1. "Regelbasis"-kop waarvan het blok alleen de citatielijst dubbelt.
	for (let i = 0; i < lines.length; i++) {
		if (!isRuleBasisHeading(lines[i])) continue;
		let end = i + 1;
		while (end < lines.length && !isHeadingLine(lines[end])) end++;
		if (isPureRefBlock(lines.slice(i + 1, end)))
			for (let j = i; j < end; j++) remove[j] = true;
	}

	// 2. Losse §-tabellen die alleen bekende verwijzingen bevatten.
	for (let i = 0; i < lines.length; i++) {
		if (remove[i] || !isTableRow(lines[i])) continue;
		let end = i;
		while (end < lines.length && isTableRow(lines[end])) end++;
		if (end - i >= 2 && isPureRefBlock(lines.slice(i, end)))
			for (let j = i; j < end; j++) remove[j] = true;
		i = end - 1;
	}

	if (!remove.some(Boolean)) return answer;
	return lines
		.filter((_, i) => !remove[i])
		.join('\n')
		.replace(/\n{3,}/g, '\n\n')
		.trim();
}
