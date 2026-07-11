<script lang="ts">
	import { renderMarkdown } from '$lib/markdown';
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
		let text = answer;
		let oordeel: string | null = null;
		let zekerheid: string | null = null;
		text = text.replace(/^\s*\*\*Oordeel:\*\*\s*(.+)$/m, (_, v: string) => {
			oordeel = v.trim();
			return '';
		});
		text = text.replace(/^\s*\*\*Zekerheid:\*\*\s*(.+)$/m, (_, v: string) => {
			zekerheid = v.trim();
			return '';
		});

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

	const zLevel = $derived.by(() => {
		const z = (parsed.zekerheid ?? '').toLowerCase();
		if (z.startsWith('bevestigd')) return 'ok';
		if (z.startsWith('afgeleid')) return 'warn';
		return 'unsure';
	});
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
	.verdict.unsure { border-color: var(--border-strong); }
	.verdict-text { margin: 0; font-size: 1.05rem; font-weight: 700; line-height: 1.5; }
	.certainty { margin: 4px 0 0; color: var(--muted); font-size: 0.85rem; }
</style>
