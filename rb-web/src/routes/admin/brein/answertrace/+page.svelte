<script lang="ts">
	let { data } = $props();

	interface TraceListItem {
		id: string;
		question: string;
		questionType: string;
		retrievalMode: string;
		primaryChannel: string;
		supportCount: number;
		createdAt: string;
	}
	interface Support {
		citationN: number;
		subjectRef: string;
		tier: string;
		trustWeightAtQuery: number;
		widgetMarker: string | null;
	}
	interface TraceDetail {
		id: string;
		question: string;
		questionType: string;
		retrievalMode: string;
		fallbackReason: string | null;
		beta: number;
		primaryChannel: string;
		gateMemo: string | null;
		graphEpoch: string | null;
		llmModel: string | null;
		promptVersion: string | null;
		embeddingRev: string | null;
		createdAt: string;
		supports: Support[];
	}

	const list = $derived(data.list as TraceListItem[] | null);
	const detail = $derived(data.detail as TraceDetail | null);

	function channelClass(channel: string): string {
		if (channel === 'official') return 'ok';
		if (channel === 'none') return 'err';
		return 'accent';
	}
	function tierClass(tier: string): string {
		const t = tier.toLowerCase();
		if (t === 'official') return 'ok';
		if (t === 'community' || t === 'meta') return 'warn';
		return 'accent';
	}
	const fmtDate = (s: string) => new Date(s).toLocaleString('nl-NL');
</script>

{#if data.apiDown}
	<p class="apidown">Het brein is niet bereikbaar — is rb-api op?</p>
{:else}
	<div class="layout" class:split={detail}>
		<div class="list">
			<h2>Recente antwoorden</h2>
			{#if !list || !list.length}
				<div class="empty">
					Nog geen AnswerTraces — elke /ask-beurt legt er één vast zodra de GraphRAG-retrieval
					bedraad is. Stel een vraag op /ask.
				</div>
			{:else}
				<ul class="traces">
					{#each list as t (t.id)}
						<li class:on={data.id === t.id}>
							<a href="?id={encodeURIComponent(t.id)}">
								<span class="q">{t.question || '(lege vraag)'}</span>
								<span class="meta">
									<span class="tier {channelClass(t.primaryChannel)}">{t.primaryChannel}</span>
									<span class="muted">{t.questionType} · {t.retrievalMode}</span>
									<span class="muted tnum">{t.supportCount} steun · {fmtDate(t.createdAt)}</span>
								</span>
							</a>
						</li>
					{/each}
				</ul>
			{/if}
		</div>

		{#if detail}
			<aside class="detail">
				<div class="dhead">
					<h2>Herspeelbaar</h2>
					<a class="close" href="?">sluiten</a>
				</div>
				<p class="question">{detail.question || '(lege vraag)'}</p>

				<dl class="epoch">
					<div><dt>Kanaal</dt><dd><span class="tier {channelClass(detail.primaryChannel)}">{detail.primaryChannel}</span></dd></div>
					<div><dt>Vraagtype</dt><dd>{detail.questionType}</dd></div>
					<div><dt>Modus</dt><dd>{detail.retrievalMode}{detail.fallbackReason ? ` (terugval: ${detail.fallbackReason})` : ''}</dd></div>
					<div><dt>β(q)</dt><dd class="tnum">{detail.beta.toFixed(2)}</dd></div>
					<div><dt>Graph-epoch</dt><dd class="ref">{detail.graphEpoch ?? '—'}</dd></div>
					<div><dt>Model</dt><dd>{detail.llmModel ?? '—'}</dd></div>
					<div><dt>Prompt</dt><dd>{detail.promptVersion ?? '—'}</dd></div>
					<div><dt>Embedding-rev</dt><dd>{detail.embeddingRev ?? '—'}</dd></div>
					<div><dt>Vastgelegd</dt><dd class="tnum">{fmtDate(detail.createdAt)}</dd></div>
				</dl>

				{#if detail.gateMemo}
					<p class="gate muted">Gating: {detail.gateMemo}</p>
				{/if}

				<h3>Dragende feiten (subgraaf / paden)</h3>
				{#if !detail.supports.length}
					<p class="empty">Geen dragende feiten vastgelegd.</p>
				{:else}
					<div class="table-wrap">
						<table>
							<thead>
								<tr><th class="num">Cit.</th><th>Feit</th><th>Tier</th><th class="num">Trust (toen)</th></tr>
							</thead>
							<tbody>
								{#each detail.supports as s (s.citationN + s.subjectRef)}
									<tr>
										<td class="num tnum">{s.citationN}</td>
										<td><span class="ref">{s.subjectRef}</span></td>
										<td><span class="tier {tierClass(s.tier)}">{s.tier}</span></td>
										<td class="num tnum">{s.trustWeightAtQuery.toFixed(2)}</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
				{/if}
			</aside>
		{/if}
	</div>
{/if}

<style>
	.layout.split {
		display: grid;
		grid-template-columns: minmax(0, 1fr) minmax(0, 1.1fr);
		gap: 20px;
		align-items: start;
	}
	@media (max-width: 900px) {
		.layout.split {
			grid-template-columns: minmax(0, 1fr);
		}
	}
	ul.traces {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: 8px;
	}
	ul.traces li a {
		display: flex;
		flex-direction: column;
		gap: 6px;
		padding: 11px 13px;
		border: 1px solid var(--border);
		border-radius: var(--radius-lg);
		background: var(--surface);
		text-decoration: none;
		color: var(--text);
	}
	ul.traces li a:hover {
		border-color: var(--border-strong);
	}
	ul.traces li.on a {
		border-color: var(--accent);
		background: var(--accent-soft);
	}
	.q {
		font-weight: 600;
		font-size: 0.88rem;
	}
	.meta {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: 8px;
		font-size: 0.74rem;
	}
	.detail {
		position: sticky;
		top: 12px;
	}
	.dhead {
		display: flex;
		align-items: baseline;
		justify-content: space-between;
	}
	.dhead h2 {
		margin: 0;
	}
	.close {
		font-size: 0.78rem;
		color: var(--muted);
		text-decoration: none;
	}
	.close:hover {
		color: var(--text);
	}
	.question {
		font-weight: 650;
		font-size: 0.95rem;
		margin: 6px 0 14px;
	}
	dl.epoch {
		display: grid;
		grid-template-columns: 1fr 1fr;
		gap: 8px 16px;
		margin: 0 0 12px;
	}
	dl.epoch div {
		display: flex;
		flex-direction: column;
		gap: 1px;
	}
	dl.epoch dt {
		font-size: 0.6rem;
		text-transform: uppercase;
		letter-spacing: 0.06em;
		color: var(--muted);
		font-weight: 700;
	}
	dl.epoch dd {
		margin: 0;
		font-size: 0.82rem;
	}
	.gate {
		font-size: 0.8rem;
		font-style: italic;
	}
	h3 {
		font-size: 0.82rem;
		text-transform: uppercase;
		letter-spacing: 0.05em;
		color: var(--muted);
		margin: 18px 0 8px;
	}
</style>
