<script lang="ts">
	import { untrack } from 'svelte';
	import { renderMarkdown, renderInlineMarkdown } from '$lib/markdown';
	import {
		certaintyLevel,
		certaintyParts,
		normalizeAnswerRefs,
		normalizeRuleCode,
		prepareAnswerMarkers,
		stripDuplicateRuleRefs
	} from '$lib/answerFormat';
	import {
		citationPreview,
		placePopover,
		ruleCodeFromHref,
		sharedPreviewCache,
		type RulePreviewData
	} from '$lib/rulePreview';
	import RuleWidget from '$lib/RuleWidget.svelte';
	import CardWidget from '$lib/CardWidget.svelte';
	import RbText from '$lib/RbText.svelte';

	// Rijk antwoord: het LLM plaatst [[rule:…]]/[[card:…]]-markers; wij
	// renderen die als interactieve widgets met de citaten-/kaartendata die
	// toch al in de response zitten. Oordeel/Zekerheid worden een banner.
	let {
		answer,
		citations = [],
		cards = []
	}: {
		answer: string;
		// bewust los getypeerd: de widgets valideren zelf wat ze nodig hebben
		citations?: any[];
		cards?: any[];
	} = $props();

	type Seg = { kind: 'md' | 'rule' | 'card'; value: string };

	const parsed = $derived.by(() => {
		// Vangnet (#69): een "Regelbasis"-blok of §-tabel die alleen de
		// citatielijst onderaan dubbelt wordt niet nogmaals getoond. Daarná
		// de verwijzings-/keyword-normalisatie (#363) — stripDuplicateRuleRefs
		// moet de originele [n]/§-vormen nog zien om ref-blokken te herkennen.
		// prepareAnswerMarkers als laatste: die maakt van mid-zin- en dubbele
		// markers inline tekst/links, en normalizeAnswerRefs mag die nieuwe
		// linkteksten niet nogmaals herschrijven.
		let text = prepareAnswerMarkers(
			normalizeAnswerRefs(stripDuplicateRuleRefs(answer, citations), citations)
		);
		let oordeel: string | null = null;
		let zekerheid: string | null = null;
		// Twee vormen accepteren: "**Oordeel:** zin" én "## Oordeel\n\nzin"
		// (het model wijkt soms af naar koppen).
		text = text.replace(/^\s*\*\*Oordeel:\*\*\s*(.+)$/m, (_, v: string) => {
			oordeel = v.trim();
			return '';
		});
		text = text.replace(/^\s*\*\*Zekerheid:\*\*\s*(.+)$/m, (_, v: string) => {
			zekerheid = v.trim();
			return '';
		});
		if (!oordeel) {
			text = text.replace(/^#{1,3}\s*Oordeel\s*\n+([^\n#][^\n]*(?:\n(?!#{1,3}\s|---)[^\n]*)*)/m, (_, v: string) => {
				oordeel = v.replace(/\n+/g, ' ').trim();
				return '';
			});
		}
		if (!zekerheid) {
			text = text.replace(/^#{1,3}\s*Zekerheid\s*\n+([^\n#][^\n]*(?:\n(?!#{1,3}\s|---)[^\n]*)*)/m, (_, v: string) => {
				zekerheid = v.replace(/\n+/g, ' ').trim();
				return '';
			});
		}
		text = text.replace(/^---\s*$/gm, '');

		const segs: Seg[] = [];
		const re = /\[\[(rule|card):([^\]]+)\]\]/g;
		let last = 0;
		let m: RegExpExecArray | null;
		const seen = new Set<string>();
		while ((m = re.exec(text)) !== null) {
			if (m.index > last) segs.push({ kind: 'md', value: text.slice(last, m.index) });
			// Rule-codes normaliseren ("348." / "§348" → "348") vóór dedup ÉN
			// doorgifte, anders staat §348 er dubbel als het model beide vormen
			// schrijft (melding Sjoerd) — of erger: de slordige variant verdringt
			// de matchende widget en de exacte lookup in RuleWidget faalt.
			const value = m[1] === 'rule' ? normalizeRuleCode(m[2]) : m[2].trim();
			const key = `${m[1]}:${value.toLowerCase()}`;
			if (!seen.has(key)) {
				segs.push({ kind: m[1] as 'rule' | 'card', value });
				seen.add(key);
			}
			last = m.index + m[0].length;
		}
		if (last < text.length) segs.push({ kind: 'md', value: text.slice(last) });
		return { oordeel, zekerheid, segs };
	});

	const zLevel = $derived(certaintyLevel(parsed.zekerheid));
	// Zekerheid als chip (#366): kopdeel ("Zeker", "Hoog") in de niveaukleur,
	// de toelichting erachter blijft muted tekst. De splitsing zit in
	// answerFormat (unit-getest); hier alleen weergave.
	const zParts = $derived(parsed.zekerheid ? certaintyParts(parsed.zekerheid) : null);

	// --- Hover-preview op §-chips (#370) ---
	// De chips zijn kale `<a href="/rules/…">`-links binnen de {@html}-markdown
	// (#363); we hangen dáárom één gedelegeerde hover/focus-handler aan de
	// antwoord-wrapper in plaats van per link een component te bouwen of de
	// {@html}-uitvoer te herschrijven. Popover-state blijft bewust lokaal in
	// deze component (#248: die leeft precies zolang het antwoord leeft, ook in
	// de thread-weergave); alleen de fetch-cache is gedeeld (rulePreview.ts).
	type Pop = {
		code: string;
		/** null = sectie bestaat niet (echte 404) — dan toont de popover dat. */
		data: RulePreviewData | null;
		anchor: HTMLAnchorElement;
		left: number;
		top: number;
		ready: boolean;
	};
	const OPEN_DELAY_MS = 150;
	const popId = $props.id();
	let pop = $state<Pop | null>(null);
	let popEl = $state<HTMLDivElement | null>(null);
	let openTimer: ReturnType<typeof setTimeout> | null = null;
	let pendingAnchor: HTMLAnchorElement | null = null;
	let openSeq = 0;

	// Zonder hover (touch) doet de popover niet mee: de link navigeert daar
	// direct, en een focus-flits vlak vóór de navigatie stoort alleen maar.
	const hoverCapable = () =>
		typeof window !== 'undefined' && window.matchMedia('(hover: hover)').matches;

	/** Herkent een §-chip onder de cursor/focus: alleen de markdown- en
	 *  oordeel-links (#363). De RuleWidget-badges vallen er bewust buiten —
	 *  daar staat de regeltekst al onder. */
	function chipFrom(target: EventTarget | null): { a: HTMLAnchorElement; code: string } | null {
		if (!(target instanceof Element)) return null;
		const a = target.closest('a[href^="/rules/"]');
		if (!(a instanceof HTMLAnchorElement) || !a.closest('.md, .verdict')) return null;
		const code = ruleCodeFromHref(a.getAttribute('href'));
		return code ? { a, code } : null;
	}

	function close() {
		if (openTimer) {
			clearTimeout(openTimer);
			openTimer = null;
		}
		pendingAnchor = null;
		openSeq++;
		if (pop) {
			pop.anchor.removeAttribute('aria-describedby');
			pop = null;
		}
	}

	async function openFor(a: HTMLAnchorElement, code: string) {
		const seq = ++openSeq;
		// Databron: eerst de citaties die al in het antwoord zitten; pas bij een
		// miss één fetch via de gedeelde cache (één in-flight per code).
		let data: RulePreviewData | null | undefined = citationPreview(citations, code);
		if (!data) data = await sharedPreviewCache.get(code);
		// Verlopen (intussen gesloten/elders geopend), transportfout of anchor
		// weg (streaming ververst de {@html}): stilhouden.
		if (seq !== openSeq || data === undefined || !a.isConnected) return;
		a.setAttribute('aria-describedby', popId);
		pop = { code, data, anchor: a, left: 0, top: 0, ready: false };
	}

	function onOver(e: MouseEvent) {
		if (!hoverCapable()) return;
		const hit = chipFrom(e.target);
		if (!hit || pop?.anchor === hit.a || pendingAnchor === hit.a) return;
		if (openTimer) clearTimeout(openTimer);
		pendingAnchor = hit.a;
		// Kleine open-vertraging: er langs strijken opent niets.
		openTimer = setTimeout(() => {
			openTimer = null;
			pendingAnchor = null;
			void openFor(hit.a, hit.code);
		}, OPEN_DELAY_MS);
	}

	function onOut(e: MouseEvent) {
		const hit = chipFrom(e.target);
		if (!hit) return;
		// Bewegen bínnen de chip (naar een glyph-span) is geen vertrek.
		if (e.relatedTarget instanceof Node && hit.a.contains(e.relatedTarget)) return;
		close();
	}

	function onFocusIn(e: FocusEvent) {
		if (!hoverCapable()) return;
		const hit = chipFrom(e.target);
		if (hit) void openFor(hit.a, hit.code);
	}

	function onFocusOut(e: FocusEvent) {
		if (chipFrom(e.target)) close();
	}

	// Escape en scroll sluiten — alleen geregistreerd zolang de popover open
	// staat; $effect draait uitsluitend in de browser, dus SSR-veilig.
	$effect(() => {
		if (!pop) return;
		const onKey = (e: KeyboardEvent) => {
			if (e.key === 'Escape') close();
		};
		const onScroll = () => close();
		window.addEventListener('keydown', onKey);
		window.addEventListener('scroll', onScroll, { capture: true, passive: true });
		return () => {
			window.removeEventListener('keydown', onKey);
			window.removeEventListener('scroll', onScroll, { capture: true });
		};
	});

	// Positioneren ná het renderen: eerst onzichtbaar meten, dan plaatsen
	// (placePopover is puur en unit-getest). Leest code/data zodat nieuwe
	// inhoud opnieuw gemeten wordt; schrijft alleen velden die hij niet leest.
	$effect(() => {
		if (!pop || !popEl) return;
		void pop.code;
		void pop.data;
		if (!pop.anchor.isConnected) {
			close();
			return;
		}
		const placed = placePopover(
			pop.anchor.getBoundingClientRect(),
			{ width: popEl.offsetWidth, height: popEl.offsetHeight },
			{ width: window.innerWidth, height: window.innerHeight }
		);
		pop.left = placed.left;
		pop.top = placed.top;
		pop.ready = true;
	});

	// Nieuw antwoord (streaming vervangt de {@html}-DOM onder de popover
	// vandaan) → dicht; en bij afbraak van de component de timer opruimen.
	// untrack is essentieel: close() léést pop, en zonder untrack wordt pop
	// daarmee een dependency van dit effect — dan sluit elke open meteen weer.
	$effect(() => {
		void answer;
		untrack(close);
	});
	$effect(() => () => {
		if (openTimer) clearTimeout(openTimer);
	});
