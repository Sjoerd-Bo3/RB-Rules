<script lang="ts">
	import AnswerView from '$lib/AnswerView.svelte';

	// Eigen ask-geschiedenis (#157): uitklap-paneel, dicht standaard. rb-api
	// heeft de scope (user_id/ip_hash) al bepaald — hier alleen tonen.
	interface AskHistoryItem {
		id: number;
		question: string;
		createdAt: string;
		questionType: string | null;
		answer: string | null;
		agentic: boolean;
	}

	let { items, loggedIn }: { items: AskHistoryItem[]; loggedIn: boolean } = $props();
</script>

<details class="panel history-panel">
	<summary>
		<span>Mijn eerdere vragen</span>
		{#if items.length}<span class="meta">({items.length})</span>{/if}
	</summary>
	<p class="meta small privacy-note">
		{loggedIn
			? 'Deze geschiedenis hangt aan je account.'
			: 'Deze geschiedenis hangt aan je IP-adres/apparaat en verdwijnt bij een IP-wissel — log in om hem aan je account te koppelen.'}
	</p>
	{#if items.length === 0}
		<p class="meta">Nog geen eerdere vragen{loggedIn ? '' : ' op dit IP'}.</p>
	{:else}
		{#each items as item (item.id)}
			<details class="hist-item">
				<summary>
					{#if item.questionType}<span class="badge ok-b">{item.questionType}</span>{/if}
					{#if item.agentic}<span class="badge warn-b">agentic</span>{/if}
					<span class="hist-q">{item.question}</span>
					<span class="meta">{new Date(item.createdAt).toLocaleString('nl-NL')}</span>
				</summary>
				{#if item.answer}
					<AnswerView answer={item.answer} />
				{:else}
					<p class="meta">Voor deze vraag is geen antwoord bewaard.</p>
				{/if}
			</details>
		{/each}
	{/if}
</details>

<style>
	.history-panel { padding: 14px 18px; margin-bottom: 16px; }
	.history-panel > summary {
		cursor: pointer; font-weight: 700; display: flex; gap: 8px; align-items: center;
	}
	.privacy-note { margin: 8px 0 10px; }
	.hist-item {
		background: var(--surface-deep); border: 1px solid var(--border);
		border-radius: 10px; padding: 8px 14px; margin-bottom: 8px;
	}
	.hist-item summary {
		cursor: pointer; display: flex; gap: 10px; align-items: center; flex-wrap: wrap;
	}
	.hist-q {
		flex: 1; min-width: 160px; overflow: hidden;
		text-overflow: ellipsis; white-space: nowrap;
	}
	.small { font-size: 0.78rem; }
</style>
