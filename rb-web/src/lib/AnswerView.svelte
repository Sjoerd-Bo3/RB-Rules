<script lang="ts">
	import { renderMarkdown, renderInlineMarkdown } from '$lib/markdown';
	import {
		certaintyLevel,
		certaintyParts,
		normalizeAnswerRefs,
		normalizeRuleCode,
		prepareAnswerMarkers,
		stripDuplicateRuleRefs
	} from '$lib/answerFormat';
	import RuleWidget from '$lib/RuleWidget.svelte';
	import CardWidget from '$lib/CardWidget.svelte';

	// Rijk antwoord: het LLM plaatst [[rule:…]]/[[card:…]]-markers; wij
	// renderen die als interactieve widgets met de citaten-/kaartendata die
	// toch al in de response zitten. Oordeel/Zekerheid worden een banner.
	let {
		answer,
		citations = [],
		cards = []
	}: {
		answer: string;
		// bewust los getypeerd: de widgets valideren zelf wat ze nodig hebben
		citations?: any[];
		cards?: any[];
	} = $props();

	type Seg = { kind: 'md' | 'rule' | 'card'; value: string };

	const parsed = $derived.by(() => {
		// Vangnet (#69): een "Regelbasis"-blok of §-tabel die alleen de
		// citatielijst onderaan dubbelt wordt niet nogmaals getoond. Daarná
		// de verwijzings-/keyword-normalisatie (#363) — stripDuplicateRuleRefs
		// moet de originele [n]/§-vormen nog zien om ref-blokken te herkennen.
		// prepareAnswerMarkers als laatste: die maakt van mid-zin- en dubbele
		// markers inline tekst/links, en normalizeAnswerRefs mag die nieuwe
		// linkteksten niet nogmaals herschrijven.
		let text = prepareAnswerMarkers(
			normalizeAnswerRefs(stripDuplicateRuleRefs(answer, citations), citations)
		);
		let oordeel: string | null = null;
		let zekerheid: string | null = null;
		// Twee vormen accepteren: "**Oordeel:** zin" én "## Oordeel\n\nzin"
		// (het model wijkt soms af naar koppen).
		text = text.replace(/^\s*\*\*Oordeel:\*\*\s*(.+)$/m, (_, v: string) => {
			oordeel = v.trim();
			return '';
		});
		text = text.replace(/^\s*\*\*Zekerheid:\*\*\s*(.+)$/m, (_, v: string) => {
			zekerheid = v.trim();
			return '';
		});
		if (!oordeel) {
			text = text.replace(/^#{1,3}\s*Oordeel\s*\n+([^\n#][^\n]*(?:\n(?!#{1,3}\s|---)[^\n]*)*)/m, (_, v: string) => {
				oordeel = v.replace(/\n+/g, ' ').trim();
				return '';
			});
		}
		if (!zekerheid) {
			text = text.replace(/^#{1,3}\s*Zekerheid\s*\n+([^\n#][^\n]*(?:\n(?!#{1,3}\s|---)[^\n]*)*)/m, (_, v: string) => {
				zekerheid = v.replace(/\n+/g, ' ').trim();
				return '';
			});
		}
		text = text.replace(/^---\s*$/gm, '');

		const segs: Seg[] = [];
		const re = /\[\[(rule|card):([^\]]+)\]\]/g;
		let last = 0;
		let m: RegExpExecArray | null;
		const seen = new Set<string>();
		while ((m = re.exec(text)) !== null) {
			if (m.index > last) segs.push({ kind: 'md', value: text.slice(last, m.index) });
			// Rule-codes normaliseren ("348." / "§348" → "348") vóór dedup ÉN
			// doorgifte, anders staat §348 er dubbel als het model beide vormen
			// schrijft (melding Sjoerd) — of erger: de slordige variant verdringt
			// de matchende widget en de exacte lookup in RuleWidget faalt.
			const value = m[1] === 'rule' ? normalizeRuleCode(m[2]) : m[2].trim();
			const key = `${m[1]}:${value.toLowerCase()}`;
			if (!seen.has(key)) {
				segs.push({ kind: m[1] as 'rule' | 'card', value });
				seen.add(key);
			}
			last = m.index + m[0].length;
		}
		if (last < text.length) segs.push({ kind: 'md', value: text.slice(last) });
		return { oordeel, zekerheid, segs };
	});

	const zLevel = $derived(certaintyLevel(parsed.zekerheid));
	// Zekerheid als chip (#366): kopdeel ("Zeker", "Hoog") in de niveaukleur,
	// de toelichting erachter blijft muted tekst. De splitsing zit in
	// answerFormat (unit-getest); hier alleen weergave.
	const zParts = $derived(parsed.zekerheid ? certaintyParts(parsed.zekerheid) : null);
