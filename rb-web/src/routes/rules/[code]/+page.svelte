<script lang="ts">
	let { data } = $props();
	const s = $derived(data.section);

	// Broodkruimel: 601.2.d → [601, 601.2, 601.2.d]
	const crumbs = $derived.by(() => {
		const parts = s.code.split('.');
		return parts.map((_, i) => parts.slice(0, i + 1).join('.'));
	});

	// Temporele precedentie (#168): "laatst bijgewerkt" weegt zwaarder dan
	// "geldig sinds" — beide null ⇒ geen label (de bron droeg geen van beide).
	const dateLabel = $derived.by(() => {
		if (s.sourceUpdatedAt) return `laatst bijgewerkt ${new Date(s.sourceUpdatedAt).toLocaleDateString('nl-NL')}`;
		if (s.sourcePublishedAt) return `geldig sinds ${new Date(s.sourcePublishedAt).toLocaleDateString('nl-NL')}`;
		return null;
	});

	let copied = $state(false);
	async function copyLink() {
		try {
			await navigator.clipboard.writeText(location.href);
			copied = true;
			setTimeout(() => (copied = false), 1500);
		} catch {
			/* clipboard niet beschikbaar */
		}
	}
</script>

<svelte:head><title>§ {s.code} — {s.sourceName} — RB Rules</title></svelte:head>

