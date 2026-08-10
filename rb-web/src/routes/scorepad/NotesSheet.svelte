<script lang="ts">
	// Notes (#342): losse notitiepagina — puntenraster of lijntjes.
	import SheetFrame from './SheetFrame.svelte';

	let { style = 'dots', bw = false }: { style?: 'dots' | 'lines'; bw?: boolean } = $props();
</script>

<SheetFrame {bw} title="Notes" sub="">
	<div class="sh-row">
		<span class="sh-field"><span class="micro">Date</span><span class="fill"></span></span>
		<span class="sh-field" style="flex: 2"
			><span class="micro">Topic</span><span class="fill"></span></span
		>
	</div>
	<div class="area" class:dots={style === 'dots'} class:rlines={style === 'lines'}></div>
</SheetFrame>

<style>
	.area {
		flex: 1;
		margin-bottom: 1mm;
	}
</style>
