import { pool } from "@/lib/db";
import { embed, toVectorLiteral } from "@/lib/embeddings";
import { loadSources } from "@/lib/source-store";

const SECTION_RE = /\b(\d{3}\.\d+[a-z]?(?:\.\d+)?)/;

/** Splits tekst in overlappende chunks van ~size tekens (zinsgrens-bewust). */
export function chunkText(text: string, size = 900, overlap = 120): string[] {
  const clean = text.replace(/\s+/g, " ").trim();
  if (clean.length <= size) return clean ? [clean] : [];
  const chunks: string[] = [];
  let i = 0;
  while (i < clean.length) {
    let end = Math.min(i + size, clean.length);
    if (end < clean.length) {
      const dot = clean.lastIndexOf(". ", end);
      if (dot > i + size * 0.5) end = dot + 1;
    }
    chunks.push(clean.slice(i, end).trim());
    if (end >= clean.length) break;
    i = end - overlap;
  }
  return chunks.filter(Boolean);
}

function sectionOf(chunk: string): string | null {
  return chunk.match(SECTION_RE)?.[1] ?? null;
}

/** Bouw de vector-index: chunk de nieuwste documenten en embed ze in pgvector. */
export async function buildIndex(): Promise<{ source: string; chunks: number }[]> {
  const sources = await loadSources(true);
  const out: { source: string; chunks: number }[] = [];

  for (const s of sources) {
    const doc = await pool.query<{ id: number; content: string }>(
      "SELECT id, content FROM document WHERE source_id = $1 ORDER BY retrieved_at DESC LIMIT 1",
      [s.id],
    );
    if (!doc.rows[0]) continue;

    const chunks = chunkText(doc.rows[0].content);
    await pool.query("DELETE FROM rule_chunk WHERE source_id = $1", [s.id]);

    for (let b = 0; b < chunks.length; b += 32) {
      const batch = chunks.slice(b, b + 32);
      const vecs = await embed(batch, "document");
      for (let k = 0; k < batch.length; k++) {
        await pool.query(
          `INSERT INTO rule_chunk (document_id, source_id, section_code, text, embedding)
           VALUES ($1, $2, $3, $4, $5::vector)`,
          [doc.rows[0].id, s.id, sectionOf(batch[k]), batch[k], toVectorLiteral(vecs[k])],
        );
      }
    }
    out.push({ source: s.id, chunks: chunks.length });
  }
  return out;
}

/** Vind kaarten waarvan de naam in de vraag voorkomt; lever feiten als context. */
async function cardFacts(question: string): Promise<string[]> {
  const q = question.toLowerCase();
  const { rows } = await pool.query<{
    name: string;
    type: string | null;
    supertype: string | null;
    rarity: string | null;
    domains: string[];
    energy: number | null;
    might: number | null;
    set_label: string | null;
    text_plain: string | null;
  }>(
    `SELECT name, type, supertype, rarity, domains, energy, might, set_label, text_plain FROM card`,
  );
  return rows
    .filter((r) => r.name.length >= 3 && q.includes(r.name.toLowerCase()))
    .slice(0, 5)
    .map(
      (r) =>
        `${r.name} — ${[r.supertype, r.type].filter(Boolean).join(" ")} (${r.set_label ?? "?"}` +
        `${r.rarity ? `, ${r.rarity}` : ""}). Domains: ${r.domains.join(", ") || "—"}. ` +
        `Energy ${r.energy ?? "—"}, Might ${r.might ?? "—"}.` +
        (r.text_plain ? ` Tekst: ${r.text_plain.slice(0, 240)}` : ""),
    );
}

