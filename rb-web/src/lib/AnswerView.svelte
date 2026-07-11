<script lang="ts">
	import { renderMarkdown } from '$lib/markdown';
	import { certaintyLevel, stripDuplicateRuleRefs } from '$lib/answerFormat';
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
		// citatielijst onderaan dubbelt wordt niet nogmaals getoond.
		let text = stripDuplicateRuleRefs(answer, citations);
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
			const key = `${m[1]}:${m[2].trim().toLowerCase()}`;
			if (!seen.has(key)) {
				segs.push({ kind: m[1] as 'rule' | 'card', value: m[2].trim() });
				seen.add(key);
			}
			last = m.index + m[0].length;
		}
		if (last < text.length) segs.push({ kind: 'md', value: text.slice(last) });
		return { oordeel, zekerheid, segs };
	});

	const zLevel = $derived(certaintyLevel(parsed.zekerheid));
</script>

{#if parsed.oordeel}
	<div class="verdict {zLevel}">
		<p class="verdict-text">{parsed.oordeel}</p>
		{#if parsed.zekerheid}<p class="certainty">{parsed.zekerheid}</p>{/if}
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
	.certainty { margin: 4px 0 0; color: var(--muted); font-size: 0.85rem; }
</style>
