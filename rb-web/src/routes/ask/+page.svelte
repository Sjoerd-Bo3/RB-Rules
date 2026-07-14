<script lang="ts">
	import { applyAction, deserialize, enhance } from '$app/forms';
	import { invalidateAll } from '$app/navigation';
	import RbText from '$lib/RbText.svelte';
	import AnswerView from '$lib/AnswerView.svelte';
	import AskHistoryPanel from '$lib/AskHistoryPanel.svelte';
	import { citationEssence, splitSettled } from '$lib/answerFormat';
	import { quotaMessage } from '$lib/quota';
	import { APPROACH_OPTIONS, approachNotice, type Approach } from '$lib/approach';

	let { data, form } = $props();
	let busy = $state(false);
	let phase = $state(0);

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
	// Terugmelding van de server (#153): de keuze werd niet gehonoreerd —
	// bij quota-op de eerlijke "automatisch beantwoord"-melding.
	const answerNotice = $derived(approachNotice(form?.approachReason));

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
	let question = $state('');
	let correcting = $state(false);
	let followUp = $state('');
	// Ruling vastleggen vanuit dit gesprek (#166) — alleen ingelogd/admin.
	let rulingOpen = $state(false);

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

	// ── Streaming (#31) ────────────────────────────────────────────────
	// Het antwoord komt via /ask/stream (NDJSON-proxy naar rb-api) woord
	// voor woord binnen. `live` is de groeiende tussenstand; het slotframe
	// wordt via applyAction het gewone `form`-resultaat, zodat voorlezen,
	// feedback en doorvragen ongewijzigd op het eindantwoord werken.
	interface Turn {
		question: string;
		answer: string;
	}
	interface LiveAsk {
		question: string;
		history: Turn[];
		questionType: string | null;
		citations: unknown[];
		answer: string;
		/** #153: terugval-reden uit het meta-frame — de melding hoort niet
		 *  te wachten op het slotframe (agentic heeft een lange stille fase). */
		approachReason: string | null;
	}
	let live = $state<LiveAsk | null>(null);
	let liveError = $state<string | null>(null);
	// Brak de verbinding ná response-start maar vóór het eerste frame, dan
	// kán rb-api al aan de (betaalde) LLM-call begonnen zijn: geen stille
	// automatische herkansing, maar een expliciete "Opnieuw proberen"-knop.
	let retryPending = $state<{ fd: FormData; clearQuestion: boolean } | null>(null);
	// Afronding voor screenreaders: het groeiende antwoord zelf is bewust
	// géén live-region (elke delta zou opnieuw voorgelezen worden).
	let announce = $state('');
	// Markdown/widget-parsing is pas zinvol op afgeronde regels: alleen het
	// deel t/m de laatste newline gaat door AnswerView, de staart als kale
	// tekst. Het slotframe her-rendert daarna het volledige antwoord.
	const liveParts = $derived(live ? splitSettled(live.answer) : { settled: '', tail: '' });
	const liveNotice = $derived(approachNotice(live?.approachReason));

	const canStream = () =>
		typeof ReadableStream === 'function' && typeof TextDecoder === 'function';

	async function blobToBase64(blob: Blob): Promise<string> {
		const bytes = new Uint8Array(await blob.arrayBuffer());
		let bin = '';
		for (let i = 0; i < bytes.length; i += 0x8000)
			bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
		return btoa(bin);
	}

	/** Vangnet: de bestaande niet-streamende form action, handmatig
	 *  aangeroepen. Automatisch alléén als de stream-fetch faalde vóór er
	 *  response-headers waren of met een nette foutstatus antwoordde — rb-api
	 *  is dan (vrijwel zeker) nooit aan een antwoord begonnen. Brak een
	 *  gestárte response af, dan loopt dit uitsluitend via de expliciete
	 *  "Opnieuw proberen"-knop (retryPending) om stille dubbele LLM-kosten
	 *  te vermijden. */
	async function fallbackAsk(formData: FormData, clearQuestion: boolean) {
		try {
			const res = await fetch('?/ask', {
				method: 'POST',
				headers: { 'x-sveltekit-action': 'true' },
				body: formData
			});
			const result = deserialize(await res.text());
			if (result.type === 'success') {
				// Zelfde afronding als de oude update()-flow: formulier leeg en
				// duurstatistiek ("Meestal ±Xs") vers.
				if (clearQuestion) question = '';
				await invalidateAll();
			}
			await applyAction(result);
			announce = result.type === 'success' ? 'Antwoord compleet.' : 'Antwoord mislukt.';
		} catch (e) {
			await applyAction({
				type: 'failure',
				status: 500,
				data: { error: `Vraag mislukt (${e instanceof Error ? e.message : e})` }
			});
		}
	}

	/** De expliciete herkansing na een afgebroken maar wél gestarte stream —
	 *  via de niet-streamende route, zodat het antwoord in één keer landt. */
	async function retryAsk() {
		const pending = retryPending;
		if (!pending || busy) return;
		retryPending = null;
		busy = true;
		try {
			await fallbackAsk(pending.fd, pending.clearQuestion);
		} finally {
			busy = false;
		}
	}

	async function streamAsk(
		q: string,
		turns: Turn[],
		photo: Blob | null,
		formData: FormData,
		clearQuestion: boolean
	) {
		busy = true;
		liveError = null;
		live = null;
		retryPending = null;
		announce = '';
		// Expliciet error-frame van rb-api (fout ná de 200) — apart van een
		// verbindingsbreuk, zodat de catch een echte fout als fout toont en
		// alleen een breuk de retry-knop geeft (#103/#107). Of er "al iets"
		// binnenkwam meet de afhandeling niet meer op frames (het meta-frame
		// komt vóór de lange agentic-wachtfase, #107) maar op antwoordtekst.
		let serverError: string | null = null;
		try {
			const images = photo
				? // mediaType uit het bestand zelf: downscale() kan het originele
					// File (PNG/WebP) teruggeven — een vast 'image/jpeg'-label laat
					// de Anthropic-API de mismatch weigeren (review-fix).
					[{ mediaType: photo.type || 'image/jpeg', data: await blobToBase64(photo) }]
				: undefined;
			let res: Response;
			try {
				res = await fetch('/ask/stream', {
					method: 'POST',
					headers: { 'content-type': 'application/json' },
					body: JSON.stringify({
						question: q,
						history: turns.length ? turns : undefined,
						images,
						// #153: keuze reist mee; de server honoreert alleen ingelogd.
						approach: account ? approach : undefined
					})
				});
			} catch {
				// Geen response-headers gezien: veilige automatische terugval.
				await fallbackAsk(formData, clearQuestion);
				return;
			}
			const gate = quotaMessage(res.status);
			if (gate) {
				// Rate-limit/quota/sessiepoort (#42): terugvallen raakt exact
				// dezelfde poort — gewoon melden, met dezelfde tekst als de
				// niet-streamende route.
				await applyAction({
					type: 'failure',
					status: res.status,
					data: { error: gate, question: q, history: turns }
				});
				return;
			}
			if (!res.ok || !res.body) {
				// Nette foutstatus vóór het streamen. De proxy markeert met
				// retry:true dat rb-api al aan het werk kán zijn (verbinding brak
				// i.p.v. geweigerd) — dan expliciete knop, geen stille terugval.
				let retry = false;
				try {
					retry = Boolean(((await res.json()) as { retry?: boolean }).retry);
				} catch {
					// geen JSON-body — behandel als veilige terugval
				}
				if (retry) {
					retryPending = { fd: formData, clearQuestion };
					announce = 'Antwoord mislukt.';
					return;
				}
				await fallbackAsk(formData, clearQuestion);
				return;
			}

			live = {
				question: q,
				history: turns,
				questionType: null,
				citations: [],
				answer: '',
				approachReason: null
			};
			const reader = res.body.getReader();
			const decoder = new TextDecoder();
			let buffer = '';
			let finalData: Record<string, unknown> | null = null;
			for (;;) {
				const { done, value } = await reader.read();
				if (done) break;
				buffer += decoder.decode(value, { stream: true });
				let nl: number;
				while ((nl = buffer.indexOf('\n')) >= 0) {
					const line = buffer.slice(0, nl).trim();
					buffer = buffer.slice(nl + 1);
					if (!line) continue;
					let frame: {
						type?: string;
						text?: string;
						questionType?: string;
						citations?: unknown[];
						approachReason?: string | null;
						result?: Record<string, unknown>;
						error?: string;
					};
					try {
						frame = JSON.parse(line);
					} catch {
						continue; // half frame door een weggevallen verbinding
					}
					if (frame.type === 'meta' && live) {
						// Citaties zijn vóór het antwoord al bekend: daarmee kunnen
						// [[rule:…]]-widgets tijdens het streamen al renderen.
						live = {
							...live,
							questionType: frame.questionType ?? null,
							citations: frame.citations ?? [],
							approachReason: frame.approachReason ?? null
						};
					} else if (frame.type === 'delta' && live && typeof frame.text === 'string') {
						live = { ...live, answer: live.answer + frame.text };
					} else if (frame.type === 'final') {
						finalData = frame.result ?? null;
					} else if (frame.type === 'error') {
						serverError = String(frame.error ?? 'stream-fout');
						throw new Error(serverError);
					}
				}
			}
			if (finalData) {
				if (finalData.ok === false && live?.answer) {
					// AI viel halverwege uit: het deelantwoord dat er al staat is
					// meer waard dan de kale uitvalmelding in het slotframe —
					// behouden + melden, net als bij een weggevallen verbinding.
					liveError = 'De AI viel halverwege uit — dit antwoord is mogelijk onvolledig.';
					announce = 'Antwoord onderbroken.';
				} else {
					// Slotframe = het volledige AskResult: her-render via het gewone
					// form-pad (incl. citaties, kaarten, claims, feedback, doorvragen).
					// Zelfde afronding als de oude update()-flow: formulier leeg en
					// duurstatistiek vers (review-fix).
					if (clearQuestion) question = '';
					await invalidateAll();
					await applyAction({
						type: 'success',
						status: 200,
						data: { question: q, history: turns, hadPhoto: Boolean(photo), ...finalData }
					});
					live = null;
					announce = 'Antwoord compleet.';
				}
			} else if (!live?.answer) {
				// Gebroken vóór de eerste antwoordtekst: rb-api kan al aan de
				// LLM-call begonnen zijn — expliciete knop, geen stille dubbele
				// kosten (review-fix). Niet op sawFrame toetsen: het meta-frame
				// komt vóór het antwoord al binnen, en bij agentic (#107) zit
				// daar een lange stille wachtfase achter — een breuk dáár hoort
				// dezelfde herkansing te krijgen als een breuk vóór elk frame.
				live = null;
				retryPending = { fd: formData, clearQuestion };
				announce = 'Antwoord mislukt.';
			} else {
				// Midden in het antwoord gebroken: partial behouden (fail-paden
				// laten het antwoord niet verdwijnen), geen dure herkansing.
				liveError = 'De verbinding viel weg — dit antwoord is mogelijk onvolledig.';
				announce = 'Antwoord onderbroken.';
			}
		} catch (e) {
			if (live?.answer) {
				liveError = 'De verbinding viel weg — dit antwoord is mogelijk onvolledig.';
				announce = 'Antwoord onderbroken.';
			} else if (serverError) {
				// Expliciete fout van rb-api (error-frame): eerlijk als fout
				// tonen — dit is geen verbindingskwestie die een retry oplost.
				live = null;
				await applyAction({
					type: 'failure',
					status: 500,
					data: {
						error: `Vraag mislukt (${e instanceof Error ? e.message : e})`,
						question: q,
						history: turns
					}
				});
			} else {
				// Verbindingsbreuk zonder antwoordtekst — ook mét al gezien
				// meta-frame (agentic-wachtfase, #107): expliciete herkansing.
				live = null;
				retryPending = { fd: formData, clearQuestion };
				announce = 'Antwoord mislukt.';
			}
		} finally {
			busy = false;
			clearPhoto();
		}
	}

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

	<!-- Eigen ask-geschiedenis (#157): dicht standaard, laadt al mee met de
	     pagina (+page.server.ts) — geen extra client-fetch nodig. -->
	<AskHistoryPanel items={data.askHistory} loggedIn={data.loggedIn} />

	<form
		method="POST"
		action="?/ask"
		enctype="multipart/form-data"
		use:enhance={async ({ formData, cancel }) => {
			// Guard meteen dicht (review-fix): tijdens 'await downscale' mag een
			// tweede klik geen tweede (betaalde) request kunnen starten.
			if (busy) {
				cancel();
				return;
			}
			busy = true;
			const q = String(formData.get('question') ?? '').trim();
			if (q) remember(q);
			const f = photoInput?.files?.[0];
			const photo = f ? await downscale(f) : null;
			if (photo) formData.set('photo', photo, 'board.jpg');
			else formData.delete('photo');
			// Streaming (#31) waar de browser het kan; anders loopt de
			// bestaande niet-streamende action gewoon door.
			if (q && canStream()) {
				cancel();
				void streamAsk(q, [], photo, formData, true);
				return;
			}
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

	{#if busy && !live?.answer}
		<div class="panel waiting">
			<span class="spin"></span>
			<div>
				<p class="phase">{PHASES[phase]}</p>
				<p class="meta">{waitText}</p>
				{#if liveNotice}<p class="approach-notice">{liveNotice}</p>{/if}
			</div>
		</div>
	{/if}

	{#if form?.error && !live}<p class="warn">{form.error}</p>{/if}

	<!-- Screenreader-status (review-fix): alleen de afronding wordt
	     aangekondigd; het groeiende antwoord zelf is géén live-region, anders
	     wordt bij elke delta het hele antwoord opnieuw voorgelezen. -->
	<p class="visually-hidden" role="status">{announce}</p>

	{#if retryPending && !busy}
		<div class="panel waiting">
			<div>
				<p class="phase">De verbinding brak voordat het antwoord binnenkwam.</p>
				<p class="meta">
					Mogelijk was er al een antwoord onderweg; daarom proberen we niet automatisch
					opnieuw.
				</p>
				<button type="button" class="retry" onclick={retryAsk}>Opnieuw proberen</button>
			</div>
		</div>
	{/if}

	{#if live?.answer}
		<!-- Streaming (#31): het antwoord groeit woord voor woord. Afgeronde
		     regels gaan door de gewone AnswerView (widgets werken al via de
		     meta-citaties); de staart is kale tekst met een cursor. -->
		<article class="panel answer-panel" aria-busy={!liveError}>
			<p class="asked meta">
				{#if live.questionType}<span class="qtype">{TYPE_LABELS[live.questionType] ?? live.questionType}</span>{/if}
				Vraag: {live.question}
			</p>
			{#if liveNotice}<p class="approach-notice">{liveNotice}</p>{/if}
			<AnswerView answer={liveParts.settled} citations={live.citations} cards={[]} />
			<p class="md-tail">{liveParts.tail}{#if !liveError}<span class="cursor"></span>{/if}</p>
			{#if liveError}<p class="warn">{liveError}</p>{/if}
		</article>
	{/if}

	{#if hasAnswer && !busy && !live}
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
			{#if answerNotice}<p class="approach-notice">{answerNotice}</p>{/if}
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

			{#if form?.misconceptions?.length}
				<!-- Misvattingen (#125): verworpen community-lezingen mét officiële
				     weerlegging — naast de community-consensus, herkenbaar als
				     negatieve kennis: zo zit het dus níet. -->
				<h2 class="misconception-h">Veelgemaakte misvatting</h2>
				<p class="meta small misconception-sub">Community-lezing die door de officiële regels is weerlegd — zo zit het dus niet.</p>
				{#each form.misconceptions as m (m.topicRef + m.statement)}
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
						<input type="hidden" name="misconceptions" value={JSON.stringify(form?.misconceptions ?? [])} />
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
						<input type="hidden" name="misconceptions" value={JSON.stringify(form?.misconceptions ?? [])} />
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
						<input type="hidden" name="question" value={form?.question ?? ''} />
						<input type="hidden" name="answer" value={form?.answer ?? ''} />
						<input type="hidden" name="citations" value={JSON.stringify(form?.citations ?? [])} />
						<input type="hidden" name="cards" value={JSON.stringify(form?.cards ?? [])} />
						<input type="hidden" name="claims" value={JSON.stringify(form?.claims ?? [])} />
						<input
							type="hidden"
							name="misconceptions"
							value={JSON.stringify(form?.misconceptions ?? [])}
						/>
						<label for="ruling-statement">Uitspraak</label>
						<textarea id="ruling-statement" name="statement" rows="3">{form?.answer ?? ''}</textarea>
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
		</article>
	{/if}

	{#if hasAnswer && !busy && !live}
		<!-- Doorvragen (#41): bouwt voort op het gesprek, met alle context -->
		<form
			method="POST"
			action="?/ask"
			use:enhance={({ formData, cancel }) => {
				if (busy) {
					cancel();
					return;
				}
				busy = true;
				const q = String(formData.get('question') ?? '').trim();
				if (q) remember(q);
				if (q && canStream()) {
					// Doorvragen streamt ook (#31); de historie reist mee.
					let turns: Turn[] = [];
					try {
						turns = JSON.parse(String(formData.get('history') ?? '[]'));
					} catch {
						turns = [];
					}
					cancel();
					followUp = '';
					void streamAsk(q, turns.slice(-3), null, formData, false);
					return;
				}
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
	.approach-opt:focus-within { outline: 2px solid var(--accent); outline-offset: -2px; }
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
