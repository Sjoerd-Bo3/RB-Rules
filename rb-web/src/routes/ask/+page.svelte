<script lang="ts">
	import { enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import { page } from '$app/state';
	import { untrack } from 'svelte';
	import RbText from '$lib/RbText.svelte';
	import AnswerView from '$lib/AnswerView.svelte';
	import AskHistoryPanel from '$lib/AskHistoryPanel.svelte';
	import { citationEssence, splitSettled } from '$lib/answerFormat';
	import { APPROACH_OPTIONS, approachNotice, type Approach } from '$lib/approach';
	import { askSession } from '$lib/askSession.svelte';
	import type { StoredAnswer } from '$lib/askPersist';
	import type { AskTurn } from '$lib/types';

	let { data, form } = $props();

	// Vraag, antwoord en de lopende stream leven in de sessie-store (#248),
	// niet in deze component: bij client-side navigatie unmount de component
	// en brak vroeger de fetch/ReadableStream af — antwoord kwijt. Deze pagina
	// leest en rendert alleen; het haakje hieronder ververst de duurstatistiek
	// zodra er een antwoord landt (alleen zolang je hier staat).
	$effect(() => {
		untrack(() => askSession.restore());
		askSession.onAnswered = () => void invalidateAll();
		return () => {
			askSession.onAnswered = null;
		};
	});

	// Aanpak-keuze (#153, alleen ingelogd): Auto laat de bestaande gate
	// beslissen, Snel forceert de single-pass, Grondig de brein-agent —
	// binnen het eigen dagtegoed. De server blijft de meester: anoniem of
	// zonder tegoed wordt de keuze daar genegeerd (met nette melding).
	const account = $derived(data.account);
	const agenticLeft = $derived(
		account ? Math.max(0, account.dailyAgenticQuota - account.agenticToday) : 0
	);
	let approach = $state<Approach>('auto');
	// Tegoed op (ook halverwege een sessie, na invalidateAll): terug naar
	// Auto — een disabled radio zou anders stil uit het formulier vallen.
	$effect(() => {
		if (approach === 'thorough' && account && agenticLeft === 0) approach = 'auto';
	});
	const approachHint = $derived.by(() => {
		if (!account) return '';
		if (approach === 'thorough')
			return `Grondig: de brein-agent redeneert door — duurt ±2 min en telt zwaarder mee. Vandaag nog ${agenticLeft} van ${account.dailyAgenticQuota} over.`;
		const base = APPROACH_OPTIONS.find((o) => o.value === approach)?.hint ?? '';
		return agenticLeft === 0
			? `${base}. Het dagtegoed voor Grondig is vandaag op.`
			: `${base}.`;
	});

	// Echte duurstatistiek (mediaan/p90 van de laatste vragen) i.p.v. schatting.
	const stats = $derived(data.stats);
	// Fase-verdeling (#152): retrieval (zoeken, overlapt de herformulering) vs.
	// de AI-generatie — zodat de wachtende gebruiker ziet waar de tijd zit.
	const phaseText = $derived(
		stats.phases
			? ` Daarvan is ±${Math.round(stats.phases.retrievalMs / 1000)}s zoeken en ±${Math.round(stats.phases.aiMs / 1000)}s antwoord formuleren.`
			: ''
	);
	const waitText = $derived(
		stats.count > 0 && stats.medianMs
			? `Meestal ±${Math.round(stats.medianMs / 1000)}s, uitschieters tot ~${Math.round((stats.p90Ms ?? stats.medianMs) / 1000)}s — gemeten over de laatste ${stats.count} ${stats.count === 1 ? 'vraag' : 'vragen'}.${phaseText}`
			: 'Eerste metingen lopen nog — dit kan even duren.'
	);
	// Prefill vanuit de globale zoekbalk / dashboard-hero (#214): /ask?q=…
	// vult de vraag in (niet auto-versturen — de bezoeker houdt de controle).
	$effect(() => {
		const q = page.url.searchParams.get('q')?.trim();
		if (q) askSession.draft = q;
	});
	let correcting = $state(false);
	let followUp = $state('');
	// Ruling vastleggen vanuit dit gesprek (#166) — alleen ingelogd/admin.
	let rulingOpen = $state(false);

	// Zonder JS post het formulier gewoon door en komt het antwoord als
	// ActionData terug — dan is `form` de bron. Met JS vult de sessie-store
	// zichzelf en wint die: alleen dát antwoord overleeft navigatie (#248).
	const formAnswer = $derived.by<StoredAnswer | null>(() =>
		form?.answer
			? {
					question: form.question ?? '',
					history: form.history ?? [],
					answer: form.answer,
					citations: form.citations ?? [],
					cards: form.cards ?? [],
					claims: form.claims ?? null,
					misconceptions: form.misconceptions ?? null,
					questionType: form.questionType ?? null,
					approachReason: form.approachReason ?? null,
					interrupted: null
				}
			: null
	);
	const current = $derived(askSession.answer ?? formAnswer);
	const hasAnswer = $derived(Boolean(current?.answer));
	const live = $derived(askSession.live);
	const busy = $derived(askSession.busy);
	// Fouten uit de vraagbaak zelf komen uit de store; `form.error` blijft over
	// voor feedback/ruling (en voor de vraag zonder JS).
	const errorText = $derived(askSession.error ?? form?.error ?? null);
	// Terugmelding van de server (#153): de keuze werd niet gehonoreerd —
	// bij quota-op de eerlijke "automatisch beantwoord"-melding.
	const answerNotice = $derived(approachNotice(current?.approachReason));

	// Historie voor de volgende doorvraag: eerdere rondes + de huidige.
	const nextTurns = $derived.by<AskTurn[]>(() => {
		if (!current?.answer) return [];
		return [...current.history, { question: current.question, answer: current.answer }].slice(-3);
	});
	const nextHistory = $derived(JSON.stringify(nextTurns));

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

	// De foto-keuze hoort bij dít formulier, niet bij de sessie: opruimen zodra
	// de vraag klaar is (of mislukte). Navigeer je ertussenuit, dan verdwijnt
	// de preview met de component mee — de foto zit dan al in de request.
	let wasBusy = false;
	$effect(() => {
		const now = askSession.busy;
		if (wasBusy && !now) clearPhoto();
		wasBusy = now;
	});

	const EXAMPLES = [
		'Wanneer mag ik een unit moven, en wat telt niet als move?',
		'Wat doet Deflect tegen een spell die meerdere targets kiest?',
		'Mag ik reageren tijdens een showdown, en in welke volgorde?',
		'Wat gebeurt er met een Hidden unit die getarget wordt?'
	];

	// Fase-tekst tijdens het wachten; tempo geschaald op de echte mediaan. De
	// fase volgt uit de starttijd in de store, niet uit een component-timer:
	// kom je halverwege terug op de pagina, dan klopt de fase nog steeds.
	const PHASES = [
		'Vraag insturen',
		'Relevante regelsecties zoeken (semantisch + full-text)',
		'Antwoord formuleren met §-citaten',
		'Bijna klaar — antwoord controleren'
	];
	let tick = $state(Date.now());
	$effect(() => {
		if (!busy) return;
		tick = Date.now();
		const t = setInterval(() => (tick = Date.now()), 1000);
		return () => clearInterval(t);
	});
	const phase = $derived.by(() => {
		if (!busy || !askSession.startedAt) return 0;
		const step = Math.max(2000, Math.round((stats.medianMs ?? 24_000) / PHASES.length));
		return Math.min(PHASES.length - 1, Math.floor((tick - askSession.startedAt) / step));
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

	// Streaming (#31): het antwoord komt via /ask/stream (NDJSON-proxy naar
	// rb-api) woord voor woord binnen. Markdown/widget-parsing is pas zinvol op
	// afgeronde regels: alleen het deel t/m de laatste newline gaat door
	// AnswerView, de staart als kale tekst.
	const liveParts = $derived(live ? splitSettled(live.answer) : { settled: '', tail: '' });
	const liveNotice = $derived(approachNotice(live?.approachReason));

	// Community-consensus (#51): alleen http(s)-bronlinks renderen als link.
	const isHttp = (url: string) => /^https?:\/\//.test(url);

	// Temporele precedentie (#168): "laatst bijgewerkt" (een echte
	// content-wijziging) weegt zwaarder dan "geldig sinds" (publicatiedatum) —
	// beide null ⇒ geen label (de bron droeg geen van beide datums).
	function citationDateLabel(c: { publishedAt: string | null; updatedAt: string | null }): string | null {
		if (c.updatedAt) return `laatst bijgewerkt ${new Date(c.updatedAt).toLocaleDateString('nl-NL')}`;
		if (c.publishedAt) return `geldig sinds ${new Date(c.publishedAt).toLocaleDateString('nl-NL')}`;
		return null;
	}

	const TYPE_LABELS: Record<string, string> = {
		Ruling: 'Ruling',
		Definitie: 'Uitleg',
		Kaart: 'Kaartvraag',
		Legaliteit: 'Legaliteit',
		Toernooi: 'Toernooiregels'
	};
</script>

<svelte:head><title>Vraag een ruling — Poracle</title></svelte:head>

<main>
	<h1>Vraag een <span>ruling</span></h1>
	<p class="subtitle">Antwoord met exacte §-citaten uit de officiële regels.</p>

	{#if !data.loggedIn}
		<!-- Login-poort (#328): de vraagbaak is alleen voor accounts. Dit blok
		     is presentatie — de echte poort zit server-side (de ask-action, de
		     /ask/stream-proxy en rb-api zelf weigeren anoniem alle drie). -->
		<section class="panel login-gate">
			<p class="gate-status"><span class="dot" aria-hidden="true"></span>Alleen voor ingelogde spelers</p>
			<h2>Log in om vragen te stellen</h2>
			<p>
				Elke vraag draait een echt AI-model en kost dus rekentijd. Om dat
				eerlijk te verdelen is de vraagbaak gekoppeld aan een gratis account
				met een dagtegoed — inloggen kan met een magic-link per e-mail of met
				een passkey, zonder wachtwoord.
			</p>
			<p>
				De rest van Poracle blijft gewoon open zonder account: de regels met
				§-permalinks, de kaartbrowser, de rulings-databank, de wijzigingen-feed
				en het zoeken.
			</p>
			<a class="gate-login" href="/account">Log in of maak een account</a>
			<p class="meta small">
				Na het inloggen kom je hier terug en stel je direct je eerste vraag.
			</p>
		</section>
	{:else}
	<div class="examples">
		{#each EXAMPLES as ex (ex)}
			<button type="button" class="chip" onclick={() => (askSession.draft = ex)}>{ex}</button>
		{/each}
	</div>

	<!-- Eigen ask-geschiedenis (#157): dicht standaard, laadt al mee met de
	     pagina (+page.server.ts) — geen extra client-fetch nodig. -->
	<AskHistoryPanel items={data.askHistory} loggedIn={data.loggedIn} />

	<form
		method="POST"
		action="?/ask"
		enctype="multipart/form-data"
		use:enhance={async ({ formData, cancel }) => {
			// Met JS voert de sessie-store de vraag uit (#248) — die overleeft
			// navigatie, de form-submit niet. Zonder JS draait dit blok niet en
			// doet de action zelf het werk.
			cancel();
			// Guard meteen dicht (review-fix): tijdens 'await downscale' mag een
			// tweede klik geen tweede (betaalde) request kunnen starten.
			if (askSession.busy) return;
			const q = String(formData.get('question') ?? '').trim();
			if (!q) {
				askSession.error = 'Stel eerst een vraag.';
				return;
			}
			remember(q);
			const f = photoInput?.files?.[0];
			const photo = f ? await downscale(f) : null;
			askSession.ask({
				question: q,
				turns: [],
				photo,
				approach: account ? approach : undefined,
				clearQuestion: true
			});
		}}
		class="panel form"
	>
		<textarea
			name="question"
			rows="3"
			placeholder="Beschrijf de situatie of stel je regelvraag…"
			bind:value={askSession.draft}
		></textarea>
		{#if account}
			<!-- Aanpak-keuze (#153, alleen ingelogd): compacte segment-keuze;
			     de radio's reizen als approach-veld mee met de form-POST én
			     (via state) met het streamingpad. Server-authoritatief. -->
			<div class="approach-row">
				<span class="meta approach-label" id="approach-label">Aanpak</span>
				<div class="approach-opts" role="radiogroup" aria-labelledby="approach-label">
					{#each APPROACH_OPTIONS as opt (opt.value)}
						{@const off = opt.value === 'thorough' && agenticLeft === 0}
						<label class="approach-opt" class:active={approach === opt.value} class:off>
							<input
								type="radio"
								name="approach"
								value={opt.value}
								bind:group={approach}
								disabled={off}
							/>
							{opt.label}
						</label>
					{/each}
				</div>
			</div>
			<p class="meta small approach-hint">{approachHint}</p>
		{/if}
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
	{/if}

	{#if busy && !live?.answer}
		<div class="panel waiting">
			<span class="spin"></span>
			<div>
				<p class="phase">{PHASES[phase]}</p>
				<p class="meta">{waitText}</p>
				{#if liveNotice}<p class="approach-notice">{liveNotice}</p>{/if}
				<!-- De vraag loopt door als je wegnavigeert (#248) — dus moet je
				     hem ook expliciet kunnen stoppen. -->
				<button type="button" class="fb stop" onclick={() => askSession.stop()}>Stoppen</button>
			</div>
		</div>
	{/if}

	{#if errorText && !live}<p class="warn">{errorText}</p>{/if}

	<!-- Screenreader-status (review-fix): alleen de afronding wordt
	     aangekondigd; het groeiende antwoord zelf is géén live-region, anders
	     wordt bij elke delta het hele antwoord opnieuw voorgelezen. -->
	<p class="visually-hidden" role="status">{askSession.announce}</p>

	{#if askSession.retry && !busy}
		<div class="panel waiting">
			<div>
				<p class="phase">De verbinding brak voordat het antwoord binnenkwam.</p>
				<p class="meta">
					Mogelijk was er al een antwoord onderweg; daarom proberen we niet automatisch
					opnieuw.
				</p>
				<button type="button" class="retry" onclick={() => askSession.retryAsk()}>Opnieuw proberen</button>
			</div>
		</div>
	{/if}

	{#if live?.answer}
		<!-- Streaming (#31): het antwoord groeit woord voor woord. Afgeronde
		     regels gaan door de gewone AnswerView (widgets werken al via de
		     meta-citaties); de staart is kale tekst met een cursor. -->
		<article class="panel answer-panel" aria-busy="true">
			<p class="asked meta">
				{#if live.questionType}<span class="qtype">{TYPE_LABELS[live.questionType] ?? live.questionType}</span>{/if}
				Vraag: {live.question}
				<button type="button" class="fb speech" onclick={() => askSession.stop()}>Stoppen</button>
			</p>
			{#if liveNotice}<p class="approach-notice">{liveNotice}</p>{/if}
			<AnswerView answer={liveParts.settled} citations={live.citations} cards={[]} />
			<p class="md-tail">{liveParts.tail}<span class="cursor"></span></p>
		</article>
	{/if}

	{#if current && !busy && !live}
		<article class="panel answer-panel">
			{#if current.question}
				<p class="asked meta">
					{#if current.questionType}<span class="qtype">{TYPE_LABELS[current.questionType] ?? current.questionType}</span>{/if}
					Vraag: {current.question}
					<button type="button" class="fb speech" onclick={() => askSession.toggleSpeech()}>
						{askSession.speaking ? 'Stop voorlezen' : 'Lees voor'}
					</button>
					<!-- Het antwoord blijft staan tot je een nieuwe vraag stelt (#248),
					     dus hoort er een manier te zijn om het weg te halen. -->
					<button type="button" class="fb speech" onclick={() => askSession.clear()}>Wissen</button>
				</p>
			{/if}
			{#if answerNotice}<p class="approach-notice">{answerNotice}</p>{/if}
			{#if current.interrupted}
				<!-- Onvolledig antwoord (#248): verbinding weg, zelf gestopt, of door
				     een reload afgebroken — eerlijk gelabeld, niet stil als compleet. -->
				<p class="warn">{current.interrupted}</p>
			{/if}
			<AnswerView answer={current.answer} citations={current.citations} cards={current.cards} />
			{#if current.citations.length}
				<h2>Geciteerde regelsecties</h2>
				{#each current.citations as c (c.n)}
					{@const essence = citationEssence(c.text)}
					{@const dateLabel = citationDateLabel(c)}
					<details class="cite">
						<summary>
							<span class="cite-n">[{c.n}]</span>
							{#if c.section}<strong>§ {c.section}</strong>{/if}
							<span class="meta">{c.sourceName} · trust {c.trust}{dateLabel ? ` · ${dateLabel}` : ''}</span>
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

			{#if current.claims?.length}
				<!-- Community-consensus (#51): interpretatielaag, visueel apart
				     van de officiële citaties — met trust-label en bronnen. -->
				<h2 class="community-h">Community-consensus</h2>
				<p class="meta small community-sub">Geen officiële bron — zo leest de community het. De officiële regels hierboven winnen altijd.</p>
				{#each current.claims as cl (cl.topicRef + cl.statement)}
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

			{#if current.misconceptions?.length}
				<!-- Misvattingen (#125): verworpen community-lezingen mét officiële
				     weerlegging — naast de community-consensus, herkenbaar als
				     negatieve kennis: zo zit het dus níet. -->
				<h2 class="misconception-h">Veelgemaakte misvatting</h2>
				<p class="meta small misconception-sub">Community-lezing die door de officiële regels is weerlegd — zo zit het dus niet.</p>
				{#each current.misconceptions as m (m.topicRef + m.statement)}
					<details class="cite misconception">
						<summary>
							<span class="misconception-badge">misvatting</span>
							<strong>{m.topicRef}</strong>
							{#if m.rebuttalSection}<span class="meta">weerlegd door § {m.rebuttalSection}</span>{/if}
							<span class="cite-essence">{m.statement}</span>
						</summary>
						<p class="cite-text misread">{m.statement}</p>
						{#if m.sources?.length}
							<ul class="claim-sources">
								{#each m.sources as s (s.url)}
									<li class="meta">
										{#if s.quote}<span class="misconception-quote">"{s.quote}"</span> — {/if}
										{#if isHttp(s.url)}
											<a href={s.url} target="_blank" rel="noopener noreferrer">{s.sourceName}</a>
										{:else}
											{s.sourceName}
										{/if}
									</li>
								{/each}
							</ul>
						{/if}
						<p class="rebuttal">
							<span class="rebuttal-label">Officiële weerlegging</span>
							{m.rebuttal}
							{#if m.rebuttalSection}
								<a href="/rules/{encodeURIComponent(m.rebuttalSection)}">§ {m.rebuttalSection}</a>
							{/if}
						</p>
					</details>
				{/each}
			{/if}

			{#if current.cards.length}
				<h2>Betrokken kaarten</h2>
				{#each current.cards as k (k.riftboundId)}
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

			{#if !current.interrupted}
				<!-- Self-learning: feedback wordt beoordeeld en stuurt daarna antwoorden.
				     Bij een onderbroken antwoord (#248) blijven feedback en "vastleggen
				     als ruling" weg: een half antwoord beoordelen zegt niets. -->
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
							<input type="hidden" name="question" value={current.question ?? ''} />
							<input type="hidden" name="answer" value={current.answer} />
							<input type="hidden" name="citations" value={JSON.stringify(current.citations)} />
							<input type="hidden" name="cards" value={JSON.stringify(current.cards)} />
							<input type="hidden" name="claims" value={JSON.stringify(current.claims ?? [])} />
							<input type="hidden" name="misconceptions" value={JSON.stringify(current.misconceptions ?? [])} />
							<input type="hidden" name="verdict" value="up" />
							<button class="fb">Ja</button>
						</form>
						<button class="fb" type="button" onclick={() => (correcting = !correcting)}>Nee, corrigeer</button>
					{/if}
				</div>
				{#if correcting && !form?.feedbackSent}
					<form method="POST" action="?/feedback" use:enhance={() => async ({ update }) => { correcting = false; await update(); }} class="correct-form">
						<input type="hidden" name="question" value={current.question ?? ''} />
						<input type="hidden" name="answer" value={current.answer} />
						<input type="hidden" name="citations" value={JSON.stringify(current.citations)} />
						<input type="hidden" name="cards" value={JSON.stringify(current.cards)} />
						<input type="hidden" name="claims" value={JSON.stringify(current.claims ?? [])} />
							<input type="hidden" name="misconceptions" value={JSON.stringify(current.misconceptions ?? [])} />
						<input type="hidden" name="verdict" value="down" />
						<textarea name="text" rows="3" placeholder="Wat is het juiste antwoord? Verwijs waar mogelijk naar een §-sectie."></textarea>
						<button type="submit">Verstuur correctie</button>
					</form>
				{/if}

				{#if data.isAdmin || data.loggedIn}
					<!-- Ruling vastleggen vanuit dit gesprek (#166): hergebruikt de
					     Correction-infrastructuur. Beheerder ⇒ direct geverifieerd;
					     ingelogde gebruiker ⇒ voorstel in de reviewqueue. Anoniem ziet
					     deze actie niet — rb-api wijst het bovendien af. -->
					<div class="ruling-cta">
						{#if form?.rulingSaved}
							<p class="meta">
								{form.rulingVerified
									? 'Vastgelegd als geverifieerde ruling.'
									: 'Ter beoordeling ingediend — telt mee na goedkeuring door de beheerder.'}
							</p>
						{:else}
							<button type="button" class="fb" onclick={() => (rulingOpen = !rulingOpen)}>
								Vastleggen als ruling
							</button>
						{/if}
					</div>
					{#if rulingOpen && !form?.rulingSaved}
						<form
							method="POST"
							action="?/ruling"
							use:enhance={() => async ({ update }) => { rulingOpen = false; await update(); }}
							class="correct-form ruling-form"
						>
							<input type="hidden" name="question" value={current.question ?? ''} />
							<input type="hidden" name="answer" value={current.answer} />
							<input type="hidden" name="citations" value={JSON.stringify(current.citations)} />
							<input type="hidden" name="cards" value={JSON.stringify(current.cards)} />
							<input type="hidden" name="claims" value={JSON.stringify(current.claims ?? [])} />
							<input
								type="hidden"
								name="misconceptions"
								value={JSON.stringify(current.misconceptions ?? [])}
							/>
							<label for="ruling-statement">Uitspraak</label>
							<textarea id="ruling-statement" name="statement" rows="3">{current.answer}</textarea>
							<label for="ruling-scope">Onderwerp</label>
							<select id="ruling-scope" name="scope">
								<option value="answer">Algemeen</option>
								<option value="card">Kaart</option>
								<option value="rule_section">Regelsectie</option>
							</select>
							<input type="text" name="topicRef" placeholder="Kaartnaam of §-code (bij kaart/regelsectie)" />
							<input type="text" name="sourceRef" required placeholder="Waar besloten? (URL of citaat, verplicht)" />
							<button type="submit">Vastleggen</button>
						</form>
					{/if}
					{#if form?.rulingError}<p class="warn">{form.rulingError}</p>{/if}
				{/if}
			{/if}
		</article>
	{/if}

	{#if current && !current.interrupted && !busy && !live && data.loggedIn}
		<!-- Doorvragen (#41): bouwt voort op het gesprek, met alle context. Op een
		     onderbroken (onvolledig) antwoord bouw je niet verder — dat zou een
		     half antwoord als context meesturen. -->
		<form
			method="POST"
			action="?/ask"
			use:enhance={({ formData, cancel }) => {
				// Doorvragen loopt via dezelfde sessie-store (#248); de historie
				// komt uit het huidige antwoord in plaats van uit het hidden veld
				// (dat blijft staan voor de JS-loze route).
				cancel();
				if (askSession.busy) return;
				const q = String(formData.get('question') ?? '').trim();
				if (!q) return;
				remember(q);
				followUp = '';
				askSession.ask({
					question: q,
					turns: nextTurns,
					photo: null,
					approach: account ? approach : undefined,
					clearQuestion: false
				});
			}}
			class="panel followup"
		>
			<h2>Vraag door</h2>
			<p class="meta small">Nog niet duidelijk, of wil je een vervolgsituatie checken? De vraag bouwt voort op dit gesprek.</p>
			<input type="hidden" name="history" value={nextHistory} />
			{#if account}
				<!-- #153: de gekozen aanpak reist ook met doorvragen mee. -->
				<input type="hidden" name="approach" value={approach} />
			{/if}
			<textarea name="question" rows="2" placeholder="Bijv.: En wat als de unit al exhausted was?" bind:value={followUp}></textarea>
			<button type="submit" disabled={busy || !followUp.trim()}>Vraag door</button>
		</form>
	{/if}

	{#if history.length && !busy}
		<h2 class="hist-title">Eerdere vragen</h2>
		<ul class="history">
			{#each history as h (h.at)}
				<li><button type="button" class="chip" onclick={() => (askSession.draft = h.q)}>{h.q}</button></li>
			{/each}
		</ul>
	{/if}
</main>

<style>
	main { max-width: 760px; margin: 0 auto; padding: 24px 20px; }
	h1 span { color: var(--accent); }
	.subtitle, .meta { color: var(--muted); }
	.examples { display: flex; flex-wrap: wrap; gap: 8px; margin: 12px 0 14px; }
	.chip {
		background: var(--surface); color: var(--muted); border: 1px solid var(--border);
		border-radius: 999px; padding: 6px 14px; font-size: 0.85rem; cursor: pointer;
		text-align: left;
	}
	.chip:hover { color: var(--text); border-color: var(--border-strong); }
	/* Login-poort (#328): status = kleur + tekst, geen emoji's. */
	.login-gate { padding: 20px; margin: 14px 0 16px; }
	.login-gate h2 { margin: 8px 0 6px; }
	.login-gate p { margin: 8px 0; }
	.gate-status {
		display: inline-flex; align-items: center; gap: 8px; margin: 0;
		font-size: 0.85rem; font-weight: 600; color: var(--accent);
	}
	.gate-status .dot {
		width: 8px; height: 8px; border-radius: 50%; background: var(--accent);
	}
	.gate-login {
		display: inline-block; margin-top: 6px; background: var(--accent);
		color: var(--accent-ink); border-radius: 8px; padding: 9px 18px;
		font-weight: 600; text-decoration: none;
	}
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
	/* Aanpak-keuze (#153): compact segment; wrapt op 390px zonder overflow. */
	.approach-row { display: flex; align-items: center; gap: 10px; margin-top: 10px; flex-wrap: wrap; }
	.approach-label { font-size: 0.85rem; }
	.approach-opts {
		display: inline-flex; border: 1px solid var(--border); border-radius: 8px;
		overflow: hidden; background: var(--surface-deep);
	}
	.approach-opt {
		position: relative; padding: 6px 14px; font-size: 0.85rem;
		color: var(--muted); cursor: pointer; white-space: nowrap;
	}
	.approach-opt + .approach-opt { border-left: 1px solid var(--border); }
	.approach-opt:hover { color: var(--text); }
	.approach-opt.active { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
	.approach-opt.off { opacity: 0.5; cursor: not-allowed; }
	/* Radio zelf onzichtbaar maar focusbaar; focus zichtbaar op het segment. */
	.approach-opt input {
		position: absolute; width: 1px; height: 1px; opacity: 0; margin: 0;
	}
	.approach-opt:focus-within { outline: 2px solid var(--focus); outline-offset: -2px; }
	.approach-hint { margin: 6px 0 0; }
	/* Terugmelding (#153): keuze niet gehonoreerd — status als kleur + tekst. */
	.approach-notice {
		background: var(--warn-soft); color: var(--warn);
		border-radius: 8px; padding: 8px 12px; margin: 8px 0;
		font-size: 0.85rem;
	}
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
	/* Alleen voor screenreaders: visueel volledig verborgen statusregel. */
	.visually-hidden {
		position: absolute; width: 1px; height: 1px; margin: -1px; padding: 0;
		overflow: hidden; clip-path: inset(50%); white-space: nowrap; border: 0;
	}
	/* Stoppen tijdens het wachten (#248): de vraag loopt door bij navigatie,
	   dus is afbreken een expliciete actie. */
	.stop { margin-top: 8px; }
	.retry {
		margin-top: 8px; background: var(--accent); color: var(--accent-ink);
		border: 0; border-radius: 8px; padding: 7px 14px; font-weight: 600;
		cursor: pointer;
	}
	/* Streaming (#31): staart van de binnenstromende regel + cursor. */
	.md-tail { margin: 0; line-height: 1.6; white-space: pre-wrap; overflow-wrap: anywhere; }
	.cursor {
		display: inline-block; width: 8px; height: 1em; margin-left: 2px;
		background: var(--accent); vertical-align: text-bottom;
		animation: blink 1s steps(2) infinite;
	}
	@keyframes blink { 50% { opacity: 0; } }
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
	/* Misvattingen (#125): herkenbaar als negatieve kennis — err-rand + badge,
	   de officiële weerlegging als groen gemarkeerde slotregel. Zelfde
	   wrap-regels als .claim zodat 390px nooit horizontaal scrollt. */
	.misconception-h { margin-bottom: 2px; }
	.misconception-sub { margin: 0 0 8px; }
	.misconception { border-left: 3px solid var(--err); }
	.misconception summary { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px; }
	.misconception summary .cite-essence { flex-basis: 100%; }
	.misconception-badge {
		font-size: 0.7rem; font-weight: 700; text-transform: uppercase;
		letter-spacing: 0.06em; background: var(--err-soft); color: var(--err);
		border-radius: 999px; padding: 2px 9px;
	}
	.misconception .misread { color: var(--muted); }
	.misconception-quote { color: var(--text); font-style: italic; }
	.rebuttal {
		border-top: 1px solid var(--border); margin: 8px 0 4px; padding-top: 8px;
		line-height: 1.6; overflow-wrap: anywhere;
	}
	.rebuttal-label {
		display: block; font-size: 0.7rem; font-weight: 700;
		text-transform: uppercase; letter-spacing: 0.06em; color: var(--ok);
		margin-bottom: 2px;
	}
	.rebuttal a { color: var(--ok); text-decoration: none; font-weight: 600; }
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
	.ruling-cta { margin-top: 10px; }
	.ruling-form label { display: block; font-size: 0.8rem; color: var(--muted); margin: 8px 0 4px; }
	.ruling-form label:first-of-type { margin-top: 0; }
	.ruling-form select, .ruling-form input[type='text'] {
		width: 100%; box-sizing: border-box; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 8px 12px;
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
