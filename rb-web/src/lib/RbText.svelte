<script lang="ts">
	import { renderCardText } from '$lib/rbtokens';

	let { text }: { text: string | null | undefined } = $props();
	const html = $derived(renderCardText(text ?? ''));
</script>

<!-- eslint-disable-next-line svelte/no-at-html-tags — tekst is ge-escaped vóór icoon-injectie -->
{@html html}
