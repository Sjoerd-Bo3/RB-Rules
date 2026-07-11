<script lang="ts">
	import { enhance } from '$app/forms';

	let { data, form } = $props();

	interface CardItem {
		riftboundId: string; name: string; setLabel: string | null; rarity: string | null;
		type: string | null; variantOf: string | null; embedded: boolean;
		mechanics: string[] | null; updatedAt: string;
	}
	interface Paged<T> { total: number; page: number; pageSize: number; items: T[]; }
	interface ChunkSource { sourceId: string; count: number; }
	interface ChunkItem {
		id: number; sourceId: string; sectionCode: string | null;
		page: number | null; chunkIndex: number; snippet: string;
	}
	interface ChunkOverview extends Paged<ChunkItem> { sources: ChunkSource[]; }
	interface BanItem {
		id: number; name: string; cardRiftboundId: string | null; kind: string;
		format: string; effectiveFrom: string | null; sourceUrl: string; detectedAt: string;
	}
	interface ErratumItem {
		id: number; cardName: string; cardRiftboundId: string | null;
		newText: string; sourceUrl: string; detectedAt: string;
	}
	interface InteractionItem {
		id: number; kind: string; explanation: string; cardAId: string; cardAName: string;
		cardBId: string; cardBName: string; detectedAt: string;
	}
	interface ChangeItem {
		id: number; sourceId: string; sourceName: string; changeType: string;
		severity: string; summary: string | null; meaning: string | null; detectedAt: string;
	}
	interface CorrectionItem {
		id: number; scope: string; ref: string; text: string; question: string | null;
		provenance: string | null; status: string; createdAt: string; verifiedAt: string | null;
	}
	interface KnowledgeItem {
		id: number; kind: string; topic: string; title: string; body: string;
		sectionRefs: string | null; status: string; updatedAt: string;
	}

	const TITLES: Record<string, { title: string; sub: string }> = {
		kaarten: { title: 'Kaarten', sub: 'alle kaarten in de database, doorklikbaar naar de kaartpagina' },
		embeddings: { title: 'Embeddings', sub: 'welke kaarten een embedding hebben — en welke nog niet' },
		analyse: { title: 'Mechanieken-analyse', sub: 'LLM-geminede mechanieken per kaart, plus de restlijst' },
		regelsecties: { title: 'Regelsecties', sub: 'sectie-chunks per bron, in documentvolgorde' },
		bans: { title: 'Bans', sub: 'actuele banlijst uit de officiële bronnen' },
		errata: { title: 'Errata', sub: 'actuele oracle-teksten per kaart' },
		interacties: { title: 'Interacties', sub: 'LLM-geverifieerde kaart-interacties' },
		wijzigingen: { title: 'Wijzigingen', sub: 'de wijzigingshistorie die de feed voedt' },
		correcties: { title: 'Correcties', sub: 'feedback op antwoorden en geverifieerde rulings' },
		primer: { title: 'Spelbegrip-primer', sub: 'alle spelbegrip-docs, bewerkbaar — goedgekeurde docs voeden elke ruling' }
	};
	const meta = $derived(TITLES[data.kind]);

	const isCardKind = $derived(['kaarten', 'embeddings', 'analyse'].includes(data.kind));
	const cards = $derived(isCardKind ? (data.data as Paged<CardItem> | null) : null);
	const chunks = $derived(data.kind === 'regelsecties' ? (data.data as ChunkOverview | null) : null);
	const bans = $derived(data.kind === 'bans' ? ((data.data ?? []) as BanItem[]) : []);
	const errata = $derived(data.kind === 'errata' ? ((data.data ?? []) as ErratumItem[]) : []);
	const interactions = $derived(data.kind === 'interacties' ? (data.data as Paged<InteractionItem> | null) : null);
	const changes = $derived(data.kind === 'wijzigingen' ? (data.data as Paged<ChangeItem> | null) : null);
	const corrections = $derived(data.kind === 'correcties' ? ((data.data ?? []) as CorrectionItem[]) : []);
	const knowledge = $derived(data.kind === 'primer' ? ((data.data ?? []) as KnowledgeItem[]) : []);

	// saveKnowledge-fouten dragen het doc-id mee zodat de melding bij het
	// juiste bewerkformulier landt; andere actie-fouten hebben geen id.
	const formDocId = $derived(form && 'id' in form ? (form.id as number) : null);

	// Welk primer-doc in bewerk-modus staat; reset bij route-hergebruik
	// (dit component blijft leven bij navigatie tussen kinds).
	let editing = $state<number | null>(null);
	$effect(() => {
		void data.kind;
		editing = null;
	});

	const paged = $derived(cards ?? chunks ?? interactions ?? changes);
	const totalPages = $derived(paged ? Math.max(1, Math.ceil(paged.total / paged.pageSize)) : 1);

	function href(overrides: { page?: number; filter?: string; source?: string }): string {
		const sp = new URLSearchParams();
		if (data.q) sp.set('q', data.q);
		const filter = overrides.filter ?? data.filter;
		if (filter) sp.set('filter', filter);
		const source = overrides.source ?? data.source;
		if (source) sp.set('source', source);
		sp.set('page', String(overrides.page ?? 1));
		return `?${sp}`;
	}

	function fmtDate(iso: string | null): string {
		return iso ? new Date(iso).toLocaleDateString('nl-NL') : '—';
	}
