// Antwoordformat-helpers (#69): de citatielijst onder het antwoord is de
// enige plek met regelsecties. citationEssence maakt de dichtgeklapte citatie
// leesbaar (eerste zin als één-regel-essentie); stripDuplicateRuleRefs is het
// vangnet voor als het model tóch een "Regelbasis"-blok of §-tabel produceert
// die de citatielijst alleen dubbelt.
import { isKnownKeyword } from '$lib/rbtokens';

export interface CitationRef {
	n: number;
	section: string | null;
}

/** Normaliseert een §-code uit een widget-marker naar de opslagvorm (zonder
 *  §-prefix of trailing punt, zoals RuleSectionParser ze opslaat). AnswerView
 *  gebruikt dit voor de dedup-sleutel ÉN de doorgegeven waarde — dezelfde
 *  functie, anders verdringt een slordige variant ("348.", "§348") de
 *  matchende widget en blijft alleen de kapotte fallback over (review #359). */
export function normalizeRuleCode(code: string): string {
	return code.trim().replace(/^§\s*/, '').replace(/\.+$/, '');
}

/** Kleur-niveau van het zekerheidslabel in de oordeel-banner. #51 breidt het
 *  vocabulaire uit met "Community-consensus (N bronnen)" — een eigen niveau,
 *  visueel onderscheiden van officieel bevestigd/afgeleid. #366 voegt het
 *  GELEEFDE vocabulaire toe: het model schrijft in de praktijk "Zeker",
 *  "Hoog — … is expliciet", "Zeker (voor onderstaande gevallen)" — met
 *  alleen het prompt-vocabulaire viel vrijwel alles op kleurloos 'unsure'
 *  terug. Matching gebeurt op het EERSTE woord (na trim/lowercase), nooit
 *  substring-ergens: "Onzeker" bevat 'zeker' en "Redelijk zeker" ook —
 *  beide mogen nooit 'ok' worden. */
const CERTAINTY_WORDS: Array<['ok' | 'warn' | 'community' | 'unsure', string[]]> = [
	// 'onzeker'/'onbekend' bewust vóór 'zeker': met prefix-op-eerste-woord is
	// dat niet strikt nodig ("onzeker".startsWith('zeker') is al false), maar
	// zo blijft de volgorde ongevaarlijk als iemand de matching ooit verruimt.
	['unsure', ['onzeker', 'onbekend']],
	['ok', ['zeker', 'hoog', 'bevestigd']],
	['warn', ['afgeleid', 'waarschijnlijk', 'gedeeltelijk', 'laag', 'twijfel']],
	['community', ['community']]
];

export function certaintyLevel(
	zekerheid: string | null | undefined
): 'ok' | 'warn' | 'community' | 'unsure' {
	// Eerste woord = letters/koppeltekens vanaf het begin; "Hoog —", "Zeker
	// (…" en "Bevestigd door …" leveren zo hun kopwoord, en
	// "Community-consensus" blijft één woord.
	const first = (zekerheid ?? '').trim().toLowerCase().match(/^[a-z-]*/)?.[0] ?? '';
	for (const [level, words] of CERTAINTY_WORDS)
		if (words.some((w) => first.startsWith(w))) return level;
	return 'unsure';
}

/** Splitst het zekerheidslabel voor de chip-weergave (#366): het kopdeel vóór
 *  het eerste " — " of de eerste "(" wordt de gekleurde chip, de rest blijft
 *  als muted toelichting ernaast. Het " — " zelf vervalt (de chip-rand
 *  scheidt al); een "("-rest houdt zijn haakje. Zou de kop leeg worden
 *  (label begint met een scheider), dan blijft het hele label de chip —
 *  nooit een lege chip renderen. */
export function certaintyParts(zekerheid: string): { head: string; rest: string } {
	const whole = { head: zekerheid.trim(), rest: '' };
	const dash = zekerheid.indexOf(' — ');
	const paren = zekerheid.indexOf('(');
	const cuts = [
		...(dash >= 0 ? [{ at: dash, skip: 3 }] : []),
		...(paren >= 0 ? [{ at: paren, skip: 0 }] : [])
	];
	if (cuts.length === 0) return whole;
	const cut = cuts.reduce((a, b) => (a.at <= b.at ? a : b));
	const head = zekerheid.slice(0, cut.at).trim();
	if (!head) return whole;
	return { head, rest: zekerheid.slice(cut.at + cut.skip).trim() };
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

/** Opaque segmenten (review #363): stukken waar géén normalisatie-pass in mag
 *  schrijven, omdat de inhoud al machine-syntax of verbatim citaat is:
 *  - complete widget-markers — `[[rule:§348]]` is echt model-gedrag; de kale
 *    §-code erin herschrijven sloopt de widget en toont machine-syntax;
 *  - complete markdown-links — herschrijven in de linktekst geeft geneste
 *    links ("[Core Rules § 308.1](…)"), en een `[1](url)`-label vervangen
 *    laat de URL als losse tekst achter;
 *  - inline code-spans — letterlijke weergave is daar het hele punt;
 *  - aanhalingsteken-spans binnen één regel (recht én typografisch) —
 *    verbatim geciteerde kaart-/regeltekst ("[Reaction] — Add [1].") mag
 *    nooit herschreven worden; de kost-bracket is geen citatienummer.
 *  De maxlengte (300) voorkomt dat een ongepaard aanhalingsteken samen met
 *  een veel later teken de halve tekst als "quote" opeist; newlines zijn in
 *  geen enkel opaque segment toegestaan om hetzelfde over regels heen te
 *  voorkomen. Bij overlap wint de linksbuitenste match (regex-alternatie). */
const OPAQUE_RE =
	/\[\[(?:rule|card):[^\]\n]+\]\]|\[[^\]\n]*\]\([^)\n]*\)|(`+)[^`\n]*\1|"[^"\n]{0,300}"|“[^”\n]{0,300}”/g;