</script>

{#if parsed.oordeel}
	<div class="verdict {zLevel}">
		<!-- eslint-disable-next-line svelte/no-at-html-tags — bron is ge-escaped vóór markdown-parse -->
		<p class="verdict-text">{@html renderInlineMarkdown(parsed.oordeel)}</p>
		{#if zParts}
			<p class="certainty">
				<span class="certainty-chip {zLevel}">{zParts.head}</span>
				{#if zParts.rest}
					<!-- eslint-disable-next-line svelte/no-at-html-tags — bron is ge-escaped vóór markdown-parse -->
					<span class="certainty-rest">{@html renderInlineMarkdown(zParts.rest)}</span>
				{/if}
			</p>
		{/if}
	</div>
{/if}

{#each parsed.segs as seg, i (i)}
	{#if seg.kind === 'md'}
		{#if seg.value.trim()}
			<!-- eslint-disable-next-line svelte/no-at-html-tags — bron is ge-escaped vóór markdown-parse -->
			<div class="md">{@html renderMarkdown(seg.value)}</div>
		{/if}
	{:else if seg.kind === 'rule'}
		<RuleWidget code={seg.value} {citations} />
	{:else}
		<CardWidget name={seg.value} {cards} />
	{/if}
{/each}

<style>
	.verdict {
		border-radius: 10px;
		padding: 12px 16px;
		margin-bottom: 14px;
		border: 1px solid var(--border);
		background: var(--surface-deep);
	}
	.verdict.ok { border-color: var(--ok); background: var(--ok-soft); }
	.verdict.warn { border-color: var(--warn); background: var(--warn-soft); }
	/* Community-consensus (#51): eigen kleur — geen officiële bevestiging. */
	.verdict.community { border-color: var(--accent); background: var(--accent-soft); }
	.verdict.unsure { border-color: var(--border-strong); }
	.verdict-text { margin: 0; font-size: 1.05rem; font-weight: 700; line-height: 1.5; }
	.certainty {
		margin: 6px 0 0;
		display: flex;
		align-items: baseline;
		gap: 8px;
		flex-wrap: wrap;
		color: var(--muted);
		font-size: 0.85rem;
	}
	/* Chip in de niveaukleur — zelfde tokens als de .verdict-box hierboven;
	   unsure blijft bewust neutraal (rand + muted, geen vulling). */
	.certainty-chip {
		display: inline-block;
		padding: 1px 9px;
		border-radius: 999px;
		border: 1px solid var(--border-strong);
		color: var(--muted);
		font-size: 0.75rem;
		font-weight: 600;
		white-space: nowrap;
	}
	.certainty-chip.ok { border-color: var(--ok); background: var(--ok-soft); color: var(--ok); }
	.certainty-chip.warn { border-color: var(--warn); background: var(--warn-soft); color: var(--warn); }
	/* Accent (geel) is als tekstkleur te licht op de soft-achtergrond; de
	   chip volgt daarom de .verdict.community-aanpak: accent-rand en -vulling
	   met de gewone tekstkleur. */
	.certainty-chip.community {
		border-color: var(--accent);
		background: var(--accent-soft);
		color: var(--text);
	}
</style>
