<script lang="ts">
	import { enhance } from '$app/forms';
	import { renderMarkdown } from '$lib/markdown';

	let { form } = $props();
	let busy = $state(false);
	let phase = $state(0);
	let question = $state('');

	const EXAMPLES = [
		'Wanneer mag ik een unit moven, en wat telt niet als move?',
		'Wat doet Deflect tegen een spell die meerdere targets kiest?',
		'Mag ik reageren tijdens een showdown, en in welke volgorde?',
		'Wat gebeurt er met een Hidden unit die getarget wordt?'
	];

	// Antwoorden duren 10–30s (LLM op abonnement) — toon wat er gebeurt.
	const PHASES = [
		'Vraag insturen',
		'Relevante regelsecties zoeken (semantisch + full-text)',
		'Antwoord formuleren met §-citaten',
		'Bijna klaar — antwoord controleren'
	];
	$effect(() => {
		if (!busy) { phase = 0; return; }
		const t = setInterval(() => { if (phase < PHASES.length - 1) phase += 1; }, 6000);
		return () => clearInterval(t);
	});

	interface HistItem { q: string; at: number; }
	let history = $state<HistItem[]>([]);
	$effect(() => {
		try {
			history = JSON.parse(localStorage.getItem('rb-ask-history') ?? '[]');
		} catch { history = []; }
	});
	function remember(q: string) {
		history = [{ q, at: Date.now() }, ...history.filter((h) => h.q !== q)].slice(0, 8);
		localStorage.setItem('rb-ask-history', JSON.stringify(history));
	}

	const answerHtml = $derived(form?.answer ? renderMarkdown(form.answer) : null);
</script>

<svelte:head><title>Vraag een ruling — RB Rules</title></svelte:head>

<main>
	<h1>Vraag een <span>ruling</span></h1>
	<p class="subtitle">Antwoord met exacte §-citaten uit de officiële regels.</p>

	<div class="examples">
		{#each EXAMPLES as ex (ex)}
			<button type="button" class="chip" onclick={() => (question = ex)}>{ex}</button>
		{/each}
	</div>

	<form
		method="POST"
		use:enhance={({ formData }) => {
			busy = true;
			const q = String(formData.get('question') ?? '').trim();
			if (q) remember(q);
			return async ({ update }) => {
				busy = false;
				await update();
			};
		}}
		class="panel form"
	>
		<textarea
			name="question"
			rows="3"
			placeholder="Beschrijf de situatie of stel je regelvraag…"
			bind:value={question}
		></textarea>
		<button type="submit" disabled={busy}>{busy ? 'Bezig…' : 'Vraag'}</button>
	</form>

	{#if busy}
		<div class="panel waiting">
			<span class="spin"></span>
			<div>
				<p class="phase">{PHASES[phase]}</p>
				<p class="meta">Een goed onderbouwde ruling kost 10–30 seconden.</p>
			</div>
		</div>
	{/if}

	{#if form?.error}<p class="warn">{form.error}</p>{/if}

	{#if answerHtml && !busy}
		<article class="panel answer-panel">
			{#if form?.question}<p class="asked meta">Vraag: {form.question}</p>{/if}
			<!-- eslint-disable-next-line svelte/no-at-html-tags — bron is ge-escaped vóór markdown-parse -->
			<div class="md">{@html answerHtml}</div>
			{#if form?.citations?.length}
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

	{#if history.length && !busy}
		<h2 class="hist-title">Eerdere vragen</h2>
		<ul class="history">
			{#each history as h (h.at)}
				<li><button type="button" class="chip" onclick={() => (question = h.q)}>{h.q}</button></li>
			{/each}
		</ul>
	{/if}
</main>

<style>
	main { max-width: 860px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: var(--accent); }
	.subtitle, .meta { color: var(--muted); }
	.examples { display: flex; flex-wrap: wrap; gap: 8px; margin: 12px 0 14px; }
	.chip {
		background: var(--surface); color: var(--muted); border: 1px solid var(--border);
		border-radius: 999px; padding: 6px 14px; font-size: 0.85rem; cursor: pointer;
		text-align: left;
	}
	.chip:hover { color: var(--text); border-color: var(--border-strong); }
	.form { padding: 16px; margin-bottom: 16px; }
	textarea {
		width: 100%; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px;
		resize: vertical;
	}
	button[type='submit'] {
		margin-top: 10px; background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 9px 18px; font-weight: 600; cursor: pointer;
	}
	button[type='submit']:disabled { opacity: 0.55; }
	.waiting { display: flex; gap: 14px; align-items: center; padding: 16px; margin-bottom: 16px; }
	.phase { margin: 0 0 2px; font-weight: 600; }
	.waiting .meta { margin: 0; font-size: 0.85rem; }
	.answer-panel { padding: 18px 20px; }
	.asked { margin: 0 0 4px; font-size: 0.85rem; }
	h2 { font-size: 1rem; color: var(--accent); margin: 16px 0 6px; }
	ol { margin: 0; padding-left: 20px; }
	.sec {
		color: var(--ok); text-decoration: none; font-weight: 600;
		background: var(--ok-soft); border-radius: 999px; padding: 1px 9px; margin-left: 6px;
	}
	.hist-title { margin-top: 26px; }
	.history { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
	.warn { color: var(--err); }
</style>