/** Draait `fn` alleen over de vrije stukken tussen de opaque segmenten en
 *  laat de opaque segmenten zelf byte-voor-byte staan. */
function mapFree(text: string, fn: (free: string) => string): string {
	let out = '';
	let last = 0;
	for (const m of text.matchAll(OPAQUE_RE)) {
		out += fn(text.slice(last, m.index)) + m[0];
		last = m.index + m[0].length;
	}
	return out + fn(text.slice(last));
}

/** Deterministische verwijzings- en keyword-normalisatie (#363). Het model
 *  wordt gestuurd om nette verwijzingen te schrijven, maar sturen is geen
 *  garanderen — dit is de afdwing-laag, en hij werkt daardoor ook voor alle
 *  bestaande antwoorden in de geschiedenis:
 *  - `[n]` → een klikbare §-link via de citatielijst (die kent n→sectie);
 *    zonder bijpassende sectie blijft `[n]` staan (nooit informatie weggooien).
 *  - `[n→§x]` / `[→§x]` → §-link (de expliciet genoemde code wint).
 *  - kale `§x` in lopende tekst → áltijd een §-link, ook buiten de
 *    citatielijst (#363-aanvulling): het model noemt geregeld echte secties
 *    die niet geciteerd zijn ("De stapelregel (§809.2)"), en die dood laten
 *    is erger dan een dode link op een tikfout — /rules/{code} vangt
 *    onbekende codes zelf af. De `[n]`-tak blijft WEL citatie-gebonden:
 *    een kaal nummer zonder citatie is geen verwijzing.
 *  - `**Keyword**` → `[Keyword]` tegen het gesloten vocabulaire uit
 *    rbtokens, zodat de badge-rendering ze oppakt; gewone vette nadruk
 *    blijft vet.
 *  Alle verwijzingsvormen zitten in ÉÉN gecombineerde regex-pass: de
 *  geproduceerde linktekst bevat zelf "§ code" en zou in een tweede pass
 *  opnieuw matchen (geneste links). */
const REF_RE =
	/\[(?:\d{1,2}\s*)?→\s*§?\s*([0-9][0-9a-z.]*?)\.?\]|\[(\d{1,2})\]|§\s*([0-9][0-9a-z]*(?:\.[0-9a-z]+)*)/gi;

