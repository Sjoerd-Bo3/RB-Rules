<script lang="ts">
	import { enhance } from '$app/forms';
	import { renderMarkdown } from '$lib/markdown';
	import RbText from '$lib/RbText.svelte';

	let { form } = $props();
	let busy = $state(false);
	let phase = $state(0);
	let question = $state('');
	let correcting = $state(false);

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
		action="?/ask"
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
				<h2>Geciteerde regelsecties</h2>
				{#each form.citations as c (c.n)}
					<details class="cite">
						<summary>
							<span class="cite-n">[{c.n}]</span>
							{#if c.section}<strong>§ {c.section}</strong>{/if}
							<span class="meta">{c.sourceName} · trust {c.trust}</span>
						</summary>
						{#if c.text}<p class="cite-text">{c.text}</p>{/if}
						<p class="cite-links meta">
							{#if c.section}
								<a href="/rules/{encodeURIComponent(c.section)}">Sectiepagina</a>
							{/if}
							{#if c.pdfUrl}
								· <a href="{c.pdfUrl}{c.page ? `#page=${c.page}` : ''}" target="_blank" rel="noopener">
									Officiële PDF{c.page ? ` (pagina ${c.page})` : ''}</a>
							{:else}
								· <a href={c.url} target="_blank" rel="noreferrer">Officiële bron</a>
							{/if}
						</p>
					</details>
				{/each}
			{/if}

			{#if form?.cards?.length}
				<h2>Betrokken kaarten</h2>
				{#each form.cards as k (k.riftboundId)}
					<details class="cite card-detail">
						<summary>
							<strong>{k.name}</strong>
							<span class="meta">{[k.supertype, k.type].filter(Boolean).join(' ')} · {k.domains.join('/') || '—'}</span>
							{#if k.banned}<span class="ban">Verboden</span>{/if}
						</summary>
						<div class="card-body">
							{#if k.imageUrl}<img src={k.imageUrl} alt={k.name} loading="lazy" />{/if}
							<div>
								<p class="meta">
									{#if k.energy !== null}Energy {k.energy}{/if}
									{#if k.might !== null}&nbsp;· Might {k.might}{/if}
									{#if k.mechanics?.length}&nbsp;· {k.mechanics.join(', ')}{/if}
								</p>
								{#if k.textPlain}<p class="oracle"><RbText text={k.textPlain} /></p>{/if}
								<a href="/cards/{k.riftboundId}">Naar kaartpagina →</a>
							</div>
						</div>
					</details>
				{/each}
			{/if}

			<!-- Self-learning: feedback wordt beoordeeld en stuurt daarna antwoorden -->
			<div class="feedback">
				{#if form?.feedbackSent}
					<p class="meta">
						{form.feedbackSent === 'up'
							? 'Bedankt voor de bevestiging.'
							: 'Bedankt — je correctie staat in de reviewqueue en stuurt na verificatie toekomstige antwoorden.'}
					</p>
				{:else}
					<span class="meta">Was dit antwoord juist?</span>
					<form method="POST" action="?/feedback" use:enhance class="fb-inline">
						<input type="hidden" name="question" value={form?.question ?? ''} />
						<input type="hidden" name="answer" value={form?.answer ?? ''} />
						<input type="hidden" name="citations" value={JSON.stringify(form?.citations ?? [])} />
						<input type="hidden" name="cards" value={JSON.stringify(form?.cards ?? [])} />
						<input type="hidden" name="verdict" value="up" />
						<button class="fb">Ja</button>
					</form>
					<button class="fb" type="button" onclick={() => (correcting = !correcting)}>Nee, corrigeer</button>
				{/if}
			</div>
			{#if correcting && !form?.feedbackSent}
				<form method="POST" action="?/feedback" use:enhance={() => async ({ update }) => { correcting = false; await update(); }} class="correct-form">
					<input type="hidden" name="question" value={form?.question ?? ''} />
					<input type="hidden" name="answer" value={form?.answer ?? ''} />
					<input type="hidden" name="citations" value={JSON.stringify(form?.citations ?? [])} />
					<input type="hidden" name="cards" value={JSON.stringify(form?.cards ?? [])} />
					<input type="hidden" name="verdict" value="down" />
					<textarea name="text" rows="3" placeholder="Wat is het juiste antwoord? Verwijs waar mogelijk naar een §-sectie."></textarea>
					<button type="submit">Verstuur correctie</button>
				</form>
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
	.cite {
		background: var(--surface-deep); border: 1px solid var(--border);
		border-radius: 8px; padding: 8px 12px; margin-bottom: 6px;
	}
	.cite summary { cursor: pointer; }
	.cite-n { color: var(--muted); font-size: 0.85rem; margin-right: 4px; }
	.cite summary .meta { margin-left: 8px; font-size: 0.82rem; }
	.cite-text { margin: 8px 0 4px; line-height: 1.6; }
	.cite-links a { color: var(--ok); text-decoration: none; font-weight: 600; }
	.card-body { display: flex; gap: 14px; margin-top: 8px; }
	.card-body img { width: 120px; border-radius: 8px; border: 1px solid var(--border); align-self: flex-start; }
	.card-body .oracle {
		background: var(--surface); border: 1px solid var(--border);
		border-radius: 8px; padding: 8px 10px; margin: 6px 0;
	}
	.card-body a { color: var(--accent); text-decoration: none; font-size: 0.85rem; }
	.ban {
		font-size: 0.7rem; text-transform: uppercase; margin-left: 8px;
		background: var(--err-soft); color: var(--err); border-radius: 999px; padding: 2px 8px;
	}
	.feedback {
		display: flex; align-items: center; gap: 10px;
		border-top: 1px solid var(--border); margin-top: 16px; padding-top: 12px;
	}
	.fb {
		background: transparent; color: var(--muted); border: 1px solid var(--border);
		border-radius: 8px; padding: 4px 12px; font-size: 0.85rem; cursor: pointer;
	}
	.fb:hover { color: var(--text); border-color: var(--border-strong); }
	.fb-inline { display: inline; }
	.correct-form { margin-top: 10px; }
	.correct-form textarea {
		width: 100%; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px;
	}
	.correct-form button {
		margin-top: 8px; background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 7px 14px; font-weight: 600; cursor: pointer;
	}
	.hist-title { margin-top: 26px; }
	.history { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
	.warn { color: var(--err); }
</style>