<main>
	<nav class="crumbs">
		<a href="/rules">Regels</a>
		{#each crumbs as c, i (c)}
			<span>›</span>
			{#if i === crumbs.length - 1}
				<strong>§ {c}</strong>
			{:else}
				<a href="/rules/{encodeURIComponent(c)}?source={s.sourceId}">§ {c}</a>
			{/if}
		{/each}
	</nav>

	<h1>§ {s.code} <span class="src">{s.sourceName}</span></h1>
	{#if dateLabel}<p class="meta small">{dateLabel}</p>{/if}

	{#if s.parents.length}
		<!-- Bovenliggende regels als context boven de subregel -->
		<div class="parents">
			{#each s.parents as par (par.code)}
				<p class="parent">
					<a href="/rules/{encodeURIComponent(par.code)}?source={s.sourceId}">§ {par.code}</a>
					{par.text}
				</p>
			{/each}
		</div>
	{/if}

	<article class="card">
		<p class="text">{s.text}</p>
	</article>

	<div class="actions">
		<button onclick={copyLink}>{copied ? 'Gekopieerd' : 'Kopieer permalink'}</button>
		{#if s.pdfUrl}
			<a class="ext" href="{s.pdfUrl}{s.page ? `#page=${s.page}` : ''}" target="_blank" rel="noopener">
				Officiële PDF{s.page ? ` — pagina ${s.page}` : ''}
			</a>
		{:else}
			<a class="ext" href={s.sourceUrl} target="_blank" rel="noopener">Officiële bron</a>
		{/if}
	</div>

	<div class="pager">
		{#if s.prev}
			<a href="/rules/{encodeURIComponent(s.prev)}?source={s.sourceId}">← § {s.prev}</a>
		{:else}<span></span>{/if}
		{#if s.next}
			<a href="/rules/{encodeURIComponent(s.next)}?source={s.sourceId}">§ {s.next} →</a>
		{/if}
	</div>

	<!-- Sectie-dossier (#127): de levende geschiedenis van deze regel.
	     Lege hoofdstukken worden verborgen. -->
	{#if data.dossier.explains.length}
		<section id="uitleg">
			<h2>Uitleg in het spelbegrip</h2>
			<ul class="plain">
				{#each data.dossier.explains as e (e.topic)}
					<li><a href="/primer#{encodeURIComponent(e.topic)}">{e.title}</a></li>
				{/each}
			</ul>
			<p class="meta small">Primer-concepten die deze sectie uitleggen — gedistilleerd uit de officiële regels.</p>
		</section>
	{/if}

	{#if data.dossier.cards.length}
		<section id="kaarten">
			<h2>Kaarten die op deze regel leunen</h2>
			<div class="cards">
				{#each data.dossier.cards as c (c.riftboundId)}
					<a class="mini" href="/cards/{c.riftboundId}">
						{#if c.imageUrl}<img src={c.imageUrl} alt={c.name} loading="lazy" />{/if}
						<span class="mini-name">{c.name}</span>
						{#if c.type}<span class="meta small">{c.type}</span>{/if}
					</a>
				{/each}
			</div>
			<p class="meta small">Semantisch gematcht op de sectietekst — kaarten waarvan de tekst het dichtst bij deze regel ligt.</p>
		</section>
	{/if}

	{#if data.dossier.claims.length}
		<section id="claims">
			<h2>Community-inzichten over deze regel</h2>
			{#each data.dossier.claims as cl (cl.id)}
				<div class="box">
					<p class="statement">{cl.statement}</p>
					<p class="meta small">{cl.trustLabel}</p>
				</div>
			{/each}
			<p class="meta small">Community-interpretatie — de officiële tekst hierboven gaat altijd voor.</p>
		</section>
	{/if}

	{#if data.dossier.changes.length}
		<section id="wijzigingen">
			<h2>Wijzigingen die deze regel raakten</h2>
			{#each data.dossier.changes as ch (ch.id)}
				<div class="box change">
					<p class="change-head">
						<span class="badge sev-{ch.severity}">{ch.changeType}</span>
						<time class="meta small" datetime={ch.detectedAt}>{new Date(ch.detectedAt).toLocaleDateString('nl-NL')}</time>
					</p>
					{#if ch.summary}<p class="statement">{ch.summary}</p>{/if}
				</div>
			{/each}
			<p class="meta small">Uit de wijzigingen-feed, gekoppeld via de kennisgraaf — <a href="/">bekijk alle wijzigingen</a>.</p>
		</section>
	{/if}
</main>

<style>
	main { max-width: 860px; margin: 0 auto; padding: 24px 20px; }
	.crumbs { color: #9fb0cc; display: flex; gap: 8px; flex-wrap: wrap; font-size: 0.9rem; }
	.crumbs a { color: #9fb0cc; text-decoration: none; }
	.crumbs a:hover { color: #d98a4e; }
	h1 { margin: 10px 0 14px; }
	.src { color: #d98a4e; font-size: 1rem; font-weight: 600; margin-left: 10px; }
	.parents {
		border-left: 2px solid var(--border);
		padding-left: 12px; margin-bottom: 12px;
	}
	.parent { margin: 6px 0; color: var(--muted); font-size: 0.9rem; }
	.parent a { color: var(--muted); font-weight: 700; text-decoration: none; }
	.parent a:hover { color: var(--accent); }
	.card {
		background: var(--surface); border: 1px solid var(--border);
		border-radius: 12px; padding: 18px 20px;
	}
	.text { white-space: pre-wrap; margin: 0; line-height: 1.65; }
	/* Wrap op smal: knop + lange PDF-link passen niet altijd naast elkaar. */
	.actions { display: flex; gap: 8px 12px; align-items: center; flex-wrap: wrap; margin: 14px 0; }
	button {
		background: #d98a4e; color: #1a1206; border: 0; border-radius: 8px;
		padding: 8px 14px; font-weight: 600; cursor: pointer;
	}
	.ext { color: #9fb0cc; }
	.pager { display: flex; justify-content: space-between; margin-top: 18px; }
	.pager a { color: #e7eefc; text-decoration: none; font-weight: 600; }
	.pager a:hover { color: #d98a4e; }
	/* Sectie-dossier (#127) — ontwerptokens, ankerdoelen vrij van de header. */
	section[id] { scroll-margin-top: 70px; }
	h2 { font-size: 1rem; color: var(--accent); margin: 22px 0 8px; }
	.meta { color: var(--muted); }
	.small { font-size: 0.8rem; }
	.plain { list-style: none; margin: 0; padding: 0; display: grid; gap: 4px; }
	.plain a { color: var(--text); font-weight: 600; text-decoration: none; }
	.plain a:hover { color: var(--accent); }
	.cards { display: grid; gap: 12px; grid-template-columns: repeat(auto-fill, minmax(120px, 1fr)); }
	.mini { display: flex; flex-direction: column; gap: 2px; font-size: 0.85rem; text-decoration: none; color: inherit; }
	.mini img { width: 100%; border-radius: 10px; border: 1px solid var(--border); }
	.mini:hover img { border-color: var(--accent); }
	.mini-name { font-weight: 600; }
	.box {
		background: var(--surface); border: 1px solid var(--border);
		border-radius: var(--radius); padding: 10px 14px; margin-bottom: 8px;
	}
	.statement { margin: 4px 0; overflow-wrap: anywhere; }
	.change-head { display: flex; align-items: baseline; gap: 10px; margin: 0; flex-wrap: wrap; }
	.badge {
		font-size: 0.7rem; font-weight: 700; letter-spacing: 0.05em; text-transform: uppercase;
		border-radius: 999px; padding: 2px 9px; background: var(--accent-soft); color: var(--warn);
	}
	.badge.sev-high { background: var(--err-soft); color: var(--err); }
	.badge.sev-low { background: var(--surface-deep); color: var(--muted); }
	section .meta a { color: var(--muted); }
</style>