</script>

<svelte:head><title>{meta.title} — Beheer — RB Rules</title></svelte:head>

<main>
	<nav class="crumb"><a href="/admin">Beheer</a> / {meta.title}</nav>
	<h1>{meta.title}</h1>
	<p class="sub">{meta.sub}</p>

	{#if data.apiDown}
		<p class="warn">rb-api is niet bereikbaar — probeer het zo opnieuw.</p>
	{:else}
		<!-- Fouten mét doc-id landen bij het bijbehorende bewerkformulier. -->
		{#if form?.error && formDocId === null}<p class="warn">{form.error}</p>{/if}

		<!-- Filters -->
		{#if data.kind === 'kaarten'}
			<form method="GET" class="filters">
				<input type="search" name="q" value={data.q} placeholder="Zoek op kaartnaam" aria-label="Zoek op kaartnaam" />
				<button type="submit">Zoek</button>
			</form>
		{:else if data.kind === 'embeddings'}
			<div class="chips">
				<a class="chip" class:active={data.filter === 'embedded'} aria-current={data.filter === 'embedded' ? 'page' : undefined} href={href({ filter: 'embedded', page: 1 })}>Geëmbed</a>
				<a class="chip" class:active={data.filter === 'unembedded'} aria-current={data.filter === 'unembedded' ? 'page' : undefined} href={href({ filter: 'unembedded', page: 1 })}>Nog niet geëmbed</a>
			</div>
		{:else if data.kind === 'analyse'}
			<div class="chips">
				<a class="chip" class:active={data.filter === 'mined'} aria-current={data.filter === 'mined' ? 'page' : undefined} href={href({ filter: 'mined', page: 1 })}>Geanalyseerd</a>
				<a class="chip" class:active={data.filter === 'unmined'} aria-current={data.filter === 'unmined' ? 'page' : undefined} href={href({ filter: 'unmined', page: 1 })}>Nog niet geanalyseerd</a>
			</div>
		{:else if chunks && chunks.sources.length > 1}
			<div class="chips">
				<!-- Som over de bronnen: chunks.total is het gefilterde totaal. -->
				<a class="chip" class:active={!data.source} aria-current={!data.source ? 'page' : undefined} href={href({ source: '', page: 1 })}>Alle bronnen ({chunks.sources.reduce((a, s) => a + s.count, 0)})</a>
				{#each chunks.sources as s (s.sourceId)}
					<a class="chip" class:active={data.source === s.sourceId} aria-current={data.source === s.sourceId ? 'page' : undefined} href={href({ source: s.sourceId, page: 1 })}>{s.sourceId} ({s.count})</a>
				{/each}
			</div>
		{/if}

		{#if paged}
			<p class="meta count">{paged.total} totaal{totalPages > 1 ? ` · pagina ${paged.page} van ${totalPages}` : ''}</p>
		{:else if data.kind === 'correcties' && corrections.length}
			<p class="meta count">{corrections.length} getoond{corrections.length >= 200 ? ' (de laatste 200 — oudere correcties vallen buiten dit overzicht)' : ''}</p>
		{:else if data.kind === 'primer' && knowledge.length}
			<p class="meta count">{knowledge.length} docs · {knowledge.filter((k) => k.status === 'approved').length} goedgekeurd · {knowledge.filter((k) => k.status !== 'approved').length} draft</p>
		{/if}

		<!-- Kaart-overzichten (kaarten / embeddings / analyse) -->
		{#if cards}
			<div class="table-wrap">
				<table>
					<thead>
						<tr>
							<th>Kaart</th><th>Set</th><th>Type</th><th>Rarity</th>
							<th>Geëmbed</th><th>Mechanieken</th><th>Bijgewerkt</th>
						</tr>
					</thead>
					<tbody>
						{#each cards.items as c (c.riftboundId)}
							<tr>
								<td>
									<a href="/cards/{c.riftboundId}"><strong>{c.name}</strong></a>
									{#if c.variantOf}<span class="meta"> · variant van {c.variantOf}</span>{/if}
									<br /><span class="meta">{c.riftboundId}</span>
								</td>
								<td class="meta">{c.setLabel ?? '—'}</td>
								<td class="meta">{c.type ?? '—'}</td>
								<td class="meta">{c.rarity ?? '—'}</td>
								<td><span class="badge {c.embedded ? 'ok-b' : 'warn-b'}">{c.embedded ? 'ja' : 'nee'}</span></td>
								<td class="meta mech">
									{#if c.mechanics === null}<span class="badge warn-b">nog niet</span>
									{:else if c.mechanics.length === 0}geen
									{:else}{c.mechanics.join(', ')}{/if}
								</td>
								<td class="meta">{fmtDate(c.updatedAt)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}

		<!-- Regelsecties -->
		{#if chunks}
			<div class="table-wrap">
				<table>
					<thead><tr><th>§</th><th>Bron</th><th>Pagina</th><th>Tekst</th></tr></thead>
					<tbody>
						{#each chunks.items as rc (rc.id)}
							<tr>
								<td class="nowrap"><strong>{rc.sectionCode ?? '—'}</strong></td>
								<td class="meta nowrap">{rc.sourceId}</td>
								<td class="meta">{rc.page ?? '—'}</td>
								<td class="meta snippet">{rc.snippet}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}

		<!-- Bans -->
		{#if data.kind === 'bans'}
			<div class="table-wrap">
				<table>
					<thead><tr><th>Naam</th><th>Soort</th><th>Format</th><th>Vanaf</th><th>Bron</th></tr></thead>
					<tbody>
						{#each bans as b (b.id)}
							<tr>
								<td>
									{#if b.cardRiftboundId}<a href="/cards/{b.cardRiftboundId}"><strong>{b.name}</strong></a>
									{:else}<strong>{b.name}</strong> <span class="meta">(niet gematcht)</span>{/if}
								</td>
								<td class="meta">{b.kind}</td>
								<td class="meta">{b.format}</td>
								<td class="meta">{fmtDate(b.effectiveFrom)}</td>
								<td class="meta"><a href={b.sourceUrl}>bron</a></td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if !bans.length}<p class="meta">Geen bans bekend.</p>{/if}
		{/if}

		<!-- Errata -->
		{#if data.kind === 'errata'}
			{#each errata as e (e.id)}
				<div class="panel item">
					<p class="item-head">
						{#if e.cardRiftboundId}<a href="/cards/{e.cardRiftboundId}"><strong>{e.cardName}</strong></a>
						{:else}<strong>{e.cardName}</strong>{/if}
						<span class="meta">{fmtDate(e.detectedAt)} · <a href={e.sourceUrl}>bron</a></span>
					</p>
					<p class="pre">{e.newText}</p>
				</div>
			{/each}
			{#if !errata.length}<p class="meta">Geen errata bekend.</p>{/if}
		{/if}

		<!-- Interacties -->
		{#if interactions}
			{#each interactions.items as i (i.id)}
				<div class="panel item">
					<p class="item-head">
						<span class="badge ok-b">{i.kind}</span>
						<a href="/cards/{i.cardAId}"><strong>{i.cardAName}</strong></a>
						<span class="meta">+</span>
						<a href="/cards/{i.cardBId}"><strong>{i.cardBName}</strong></a>
						<span class="meta">{fmtDate(i.detectedAt)}</span>
					</p>
					<p class="pre">{i.explanation}</p>
				</div>
			{/each}
		{/if}

		<!-- Wijzigingen -->
		{#if changes}
			{#each changes.items as c (c.id)}
				<div class="panel item">
					<p class="item-head">
						<span class="badge {c.severity === 'high' ? 'err' : c.severity === 'medium' ? 'warn-b' : 'ok-b'}">{c.severity}</span>
						<strong>{c.changeType}</strong>
						<span class="meta">{c.sourceName} · {fmtDate(c.detectedAt)}</span>
					</p>
					{#if c.summary}<p class="pre">{c.summary}</p>{/if}
					{#if c.meaning}<p class="meta">{c.meaning}</p>{/if}
					{#if !c.summary && !c.meaning}<p class="meta">Zonder samenvatting (zie #58).</p>{/if}
				</div>
			{/each}
		{/if}

		<!-- Correcties -->
		{#if data.kind === 'correcties'}
			{#each corrections as c (c.id)}
				<div class="panel item corr">
					<div class="corr-body">
						<p class="item-head">
							<span class="badge {c.status === 'verified' ? 'ok-b' : 'warn-b'}">{c.status === 'verified' ? 'geverifieerd' : 'open'}</span>
							<span class="meta">{c.scope} · {c.ref === 'down' ? 'gemeld als onjuist' : c.ref === 'up' ? 'bevestigd als juist' : c.ref} · {fmtDate(c.createdAt)}</span>
						</p>
						{#if c.question}<p class="meta">{c.question}</p>{/if}
						<p class="pre">{c.text}</p>
					</div>
					{#if c.status !== 'verified'}
						<div class="corr-actions">
							<form method="POST" action="?/verifyCorrection" use:enhance>
								<input type="hidden" name="id" value={c.id} />
								<button title="Maakt dit een gezaghebbende ruling voor toekomstige antwoorden">Verifieer</button>
							</form>
							<form method="POST" action="?/deleteCorrection" use:enhance>
								<input type="hidden" name="id" value={c.id} />
								<button class="ghost small">Verwijder</button>
							</form>
						</div>
					{/if}
				</div>
			{/each}
			{#if !corrections.length}<p class="meta">Geen correcties.</p>{/if}
		{/if}

		<!-- Spelbegrip-primer (#70): alle docs leesbaar en bewerkbaar -->
		{#if data.kind === 'primer'}
			{#each knowledge as k (k.id)}
				<div class="panel item">
					{#if editing === k.id}
						<form
							method="POST"
							action="?/saveKnowledge"
							class="edit"
							use:enhance={() =>
								async ({ update, result }) => {
									await update();
									if (result.type === 'success') editing = null;
								}}
						>
							<input type="hidden" name="id" value={k.id} />
							<label>Titel <input name="title" value={k.title} required /></label>
							<label>Tekst <textarea name="body" rows="14" required>{k.body}</textarea></label>
							{#if form?.error && formDocId === k.id}<p class="warn">{form.error}</p>{/if}
							<div class="row">
								<button type="submit">Opslaan</button>
								<button type="button" class="ghost small" onclick={() => (editing = null)}>Annuleer</button>
								<span class="meta">Opslaan embedt de tekst opnieuw; de status ({k.status === 'approved' ? 'goedgekeurd' : 'draft'}) blijft staan.</span>
							</div>
						</form>
					{:else}
						<p class="item-head">
							<strong>{k.title}</strong>
							<span class="badge {k.status === 'approved' ? 'ok-b' : 'warn-b'}">{k.status === 'approved' ? 'goedgekeurd' : 'draft'}</span>
							<span class="meta">{k.topic} · {fmtDate(k.updatedAt)}</span>
						</p>
						<p class="pre">{k.body}</p>
						{#if k.sectionRefs}<p class="meta refs">Gebaseerd op §{k.sectionRefs}</p>{/if}
						<div class="row actions">
							<button type="button" class="ghost small" onclick={() => (editing = k.id)}>Bewerk</button>
							{#if k.status === 'approved'}
								<form method="POST" action="?/unapproveKnowledge" use:enhance>
									<input type="hidden" name="id" value={k.id} />
									<button class="ghost small" title="Doet dan niet meer mee in de vraag-context tot her-goedkeuring">Terug naar draft</button>
								</form>
							{:else}
								<form method="POST" action="?/approveKnowledge" use:enhance>
									<input type="hidden" name="id" value={k.id} />
									<button class="small" title="Goedgekeurde docs gaan mee als spelbegrip in elke vraag">Keur goed</button>
								</form>
							{/if}
							<form method="POST" action="?/deleteKnowledge" use:enhance>
								<input type="hidden" name="id" value={k.id} />
								<button class="ghost small">Verwijder</button>
							</form>
						</div>
					{/if}
				</div>
			{/each}
			{#if !knowledge.length}<p class="meta">Nog geen spelbegrip-docs — draai "Primer genereren" in het beheer.</p>{/if}
		{/if}

		<!-- Paginering -->
		{#if paged && totalPages > 1}
			<nav class="pager">
				{#if paged.page > 1}<a href={href({ page: paged.page - 1 })}>Vorige</a>{/if}
				<span class="meta">pagina {paged.page} van {totalPages}</span>
				{#if paged.page < totalPages}<a href={href({ page: paged.page + 1 })}>Volgende</a>{/if}
			</nav>
		{/if}
	{/if}
</main>

<style>
	main { max-width: 1080px; margin: 0 auto; padding: 24px 20px; }
	.crumb { color: var(--muted); font-size: 0.85rem; margin-bottom: 6px; }
	.crumb a { color: var(--accent); text-decoration: none; }
	h1 { margin: 0 0 2px; }
	.sub { color: var(--muted); margin: 0 0 18px; }
	.count { margin: 0 0 10px; }
	.filters { display: flex; gap: 8px; margin-bottom: 14px; }
	.filters input {
		flex: 1; max-width: 340px; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; font-size: 16px;
	}
	button {
		background: var(--accent); color: var(--accent-ink); border: 0;
		border-radius: 8px; padding: 8px 14px; font-weight: 600; cursor: pointer;
	}
	button.ghost { background: transparent; color: var(--muted); border: 1px solid var(--border); }
	button.small { padding: 4px 10px; font-size: 0.82rem; }
	.chips { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 14px; }
	.chip {
		border: 1px solid var(--border); border-radius: 999px; padding: 4px 12px;
		color: var(--muted); text-decoration: none; font-size: 0.85rem;
	}
	.chip.active { border-color: var(--accent); color: var(--accent); }
	.table-wrap { overflow-x: auto; }
	table { width: 100%; border-collapse: collapse; margin-bottom: 20px; }
	th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
	th { color: var(--muted); font-size: 0.82rem; font-weight: 600; }
	td a { color: inherit; }
	.nowrap { white-space: nowrap; }
	.snippet { max-width: 560px; }
	.mech { max-width: 260px; }
	.item { padding: 12px 14px; margin-bottom: 8px; }
	.item-head { margin: 0 0 6px; display: flex; align-items: baseline; gap: 8px; flex-wrap: wrap; }
	.item-head a { color: inherit; }
	.pre { margin: 0; white-space: pre-wrap; line-height: 1.55; }
	.corr { display: flex; gap: 14px; }
	.corr-body { flex: 1; }
	.corr-actions { display: flex; flex-direction: column; gap: 6px; }
	.refs { margin: 6px 0 0; }
	.row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
	.actions { margin-top: 10px; }
	.edit { display: flex; flex-direction: column; gap: 10px; }
	.edit label { display: flex; flex-direction: column; gap: 4px; color: var(--muted); font-size: 0.85rem; }
	.edit input, .edit textarea {
		width: 100%; box-sizing: border-box; background: var(--surface-deep); color: var(--text);
		border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;
		font-size: 16px; /* iOS zoomt in op form-controls kleiner dan 16px */
		font-family: inherit; line-height: 1.55;
	}
	.pager { display: flex; align-items: center; gap: 14px; margin: 16px 0; }
	.pager a { color: var(--accent); text-decoration: none; font-weight: 600; }
	.meta { color: var(--muted); font-size: 0.85rem; }
	.badge {
		font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.04em;
		padding: 2px 8px; border-radius: 999px; font-weight: 700;
	}
	.badge.ok-b { background: var(--ok-soft); color: var(--ok); }
	.badge.warn-b { background: var(--warn-soft); color: var(--warn); }
	.badge.err { background: var(--err-soft); color: var(--err); }
	.warn { color: var(--err); }
</style>
