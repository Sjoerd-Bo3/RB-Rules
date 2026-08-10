<script lang="ts">
	import { renderCardText } from '$lib/rbtokens';

	// keywords={false}: brontekst letterlijk tonen (haken blijven staan) —
	// voor review-oppervlakken waar tekstgetrouwheid het doel is (#359).
	let { text, keywords = true }: { text: string | null | undefined; keywords?: boolean } =
		$props();
	const html = $derived(renderCardText(text ?? '', { keywords }));
</script>

<!-- eslint-disable-next-line svelte/no-at-html-tags — tekst is ge-escaped vóór icoon-injectie -->
{@html html}
