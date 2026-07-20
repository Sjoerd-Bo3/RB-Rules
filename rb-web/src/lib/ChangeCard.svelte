<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { ChangeCardData, ChangeConfirmation } from '$lib/types';
	import RbText from '$lib/RbText.svelte';
	import {
		diffLines,
		domainColorVar,
		formatChangeDate,
		formatChangeWhen,
		severityBadgeClass,
		trustLabel
	} from '$lib/changeCard';

	// Herbruikbare wijzigingskaart (#210): één implementatie voor de publieke
	// feed, het admin-overzicht "wijzigingen" én de compacte varianten in het
	// sectie- en bron-dossier — geen tweede implementatie meer. Hiërarchie:
	// kop (type+severity, bron+trust, datum) → kern (samenvatting, speler-
	// impact) → voet (bevestigingen, voor/na-uitklap, admin-actieslot).
	let {
		change,
		compact = false,
		actions,
		confirmationActions
	}: {
		change: ChangeCardData;
		/** Compact: alleen type+severity, samenvatting en datum — voor dichte
		 *  lijsten (sectie-/bron-dossier) waar bron/context al vaststaat. */
		compact?: boolean;
		/** Admin-acties (bv. Verwijder) — bewust in de voet, niet de kop, zodat
		 *  de kop rustig blijft. */
		actions?: Snippet;
		/** Per-bevestiging admin-actie (Ontkoppel, #206): de kaart geeft de
		 *  bevestiging door zodat het aanroepende formulier het juiste
		 *  confirmedBy-id kent. */
		confirmationActions?: Snippet<[ChangeConfirmation]>;
	} = $props();

	const sevClass = $derived(severityBadgeClass(change.severity));
	const trust = $derived(trustLabel(change.trustTier));
	// Domein-randstreep (design-proof "Domains"): kleur van het geraakte item,
	// terugval op Colorless-neutraal zonder domein (zie changeCard.ts).
	const domVar = $derived(domainColorVar(change.domain));
	const confirmations = $derived(change.confirmedBy ?? []);
	const hasConfirmations = $derived(confirmations.length > 0);
	// Voor/na blijft ná consolidatie inspecteerbaar (#206, review-fix finding
	// 3) — alleen de uitklap tonen als er ook echt iets in te klappen valt.
	const showConfirmationDetails = $derived(
		confirmations.some((cb) => cb.summary || cb.meaning || cb.diff)
	);
</script>

