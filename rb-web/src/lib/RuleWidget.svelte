<script lang="ts">
	import RbText from '$lib/RbText.svelte';

	interface Parent { code: string; text: string }
	interface CitationLike {
		section: string | null;
		sourceName: string;
		url: string;
		text: string | null;
		pdfUrl: string | null;
		page: number | null;
		parents: Parent[] | null;
	}

	let { code, citations }: { code: string; citations: CitationLike[] } = $props();
	const cite = $derived(citations.find((c) => c.section === code) ?? null);
</script>

<!-- #360: regeltekst standaard zichtbaar (geen details/summary meer) en
     compact — de §-chip linkt zelf naar de sectiepagina, de bronnaam en
     bijwerkdatum verhuisden naar de citatielijst onderaan het antwoord
     (daar stonden ze al). De regeltekst gaat door RbText zodat glyphs en
     keyword-chips ook in citaten werken (#359). -->
{#if cite}
	<div class="rule-widget">
		<p class="head">
			<a class="sec-badge" href="/rules/{encodeURIComponent(code)}" title={cite.sourceName}>
				§ {code}</a>
			{#if cite.pdfUrl}
				<a
					class="pdf"
					href="{cite.pdfUrl}{cite.page ? `#page=${cite.page}` : ''}"
					target="_blank"
					rel="noopener">PDF{cite.page ? ` p. ${cite.page}` : ''}</a>
			{/if}
		</p>
		{#if cite.parents?.length}
			<div class="parents">
				{#each cite.parents as p (p.code)}
					<p>
						<a href="/rules/{encodeURIComponent(p.code)}">§ {p.code}</a>
						<RbText text={p.text} />
					</p>
				{/each}
			</div>
		{/if}
		{#if cite.text}<p class="body"><RbText text={cite.text} /></p>{/if}
	</div>
{:else}
	<!-- Zonder bijpassende citatie (model noemt een § buiten de lijst): zelfde
	     vorm, zodat het geen losse zwevende link is (melding Sjoerd, #360).
	     Eén anchor — badge en tekst samen — anders twee tab-stops naar
	     dezelfde URL (review). -->
	<p class="rule-fallback">
		<a href="/rules/{encodeURIComponent(code)}">
			<span class="sec-badge">§ {code}</span>
			<span class="more">bekijk de regel</span>
		</a>
	</p>
{/if}

<style>
	.rule-widget {
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-left: 3px solid var(--ok);
		border-radius: 8px;
		padding: 7px 12px 8px;
		margin: 8px 0;
	}
	.head {
		margin: 0;
		display: flex;
		gap: 10px;
		align-items: baseline;
	}
	.sec-badge {
		font-weight: 700;
		color: var(--ok);
		background: var(--ok-soft);
		border-radius: 999px;
		padding: 1px 9px;
		font-size: 0.8rem;
		text-decoration: none;
	}
	.pdf {
		margin-left: auto;
		color: var(--muted);
		font-size: 0.78rem;
		text-decoration: none;
	}
	.pdf:hover {
		color: var(--ok);
	}
	.parents {
		margin: 5px 0 0;
	}
	.parents p {
		margin: 2px 0;
		color: var(--muted);
		font-size: 0.82rem;
		line-height: 1.45;
	}
	.parents a {
		color: var(--muted);
		font-weight: 700;
		text-decoration: none;
	}
	.body {
		margin: 5px 0 0;
		line-height: 1.55;
		font-size: 0.95rem;
	}
	.rule-fallback {
		margin: 8px 0;
	}
	.rule-fallback a {
		display: inline-flex;
		gap: 8px;
		align-items: baseline;
		text-decoration: none;
	}
	.rule-fallback .more {
		color: var(--ok);
		font-size: 0.85rem;
		font-weight: 600;
	}
</style>