export function normalizeAnswerRefs(text: string, citations: CitationRef[]): string {
	if (!text) return text;
	const byN = new Map<number, string>();
	for (const c of citations) {
		if (!c.section) continue;
		const code = c.section.replace(/^§\s*/, '').trim();
		if (!byN.has(c.n)) byN.set(c.n, code);
	}
	const link = (code: string) => `[§ ${code}](/rules/${encodeURIComponent(code)})`;
	const refPass = (free: string) =>
		free.replace(REF_RE, (whole, arrow: string, n: string, bare: string) => {
			if (arrow) return link(arrow);
			if (n) {
				const code = byN.get(Number(n));
				return code ? link(code) : whole;
			}
			return link(bare);
		});
	const keywordPasses = (free: string) =>
		free
			.replace(/\*\*([A-Z][A-Za-z-]*(?: \d{1,2})?)\*\*/g, (whole, kw: string) =>
				isKnownKeyword(kw) ? `[${kw}]` : whole
			)
			// Kale magnitude-vorm ("Deflect 1", "Assault 2"): een vocabulairewoord
			// mét cijfer is altijd een keyword.
			.replace(/(^|[^[\w*])([A-Z][A-Za-z-]*) (\d{1,2})\b(?!\]|\*)/gm, (whole, pre: string, kw: string, n: string) =>
				isKnownKeyword(kw) ? `${pre}[${kw} ${n}]` : whole
			)
			// Kale vocabulairewoorden ("met Repeat (1 extra)", tabelcellen als
			// "Deflect (1e keuze)") — met een naam-waarborg: gevolgd door nóg een
			// hoofdletterwoord is het waarschijnlijk een kaartnaam ("Hidden
			// Blade", "Legion Rearguard") en blijft het heel. Samenstellingen met
			// een kleine letter na het koppelteken badgen alleen de kop
			// ("Repeat-extra" → "[Repeat]-extra", zoals "[Action]-keyword").
			.replace(
				/(^|[^\w[*-])([A-Z][A-Za-z]*(?:-[A-Za-z]+)*)(?!\s+[A-Z])(?=[^\w\]*-]|$)/gm,
				(whole, pre: string, token: string) => {
					if (isKnownKeyword(token)) return `${pre}[${token}]`;
					const dash = token.indexOf('-');
					if (dash > 0) {
						const head = token.slice(0, dash);
						const rest = token.slice(dash);
						if (isKnownKeyword(head) && /^-[a-z]/.test(rest)) return `${pre}[${head}]${rest}`;
					}
					return whole;
				}
			)
			// Uitgeschreven kosten in proza ("(1 Energy)") → het echte glyph-token;
			// boven de gevendorde 12 blijft de tekst staan (rbtokens-allowlist).
			.replace(/\b(\d{1,2}) Energy\b/g, (whole, n: string) =>
				Number(n) <= 12 ? `:rb_energy_${n}:` : whole
			);
	// Elke pass draait alleen over de vrije stukken tussen de opaque segmenten
	// (widget-markers, links, code-spans, quotes) — zie OPAQUE_RE. De ref-pass
	// eerst; daarna wordt de tekst opníeuw gesplitst, zodat de zojuist
	// geproduceerde `[§ code](/rules/…)`-links zelf opaque geworden zijn en de
	// keyword-passes ze per constructie niet kunnen raken. Dat is een
	// structurele garantie in plaats van een afhankelijkheid van de precieze
	// tekenklassen in de keyword-regexes.
	return mapFree(mapFree(text, refPass), keywordPasses);
}

const MARKER_RE = /\[\[(rule|card):([^\]]+)\]\]/g;

/** Widget-markers gezond maken vóór segmentatie (#363, "zinnen-gat"). De
 *  prompt vraagt markers op een eigen regel en nooit dubbel, maar het model
 *  houdt zich daar niet altijd aan — en de oude dedup gooide een dubbele
 *  marker gewoon wég, waardoor "De tekst van [[card:X]] luidt:" als "De
 *  tekst van  luidt:" renderde. Hier geldt:
 *  - een marker midden in een zin wordt inline vervangen (kaart → vette
 *    naam, rule → §-link die als chip rendert); is het de éérste keer, dan
 *    komt de widget-marker alsnog op een eigen regel ná deze alinea;
 *  - een dubbele marker (waar dan ook) wordt alleen inline vervangen —
 *    nooit meer een gat;
 *  - een marker die al netjes alleen op zijn regel staat en uniek is,
 *    blijft ongemoeid.
 *  Bewust GEEN quote-/code-bescherming zoals in normalizeAnswerRefs: markers
 *  vervangen is juist de taak van deze functie, en een marker ís machine-
 *  syntax — óók binnen aanhalingstekens. Een "[[card:X]] …"-citaat verbatim
 *  laten staan zou de rauwe marker aan de lezer tonen; inline vervangen door
 *  de naam/§-link is dan de minst slechte weergave. De geproduceerde
 *  `**naam**`/`[§ …](…)`-vervangingen zijn veilig omdat deze functie ná
 *  normalizeAnswerRefs draait (zie AnswerView) en dus niets ze nog herschrijft. */
export function prepareAnswerMarkers(text: string): string {
	if (!text) return text;
	const seen = new Set<string>();
	const keyOf = (kind: string, v: string) =>
		`${kind}:${(kind === 'rule' ? normalizeRuleCode(v) : v).toLowerCase()}`;
	const inlineOf = (kind: string, v: string) => {
		if (kind === 'card') return `**${v}**`;
		const code = normalizeRuleCode(v);
		return `[§ ${code}](/rules/${encodeURIComponent(code)})`;
	};

	const out: string[] = [];
	for (const line of text.split('\n')) {
		const alone = line.trim().match(/^\[\[(rule|card):([^\]]+)\]\]$/);
		if (alone) {
			const key = keyOf(alone[1], alone[2].trim());
			if (seen.has(key)) continue; // losse dubbele regel: gewoon weglaten
			seen.add(key);
			out.push(line);
			continue;
		}
		const queue: string[] = [];
		const newLine = line.replace(MARKER_RE, (_, kind: string, raw: string) => {
			const v = raw.trim();
			const key = keyOf(kind, v);
			if (!seen.has(key)) {
				seen.add(key);
				queue.push(`[[${kind}:${v}]]`);
			}
			return inlineOf(kind, v);
		});
		out.push(newLine);
		if (queue.length) out.push('', ...queue);
	}
	return out.join('\n');
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