<article class="change-card panel" class:compact style="--dom-color: {domVar}">
	<div class="dom-stripe" aria-hidden="true"></div>
	<div class="cc-body">
		<header class="cc-head">
			<span class="badge {sevClass}">{change.severity}</span>
			<span class="chip-kind">{change.changeType}</span>
			{#if !compact && change.domain}
				<span class="chip-domain">{change.domain}</span>
			{/if}
			{#if !compact && change.sourceName}
				<span class="cc-source">
					{#if change.sourceUrl}
						<a class="src" href={change.sourceUrl} target="_blank" rel="noopener"
							>{change.sourceName} ↗</a
						>
					{:else}
						<span class="src">{change.sourceName}</span>
					{/if}
					{#if trust}
						<span class="trust trust-{trust.tone}">{trust.label}</span>
					{/if}
				</span>
			{/if}
			<time class="when tnum" datetime={change.detectedAt}
				>{compact ? formatChangeDate(change.detectedAt) : formatChangeWhen(change.detectedAt)}</time
			>
		</header>

		{#if change.summary}<p class="summary"><RbText text={change.summary} /></p>{/if}
		{#if !compact && change.meaning}<p class="impact"><RbText text={change.meaning} /></p>{/if}

		{#if !compact}
			{#if hasConfirmations}
				<div class="confirmed-row">
					<span class="badge ok-b">bevestigd</span>
					{#each confirmations as cb (cb.id)}
						{@const cbTrust = trustLabel(cb.trustTier)}
						<div class="confirmed-item">
							{#if cb.sourceUrl}
								<a class="src confirm-link" href={cb.sourceUrl} target="_blank" rel="noopener"
									>door {cb.sourceName} ↗</a
								>
							{:else}
								<span class="src confirm-link">door {cb.sourceName}</span>
							{/if}
							{#if cbTrust}<span class="trust trust-{cbTrust.tone}">{cbTrust.label}</span>{/if}
							{@render confirmationActions?.(cb)}
						</div>
					{/each}
				</div>
				{#if showConfirmationDetails}
					<details>
						<summary>Bevestiging(en) tonen</summary>
						{#each confirmations as cb (cb.id)}
							{#if cb.summary}
								<p class="confirmation"><strong>{cb.sourceName}:</strong> <RbText text={cb.summary} /></p>
							{/if}
							{#if cb.meaning}<p class="confirmation"><RbText text={cb.meaning} /></p>{/if}
							{#if cb.diff}
								<div class="diff">
									{#each diffLines(cb.diff) as l, i (i)}
										<div class="dline {l.kind}">{l.text}</div>
									{/each}
								</div>
							{/if}
						{/each}
					</details>
				{/if}
			{/if}

			{#if change.diff}
				<details>
					<summary>Wat is er precies gewijzigd? (voor/na)</summary>
					<div class="diff">
						{#each diffLines(change.diff) as l, i (i)}
							<div class="dline {l.kind}">{l.text}</div>
						{/each}
					</div>
				</details>
			{/if}

			{#if actions}
				<footer class="cc-actions">{@render actions()}</footer>
			{/if}
		{/if}
	</div>
</article>

<style>
	/* Kaart = domein-randstreep (--dom-color, gezet vanuit de mapping in
	   changeCard.ts) + inhoud. overflow:hidden zodat de streep netjes de
	   .panel-radius van de kaart volgt. */
	.change-card {
		display: flex;
		align-items: stretch;
		padding: 0;
		margin-bottom: 12px;
		overflow: hidden;
	}
	.change-card.compact {
		margin-bottom: 8px;
		border-radius: var(--radius);
	}
	.dom-stripe {
		flex: none;
		width: 4px;
		background: var(--dom-color);
	}
	.cc-body {
		flex: 1;
		min-width: 0;
		padding: 14px 16px;
	}
	.compact .cc-body {
		padding: 8px 12px;
	}
	.cc-head {
		display: flex;
		gap: 8px;
		align-items: center;
		flex-wrap: wrap;
	}
	/* Neutrale "soort"-chip (kind), los van de severity-pil ernaast. */
	.chip-kind {
		display: inline-flex;
		align-items: center;
		background: var(--surface-deep);
		border: 1px solid var(--border);
		color: var(--muted);
		font-size: 0.7rem;
		font-weight: 600;
		text-transform: uppercase;
		letter-spacing: 0.04em;
		padding: 2px 9px;
		border-radius: 999px;
	}
	/* Domein-chip: zachte tint van --dom-color, afgeleid via color-mix zodat
	   er maar één bron (het token) is om aan te passen. */
	.chip-domain {
		display: inline-flex;
		align-items: center;
		background: color-mix(in srgb, var(--dom-color) 16%, transparent);
		border: 1px solid color-mix(in srgb, var(--dom-color) 45%, transparent);
		color: var(--dom-color);
		font-size: 0.7rem;
		font-weight: 700;
		padding: 2px 9px;
		border-radius: 999px;
	}
	.cc-source {
		display: flex;
		align-items: center;
		gap: 6px;
		flex-wrap: wrap;
	}
	.src {
		color: var(--muted);
		font-size: 0.85rem;
		text-decoration: none;
		border-bottom: 1px dotted var(--border-strong);
	}
	a.src:hover {
		color: var(--text);
	}
	.trust {
		font-size: 0.68rem;
		font-weight: 700;
		text-transform: uppercase;
		letter-spacing: 0.04em;
	}
	.trust-official {
		color: var(--ok);
	}
	.trust-community {
		color: var(--warn);
	}
	.when {
		margin-left: auto;
		color: var(--muted);
		font-size: 0.85rem;
		white-space: nowrap;
	}
	.summary {
		margin: 10px 0 4px;
		font-size: 1rem;
		font-weight: 600;
		line-height: 1.5;
	}
	.compact .summary {
		margin: 6px 0 0;
		font-size: 0.92rem;
		font-weight: 500;
	}
	/* Speler-impact (#210): subtiele linker-rand, neutraal — geel is
	   uitsluitend het actie-accent, dus niet ook hier als sfeerkleur. */
	.impact {
		margin: 8px 0;
		padding: 6px 12px;
		border-left: 3px solid var(--border-strong);
		background: var(--surface-deep);
		border-radius: 0 8px 8px 0;
		color: var(--muted);
		font-size: 0.92rem;
		line-height: 1.5;
	}
	.confirmed-row {
		display: flex;
		align-items: center;
		gap: 8px 12px;
		flex-wrap: wrap;
		margin-top: 10px;
	}
	.confirmed-item {
		display: inline-flex;
		align-items: center;
		gap: 6px;
		flex-wrap: wrap;
	}
	.confirm-link {
		font-size: 0.82rem;
	}
	.confirmation {
		font-size: 0.88rem;
		color: var(--muted);
		margin: 4px 0;
	}
	.confirmation strong {
		color: var(--text);
	}
	details {
		margin-top: 8px;
	}
	summary {
		color: var(--muted);
		font-size: 0.85rem;
		cursor: pointer;
	}
	.diff {
		background: var(--surface-deep);
		border: 1px solid var(--border);
		border-radius: 8px;
		padding: 10px 12px;
		margin-top: 8px;
		font-size: 0.85rem;
		max-height: 320px;
		overflow: auto;
	}
	.dline {
		padding: 1px 6px;
		border-radius: 4px;
	}
	.dline.head {
		color: var(--muted);
		font-weight: 700;
		margin-top: 4px;
	}
	.dline.add {
		background: var(--ok-soft);
		color: var(--ok);
	}
	.dline.del {
		background: var(--err-soft);
		color: var(--err);
		text-decoration: line-through;
	}
	.cc-actions {
		margin-top: 10px;
		display: flex;
		gap: 8px;
		flex-wrap: wrap;
	}
</style>