</script>

<!-- Wrapper voor de gedelegeerde §-chip-popover (#370): display:contents,
     dus geen layoutimpact op de vele plekken waar AnswerView leeft (panelen,
     thread-turns, admin-tabelcellen). Muis-/focus-events bubbelen er gewoon
     doorheen. -->
<!-- De linter herkent alleen de niet-bubbelende focus/blur, maar delegatie
     vereist juist de bubbelende focusin/focusout — die staan er, dus
     toetsenbord-focus krijgt dezelfde popover als hover. De wrapper zelf is
     bewust geen interactief element (de chips blijven de interactie). -->
<!-- svelte-ignore a11y_mouse_events_have_key_events, a11y_no_static_element_interactions -->
<div class="answer-root" onmouseover={onOver} onmouseout={onOut} onfocusin={onFocusIn} onfocusout={onFocusOut}>
{#if parsed.oordeel}
	<div class="verdict {zLevel}">
		<!-- eslint-disable-next-line svelte/no-at-html-tags — bron is ge-escaped vóór markdown-parse -->
		<p class="verdict-text">{@html renderInlineMarkdown(parsed.oordeel)}</p>
		{#if zParts}
			<p class="certainty">
				<span class="certainty-chip {zLevel}">{zParts.head}</span>
				{#if zParts.rest}
					<!-- eslint-disable-next-line svelte/no-at-html-tags — bron is ge-escaped vóór markdown-parse -->
					<span class="certainty-rest">{@html renderInlineMarkdown(zParts.rest)}</span>
				{/if}
			</p>
		{/if}
	</div>
{/if}

{#each parsed.segs as seg, i (i)}
	{#if seg.kind === 'md'}
		{#if seg.value.trim()}
			<!-- eslint-disable-next-line svelte/no-at-html-tags — bron is ge-escaped vóór markdown-parse -->
			<div class="md">{@html renderMarkdown(seg.value)}</div>
		{/if}
	{:else if seg.kind === 'rule'}
		<RuleWidget code={seg.value} {citations} />
	{:else}
		<CardWidget name={seg.value} {cards} />
	{/if}
{/each}

{#if pop}
	<!-- Compacte regel-preview bij de chip: zelfde vormtaal als RuleWidget
	     (ok-accent, surface-deep, border), maar zwevend en niet-interactief —
	     klikken blijft de taak van de link zelf. -->
	<div
		class="rule-pop"
		role="tooltip"
		id={popId}
		bind:this={popEl}
		style:left="{pop.left}px"
		style:top="{pop.top}px"
		style:visibility={pop.ready ? 'visible' : 'hidden'}
	>
		<p class="pop-head"><span class="sec-badge">§ {pop.code}</span></p>
		{#if pop.data}
			{#if pop.data.parent}
				<p class="pop-parent">
					<strong>§ {pop.data.parent.code}</strong>
					<RbText text={pop.data.parent.text} />
				</p>
			{/if}
			{#if pop.data.text}
				<p class="pop-body"><RbText text={pop.data.text} /></p>
			{/if}
		{:else}
			<!-- Echte 404 (#370): liever eerlijk melden dan een chip die "kapot"
			     aanvoelt naast chips die wél een preview geven — en het waarschuwt
			     vóór een klik naar een 404-pagina. -->
			<p class="pop-missing">Geen regeltekst gevonden voor deze verwijzing.</p>
		{/if}
	</div>
{/if}
</div>

<style>
	.answer-root {
		display: contents;
	}
	.rule-pop {
		position: fixed;
		z-index: 40; /* boven de layout-lagen (max 31) */
		max-width: min(360px, calc(100vw - 16px));
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-left: 3px solid var(--ok);
		border-radius: 8px;
		padding: 7px 12px 8px;
		box-shadow: var(--shadow-panel-lg);
		/* Niet-interactief: de muis kan er nooit "op" gaan staan, dus het
		   mouseout van de chip blijft het enige sluitmoment. */
		pointer-events: none;
	}
	.pop-head {
		margin: 0;
	}
	/* Zelfde chip als RuleWidget's sec-badge, hier niet-klikbaar. */
	.sec-badge {
		display: inline-block;
		font-weight: 700;
		color: var(--ok);
		background: var(--ok-soft);
		border-radius: 999px;
		padding: 1px 9px;
		font-size: 0.8rem;
	}
	.pop-parent {
		margin: 5px 0 0;
		color: var(--muted);
		font-size: 0.82rem;
		line-height: 1.45;
	}
	.pop-body {
		margin: 5px 0 0;
		font-size: 0.9rem;
		line-height: 1.5;
		/* Lange secties (errata-chunks) niet laten uitgroeien tot een scherm-
		   vullend paneel: clampen, de volledige tekst is één klik weg. */
		display: -webkit-box;
		-webkit-line-clamp: 10;
		line-clamp: 10;
		-webkit-box-orient: vertical;
		overflow: hidden;
	}
	.pop-missing {
		margin: 5px 0 0;
		color: var(--muted);
		font-size: 0.85rem;
	}
	.verdict {
		border-radius: 10px;
		padding: 12px 16px;
		margin-bottom: 14px;
		border: 1px solid var(--border);
		background: var(--surface-deep);
	}
	.verdict.ok { border-color: var(--ok); background: var(--ok-soft); }
	.verdict.warn { border-color: var(--warn); background: var(--warn-soft); }
	/* Community-consensus (#51): eigen kleur — geen officiële bevestiging. */
	.verdict.community { border-color: var(--accent); background: var(--accent-soft); }
	.verdict.unsure { border-color: var(--border-strong); }
	.verdict-text { margin: 0; font-size: 1.05rem; font-weight: 700; line-height: 1.5; }
	.certainty {
		margin: 6px 0 0;
		display: flex;
		align-items: baseline;
		gap: 8px;
		flex-wrap: wrap;
		color: var(--muted);
		font-size: 0.85rem;
	}
	/* Chip in de niveaukleur — zelfde tokens als de .verdict-box hierboven;
	   unsure blijft bewust neutraal (rand + muted, geen vulling). */
	.certainty-chip {
		display: inline-block;
		padding: 1px 9px;
		border-radius: 999px;
		border: 1px solid var(--border-strong);
		color: var(--muted);
		font-size: 0.75rem;
		font-weight: 600;
		white-space: nowrap;
	}
	.certainty-chip.ok { border-color: var(--ok); background: var(--ok-soft); color: var(--ok); }
	.certainty-chip.warn { border-color: var(--warn); background: var(--warn-soft); color: var(--warn); }
	/* Accent (geel) is als tekstkleur te licht op de soft-achtergrond; de
	   chip volgt daarom de .verdict.community-aanpak: accent-rand en -vulling
	   met de gewone tekstkleur. */
	.certainty-chip.community {
		border-color: var(--accent);
		background: var(--accent-soft);
		color: var(--text);
	}
</style>
