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

export interface RagCitation {
  n: number;
  name: string;
  url: string;
  section: string | null;
  trust: number;
}

const RAG_SYSTEM = `Je bent een Riftbound TCG regels-assistent. Beantwoord de vraag
UITSLUITEND op basis van de meegegeven context-fragmenten. Citeer je bronnen met
[n] verwijzend naar de fragmentnummers. Als de context het antwoord niet bevat,
zeg dat eerlijk. Officiële bronnen (lagere trust-tier = betrouwbaarder) gaan vóór
community-bronnen; benoem tegenstrijdigheden expliciet. Antwoord in het Nederlands.`;

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

  const { askClaude } = await import("@/lib/ai");
  const answer = await askClaude({
    task: "cheap",
    system: RAG_SYSTEM,
    prompt: `Context-fragmenten:\n${context}\n\nVraag: ${question}`,
  });

  return { answer, sources };
}
