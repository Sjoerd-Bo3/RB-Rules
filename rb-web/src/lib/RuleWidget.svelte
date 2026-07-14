<script lang="ts">
	interface Parent { code: string; text: string }
	interface CitationLike {
		section: string | null;
		sourceName: string;
		url: string;
		text: string | null;
		pdfUrl: string | null;
		page: number | null;
		parents: Parent[] | null;
		/** Temporele precedentie (#168): zie citationDateLabel in /ask. */
		publishedAt?: string | null;
		updatedAt?: string | null;
	}

	let { code, citations }: { code: string; citations: CitationLike[] } = $props();
	const cite = $derived(citations.find((c) => c.section === code) ?? null);

	function dateLabel(c: CitationLike): string | null {
		if (c.updatedAt) return `laatst bijgewerkt ${new Date(c.updatedAt).toLocaleDateString('nl-NL')}`;
		if (c.publishedAt) return `geldig sinds ${new Date(c.publishedAt).toLocaleDateString('nl-NL')}`;
		return null;
	}
</script>

{#if cite}
	<details class="rule-widget">
		<summary>
			<span class="sec-badge">§ {code}</span>
			<span class="src">{cite.sourceName}{#if dateLabel(cite)} · {dateLabel(cite)}{/if}</span>
			<span class="hint-open">lees de regel</span>
		</summary>
		{#if cite.parents?.length}
			<div class="parents">
				{#each cite.parents as p (p.code)}
					<p><a href="/rules/{encodeURIComponent(p.code)}">§ {p.code}</a> {p.text}</p>
				{/each}
			</div>
		{/if}
		{#if cite.text}<p class="body">{cite.text}</p>{/if}
		<p class="links">
			<a href="/rules/{encodeURIComponent(code)}">Sectiepagina</a>
			{#if cite.pdfUrl}
				· <a href="{cite.pdfUrl}{cite.page ? `#page=${cite.page}` : ''}" target="_blank" rel="noopener">
					Officiële PDF{cite.page ? ` (p. ${cite.page})` : ''}</a>
			{/if}
		</p>
	</details>
{:else}
	<p class="rule-fallback"><a href="/rules/{encodeURIComponent(code)}">§ {code} — bekijk de regel</a></p>
{/if}

<style>
	.rule-widget {
		background: var(--surface-deep); border: 1px solid var(--ok);
		border-radius: 10px; padding: 8px 14px; margin: 10px 0;
	}
	summary { cursor: pointer; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
	.sec-badge {
		font-weight: 700; color: var(--ok); background: var(--ok-soft);
		border-radius: 999px; padding: 2px 10px; font-size: 0.85rem;
	}
	.src { color: var(--muted); font-size: 0.82rem; }
	.hint-open { margin-left: auto; color: var(--muted); font-size: 0.78rem; }
	.parents { border-left: 2px solid var(--border); padding-left: 10px; margin: 8px 0 0; }
	.parents p { margin: 4px 0; color: var(--muted); font-size: 0.85rem; }
	.parents a { color: var(--muted); font-weight: 700; text-decoration: none; }
	.body { margin: 8px 0 4px; line-height: 1.6; }
	.links { margin: 6px 0 2px; font-size: 0.85rem; }
	.links a { color: var(--ok); text-decoration: none; font-weight: 600; }
	.rule-fallback { margin: 8px 0; }
	.rule-fallback a { color: var(--ok); font-weight: 600; }
</style>
