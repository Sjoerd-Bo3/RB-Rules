<script lang="ts">
	import { enhance } from '$app/forms';

	let { form } = $props();
	let busy = $state(false);
</script>

<svelte:head><title>Vraag een ruling — RB Rules</title></svelte:head>

<main>
	<h1>Vraag een <span>ruling</span></h1>
	<p class="subtitle">Antwoord met §-exacte citaten uit de officiële regels.</p>

	<form
		method="POST"
		use:enhance={() => {
			busy = true;
			return async ({ update }) => {
				busy = false;
				await update();
			};
		}}
		class="card"
	>
		<textarea
			name="question"
			rows="3"
			placeholder="Bijv.: Wat gebeurt er als een Hidden unit met Tank getarget wordt tijdens een showdown?"
			>{form?.question ?? ''}</textarea
		>
		<button type="submit" disabled={busy}>{busy ? 'Bezig…' : 'Vraag'}</button>
	</form>

	{#if form?.error}<p class="warn">{form.error}</p>{/if}

	{#if form?.answer}
		<article class="card">
			<p class="answer">{form.answer}</p>
			{#if form.citations?.length}
				<h2>Bronnen</h2>
				<ol>
					{#each form.citations as c (c.n)}
						<li>
							<a href={c.url} target="_blank" rel="noreferrer">{c.sourceName}</a>
							<span class="meta">(trust {c.trust})</span>
							{#if c.section}
								<a class="sec" href="/rules/{encodeURIComponent(c.section)}">§ {c.section}</a>
							{/if}
						</li>
					{/each}
				</ol>
			{/if}
		</article>
	{/if}
</main>

<style>
	main { max-width: 860px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: #d98a4e; }
	.subtitle, .meta { color: #9fb0cc; }
	.card {
		background: #16233b; border: 1px solid #243551;
		border-radius: 12px; padding: 16px; margin-bottom: 16px;
	}
	textarea {
		width: 100%; background: #0b1322; color: #e7eefc;
		border: 1px solid #243551; border-radius: 8px; padding: 10px 12px;
		resize: vertical; box-sizing: border-box;
	}
	button {
		margin-top: 10px; background: #d98a4e; color: #1a1206; border: 0;
		border-radius: 8px; padding: 9px 16px; font-weight: 600; cursor: pointer;
	}
	button:disabled { opacity: 0.6; }
	.answer { white-space: pre-wrap; }
	h2 { font-size: 1rem; color: #d98a4e; margin: 16px 0 6px; }
	ol { margin: 0; padding-left: 20px; }
	a { color: #e7eefc; }
	.sec {
		color: #7fd1a8; text-decoration: none; font-weight: 600;
		background: #58c08a1a; border-radius: 999px; padding: 1px 9px; margin-left: 6px;
	}
	.warn { color: #ff8b8e; }
</style>
