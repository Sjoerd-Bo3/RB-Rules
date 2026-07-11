<script lang="ts">
	import { enhance } from '$app/forms';
	import RbText from '$lib/RbText.svelte';
	import AnswerView from '$lib/AnswerView.svelte';
	import { citationEssence } from '$lib/answerFormat';

	let { data, form } = $props();
	let busy = $state(false);
	let phase = $state(0);

	// Echte duurstatistiek (mediaan/p90 van de laatste vragen) i.p.v. schatting.
	const stats = $derived(data.stats);
	const waitText = $derived(
		stats.count > 0 && stats.medianMs
			? `Meestal ±${Math.round(stats.medianMs / 1000)}s, uitschieters tot ~${Math.round((stats.p90Ms ?? stats.medianMs) / 1000)}s — gemeten over de laatste ${stats.count} ${stats.count === 1 ? 'vraag' : 'vragen'}.`
			: 'Eerste metingen lopen nog — dit kan even duren.'
	);
	let question = $state('');
	let correcting = $state(false);
	let followUp = $state('');

	// Historie voor de volgende doorvraag: eerdere rondes + de huidige.
	const nextHistory = $derived(
		JSON.stringify([
			...((form?.history as { question: string; answer: string }[] | undefined) ?? []),
			...(form?.question && form?.answer ? [{ question: form.question, answer: form.answer }] : [])
		].slice(-3))
	);

	// Voorlezen (#31): antwoord zonder markdown/widget-markers.
	let speaking = $state(false);
	function toggleSpeech() {
		if (speaking) {
			speechSynthesis.cancel();
			speaking = false;
			return;
		}
		const plain = (form?.answer ?? '')
			.replace(/\[\[(rule|card):[^\]]+\]\]/g, '')
			.replace(/[#*_`>|-]/g, ' ')
			.replace(/\[(\d+)\]/g, '')
			.replace(/\s+/g, ' ')
			.trim();
		if (!plain) return;
		const u = new SpeechSynthesisUtterance(plain);
		u.lang = 'nl-NL';
		u.onend = () => (speaking = false);
		u.onerror = () => (speaking = false);
		speechSynthesis.speak(u);
		speaking = true;
	}

	// Board-state-foto: client verkleint naar max 1600px JPEG vóór upload.
	let photoInput = $state<HTMLInputElement | null>(null);
	let photoPreview = $state<string | null>(null);

	async function downscale(file: File): Promise<Blob> {
		const bitmap = await createImageBitmap(file);
		const scale = Math.min(1, 1600 / Math.max(bitmap.width, bitmap.height));
		if (scale === 1 && file.size < 1_500_000) return file;
		const canvas = document.createElement('canvas');
		canvas.width = Math.round(bitmap.width * scale);
		canvas.height = Math.round(bitmap.height * scale);
		canvas.getContext('2d')!.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
		return new Promise((resolve) =>
			canvas.toBlob((b) => resolve(b ?? file), 'image/jpeg', 0.85)
		);
	}

	function onPhotoChange() {
		const f = photoInput?.files?.[0];
		if (photoPreview) URL.revokeObjectURL(photoPreview);
		photoPreview = f ? URL.createObjectURL(f) : null;
	}

	function clearPhoto() {
		if (photoInput) photoInput.value = '';
		if (photoPreview) URL.revokeObjectURL(photoPreview);
		photoPreview = null;
	}

	const EXAMPLES = [
		'Wanneer mag ik een unit moven, en wat telt niet als move?',
		'Wat doet Deflect tegen een spell die meerdere targets kiest?',
		'Mag ik reageren tijdens een showdown, en in welke volgorde?',
		'Wat gebeurt er met een Hidden unit die getarget wordt?'
	];

	// Fase-tekst tijdens het wachten; tempo geschaald op de echte mediaan.
	const PHASES = [
		'Vraag insturen',
		'Relevante regelsecties zoeken (semantisch + full-text)',
		'Antwoord formuleren met §-citaten',
		'Bijna klaar — antwoord controleren'
	];
	$effect(() => {
		if (!busy) { phase = 0; return; }
		const interval = Math.max(2000, Math.round((stats.medianMs ?? 24_000) / PHASES.length));
		const t = setInterval(() => { if (phase < PHASES.length - 1) phase += 1; }, interval);
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

	const hasAnswer = $derived(Boolean(form?.answer));

	// Community-consensus (#51): alleen http(s)-bronlinks renderen als link.
	const isHttp = (url: string) => /^https?:\/\//.test(url);

	const TYPE_LABELS: Record<string, string> = {
		Ruling: 'Ruling',
		Definitie: 'Uitleg',
		Kaart: 'Kaartvraag',
		Legaliteit: 'Legaliteit',
		Toernooi: 'Toernooiregels'
	};
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
		enctype="multipart/form-data"
		use:enhance={async ({ formData }) => {
			busy = true;
			const q = String(formData.get('question') ?? '').trim();
			if (q) remember(q);
			const f = photoInput?.files?.[0];
			if (f) formData.set('photo', await downscale(f), 'board.jpg');
			else formData.delete('photo');
			return async ({ update }) => {
				busy = false;
				clearPhoto();
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
		<div class="form-row">
			<button type="submit" disabled={busy}>{busy ? 'Bezig…' : 'Vraag'}</button>
			<input
				bind:this={photoInput}
				type="file"
				name="photo"
				accept="image/*"
				capture="environment"
				class="hidden-input"
				onchange={onPhotoChange}
			/>
			<button type="button" class="fb" onclick={() => photoInput?.click()}>
				{photoPreview ? 'Andere foto' : 'Foto van de tafel toevoegen'}
			</button>
			{#if photoPreview}
				<span class="photo-chip">
					<img src={photoPreview} alt="Board state" />
					<button type="button" class="fb" onclick={clearPhoto}>Verwijder</button>
				</span>
			{/if}
		</div>
		{#if photoPreview}
			<p class="meta small">De foto gaat mee als board state — de ruling benoemt eerst wat er zichtbaar is.</p>
		{/if}
	</form>

	{#if busy}
		<div class="panel waiting">
			<span class="spin"></span>
			<div>
				<p class="phase">{PHASES[phase]}</p>
				<p class="meta">{waitText}</p>
			</div>
		</div>
	{/if}

	{#if form?.error}<p class="warn">{form.error}</p>{/if}

	{#if hasAnswer && !busy}
		<article class="panel answer-panel">
			{#if form?.question}
				<p class="asked meta">
					{#if form?.questionType}<span class="qtype">{TYPE_LABELS[form.questionType] ?? form.questionType}</span>{/if}
					Vraag: {form.question}
					<button type="button" class="fb speech" onclick={toggleSpeech}>
						{speaking ? 'Stop voorlezen' : 'Lees voor'}
					</button>
				</p>
			{/if}
			<AnswerView answer={form?.answer ?? ''} citations={form?.citations ?? []} cards={form?.cards ?? []} />
			{#if form?.citations?.length}
				<h2>Geciteerde regelsecties</h2>
				{#each form.citations as c (c.n)}
					{@const essence = citationEssence(c.text)}
					<details class="cite">
						<summary>
							<span class="cite-n">[{c.n}]</span>
							{#if c.section}<strong>§ {c.section}</strong>{/if}
							<span class="meta">{c.sourceName} · trust {c.trust}</span>
							{#if essence}<span class="cite-essence">{essence}</span>{/if}
						</summary>
						{#if c.parents?.length}
							<!-- Bovenliggende regels: zonder § 466.2 is 466.2.c onleesbaar -->
							<div class="parents">
								{#each c.parents as par (par.code)}
									<p class="parent"><a href="/rules/{encodeURIComponent(par.code)}">§ {par.code}</a> {par.text}</p>
								{/each}
							</div>
						{/if}
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

			{#if form?.claims?.length}
				<!-- Community-consensus (#51): interpretatielaag, visueel apart
				     van de officiële citaties — met trust-label en bronnen. -->
				<h2 class="community-h">Community-consensus</h2>
				<p class="meta small community-sub">Geen officiële bron — zo leest de community het. De officiële regels hierboven winnen altijd.</p>
				{#each form.claims as cl (cl.topicRef + cl.statement)}
					<details class="cite claim">
						<summary>
							<span class="community-badge">community</span>
							<strong>{cl.topicRef}</strong>
							<span class="meta">{cl.corroboration} {cl.corroboration === 1 ? 'bron leest' : 'bronnen lezen'} dit zo · trust {cl.trustScore.toFixed(2)}</span>
							{#if cl.officialStatus === 'confirmed'}<span class="confirmed">officieel bevestigd</span>{/if}
							<span class="cite-essence">{cl.statement}</span>
						</summary>
						<p class="cite-text">{cl.statement}</p>
						{#if cl.sources?.length}
							<ul class="claim-sources">
								{#each cl.sources as s (s.url)}
									<li class="meta">
										{#if isHttp(s.url)}
											<a href={s.url} target="_blank" rel="noopener noreferrer">{s.sourceName}</a>
										{:else}
											{s.sourceName}
										{/if}
									</li>
								{/each}
							</ul>
						{/if}
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
							{#if k.legality === 'upcoming'}
								<span class="soon">Nog niet legaal{k.legalFrom ? ` — komt ${new Date(k.legalFrom).toLocaleDateString('nl-NL')}` : ''}</span>
							{/if}
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
						<input type="hidden" name="claims" value={JSON.stringify(form?.claims ?? [])} />
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
					<input type="hidden" name="claims" value={JSON.stringify(form?.claims ?? [])} />
					<input type="hidden" name="verdict" value="down" />
					<textarea name="text" rows="3" placeholder="Wat is het juiste antwoord? Verwijs waar mogelijk naar een §-sectie."></textarea>
					<button type="submit">Verstuur correctie</button>
				</form>
			{/if}
		</article>
	{/if}

	{#if hasAnswer && !busy}
		<!-- Doorvragen (#41): bouwt voort op het gesprek, met alle context -->
		<form
			method="POST"
			action="?/ask"
			use:enhance={({ formData }) => {
				busy = true;
				const q = String(formData.get('question') ?? '').trim();
				if (q) remember(q);
				return async ({ update }) => {
					busy = false;
					followUp = '';
					await update();
				};
			}}
			class="panel followup"
		>
			<h2>Vraag door</h2>
			<p class="meta small">Nog niet duidelijk, of wil je een vervolgsituatie checken? De vraag bouwt voort op dit gesprek.</p>
			<input type="hidden" name="history" value={nextHistory} />
			<textarea name="question" rows="2" placeholder="Bijv.: En wat als de unit al exhausted was?" bind:value={followUp}></textarea>
			<button type="submit" disabled={busy || !followUp.trim()}>Vraag door</button>
		</form>
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
		background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 9px 18px; font-weight: 600; cursor: pointer;
	}
	button[type='submit']:disabled { opacity: 0.55; }
	.form-row { display: flex; align-items: center; gap: 10px; margin-top: 10px; flex-wrap: wrap; }
	.hidden-input { display: none; }
	.photo-chip { display: inline-flex; align-items: center; gap: 8px; }
	.photo-chip img {
		height: 44px; border-radius: 6px; border: 1px solid var(--border);
	}
	.small { font-size: 0.78rem; margin: 8px 0 0; }
	.waiting { display: flex; gap: 14px; align-items: center; padding: 16px; margin-bottom: 16px; }
	.phase { margin: 0 0 2px; font-weight: 600; }
	.waiting .meta { margin: 0; font-size: 0.85rem; }
	.answer-panel { padding: 18px 20px; }
	.asked { margin: 0 0 4px; font-size: 0.85rem; }
	.qtype {
		display: inline-block; font-size: 0.7rem; font-weight: 700;
		text-transform: uppercase; letter-spacing: 0.06em;
		background: var(--accent-soft); color: var(--accent);
		border-radius: 999px; padding: 2px 9px; margin-right: 8px;
	}
	h2 { font-size: 1rem; color: var(--accent); margin: 16px 0 6px; }
	.cite {
		background: var(--surface-deep); border: 1px solid var(--border);
		border-radius: 8px; padding: 8px 12px; margin-bottom: 6px;
	}
	.cite summary { cursor: pointer; }
	.cite-n { color: var(--muted); font-size: 0.85rem; margin-right: 4px; }
	.cite summary .meta { margin-left: 8px; font-size: 0.82rem; }
	/* Eén regel essentie, dichtgeklapt; truncaten zodat 390px nooit
	   horizontaal scrollt. Open toont de volledige tekst al — dan weg. */
	.cite-essence {
		display: block; margin-top: 2px; font-size: 0.9rem;
		overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
	}
	.cite[open] .cite-essence { display: none; }
	/* Community-consensus (#51): visueel onderscheiden van de officiële
	   citaties — accentrand + badge; de summary wrapt zodat 390px nooit
	   horizontaal scrollt. */
	.community-h { margin-bottom: 2px; }
	.community-sub { margin: 0 0 8px; }
	.claim { border-left: 3px solid var(--accent); }
	.claim summary { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px; }
	.claim summary .cite-essence { flex-basis: 100%; }
	.community-badge {
		font-size: 0.7rem; font-weight: 700; text-transform: uppercase;
		letter-spacing: 0.06em; background: var(--accent-soft); color: var(--accent);
		border-radius: 999px; padding: 2px 9px;
	}
	.confirmed {
		font-size: 0.7rem; text-transform: uppercase;
		background: var(--ok-soft); color: var(--ok); border-radius: 999px; padding: 2px 8px;
	}
	.claim-sources { list-style: none; margin: 4px 0 4px; padding: 0; }
	.claim-sources li { margin: 3px 0; overflow-wrap: anywhere; }
	.claim-sources a { color: var(--ok); text-decoration: none; font-weight: 600; }
	.parents { border-left: 2px solid var(--border); margin: 8px 0 0; padding-left: 10px; }
	.parent { margin: 4px 0; color: var(--muted); font-size: 0.85rem; }
	.parent a { color: var(--muted); font-weight: 700; text-decoration: none; }
	.parent a:hover { color: var(--accent); }
	.cite-text { margin: 8px 0 4px; line-height: 1.6; }
	.cite-links a { color: var(--ok); text-decoration: none; font-weight: 600; }
	.card-body { display: flex; flex-wrap: wrap; gap: 14px; margin-top: 8px; }
	.card-body > div { flex: 1 1 220px; min-width: 0; }
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
	.soon {
		font-size: 0.7rem; margin-left: 8px;
		background: var(--warn-soft); color: var(--warn); border-radius: 999px; padding: 2px 8px;
	}
	.feedback {
		display: flex; align-items: center; gap: 10px; flex-wrap: wrap;
		border-top: 1px solid var(--border); margin-top: 16px; padding-top: 12px;
	}
	.fb {
		background: transparent; color: var(--muted); border: 1px solid var(--border);
		border-radius: 8px; padding: 4px 12px; font-size: 0.85rem; cursor: pointer;
		/* Op smal wrapt de rij als geheel; de knoptekst zelf breekt niet. */
		white-space: nowrap;
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
	.followup { padding: 14px 16px; margin-top: 14px; }
	.followup h2 { margin: 0 0 2px; }
	.followup textarea {
		width: 100%; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px;
		resize: vertical; margin-top: 8px;
	}
	.followup button {
		margin-top: 8px; background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 8px 16px; font-weight: 600; cursor: pointer;
	}
	.followup button:disabled { opacity: 0.5; }
	.speech { margin-left: 8px; }
	.hist-title { margin-top: 26px; }
	.history { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
	.warn { color: var(--err); }
</style>