/** GraphRAG: herken kaarten in de vraag en haal hun graph-feiten op (als Neo4j er is). */
async function cardGraphBlock(question: string): Promise<string> {
  const q = question.toLowerCase();
  const { rows } = await pool.query<{ riftbound_id: string; name: string }>(
    `SELECT riftbound_id, name FROM card`,
  );
  const ids = rows
    .filter((r) => r.name.length >= 3 && q.includes(r.name.toLowerCase()))
    .slice(0, 3)
    .map((r) => r.riftbound_id);
  if (ids.length === 0) return "";

  const { graphPing, cardGraphContext } = await import("@/lib/neo4j");
  if (!(await graphPing())) return "";
  const facts = await cardGraphContext(ids);
  return facts.length
    ? `\n\nGraph-feiten (kaart-relaties, gezaghebbend):\n` +
        facts.map((f) => `- ${f}`).join("\n")
    : "";
}

export interface RagCitation {
  n: number;
  name: string;
  url: string;
  section: string | null;
  trust: number;
}

const RAG_SYSTEM = `Je bent een Riftbound TCG regels-assistent. Beantwoord de vraag
op basis van de meegegeven context-fragmenten. Citeer je bronnen met [n] verwijzend
naar de fragmentnummers. Als de context het antwoord niet bevat, zeg dat eerlijk.
Officiële bronnen (lagere trust-tier = betrouwbaarder) gaan vóór community-bronnen;
benoem tegenstrijdigheden expliciet. GEVERIFIEERDE RULINGS (indien meegegeven) zijn
gezaghebbend en gaan vóór alles — volg ze. Antwoord in het Nederlands.`;

/** Vector-RAG vraag→antwoord met citaten. */
export async function ask(
  question: string,
): Promise<{ answer: string; sources: RagCitation[] }> {
  const [qv] = await embed([question], "query");
  const lit = toVectorLiteral(qv);

  const { rows } = await pool.query<{
    text: string;
    section_code: string | null;
    name: string;
    url: string;
    trust_tier: number;
  }>(
    `SELECT rc.text, rc.section_code, s.name, s.url, s.trust_tier
       FROM rule_chunk rc JOIN source s ON s.id = rc.source_id
      WHERE rc.embedding IS NOT NULL
      ORDER BY rc.embedding <=> $1::vector ASC
      LIMIT 6`,
    [lit],
  );

  if (rows.length === 0) {
    return {
      answer:
        "Er is nog geen geïndexeerde regeltekst. Bouw eerst de index op via /admin → 'Index opbouwen'.",
      sources: [],
    };
  }

  const sources: RagCitation[] = rows.map((r, i) => ({
    n: i + 1,
    name: r.name,
    url: r.url,
    section: r.section_code,
    trust: r.trust_tier,
  }));

  const context = rows
    .map(
      (r, i) =>
        `[${i + 1}] (${r.name}, trust ${r.trust_tier}${r.section_code ? `, §${r.section_code}` : ""})\n${r.text}`,
    )
    .join("\n\n");

  const cards = await cardFacts(question);
  const cardBlock = cards.length
    ? `\n\nKaartgegevens (kaartdatabase, gezaghebbend voor stats/domains):\n` +
      cards.map((c) => `- ${c}`).join("\n")
    : "";

  // Self-learning: gezaghebbende geverifieerde correcties (override-laag).
  const { relevantCorrections } = await import("@/lib/corrections");
  const corr = await relevantCorrections(question).catch(() => [] as string[]);
  const corrBlock = corr.length
    ? `\n\nGEVERIFIEERDE RULINGS (gezaghebbend, gaan vóór alles):\n` +
      corr.map((c) => `- ${c}`).join("\n")
    : "";

  // GraphRAG: relateer herkende kaarten via Neo4j (domains/keywords→regels/ban).
  const graphBlock = await cardGraphBlock(question).catch(() => "");

  const { askClaude } = await import("@/lib/ai");
  const answer = await askClaude({
    task: "cheap",
    system: RAG_SYSTEM,
    prompt: `Context-fragmenten:\n${context}${cardBlock}${graphBlock}${corrBlock}\n\nVraag: ${question}`,
  });

  return { answer, sources };
}
