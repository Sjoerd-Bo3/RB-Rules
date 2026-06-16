import { pool } from "@/lib/db";
import { embed, toVectorLiteral } from "@/lib/embeddings";

export interface CorrectionRow {
  id: number;
  scope: string;
  ref: string;
  text: string;
  question: string | null;
  provenance: string | null;
  status: string;
  created_at: string;
}

/** Speler/judge dient een correctie in (start altijd als 'unverified'). */
export async function submitCorrection(input: {
  scope: string; // 'answer' | 'rule_section' | 'card'
  ref: string; // bv. de vraag of section-code / kaart-id
  text: string;
  question?: string;
  provenance?: string;
}): Promise<number> {
  const { rows } = await pool.query<{ id: number }>(
    `INSERT INTO correction (scope, ref, text, question, provenance, status)
     VALUES ($1, $2, $3, $4, $5, 'unverified') RETURNING id`,
    [input.scope, input.ref, input.text, input.question ?? null, input.provenance ?? null],
  );
  return rows[0].id;
}

export async function listCorrections(status?: string): Promise<CorrectionRow[]> {
  const { rows } = await pool.query<CorrectionRow>(
    `SELECT id, scope, ref, text, question, provenance, status, created_at
       FROM correction ${status ? "WHERE status = $1" : ""}
      ORDER BY created_at DESC LIMIT 200`,
    status ? [status] : [],
  );
  return rows;
}

/** Verifiëren: status→verified én de correctie embedden voor terugkoppeling. */
export async function verifyCorrection(id: number): Promise<void> {
  const { rows } = await pool.query<{ text: string; question: string | null }>(
    "SELECT text, question FROM correction WHERE id = $1",
    [id],
  );
  if (!rows[0]) return;
  const basis = [rows[0].question, rows[0].text].filter(Boolean).join(" — ");
  let embeddingLit: string | null = null;
  try {
    const [v] = await embed([basis], "document");
    embeddingLit = toVectorLiteral(v);
  } catch {
    embeddingLit = null; // verifieer toch; zonder embedding valt 'ie terug op tekstmatch
  }
  await pool.query(
    `UPDATE correction
        SET status = 'verified', verified_at = now(),
            embedding = ${embeddingLit ? "$2::vector" : "embedding"}
      WHERE id = $1`,
    embeddingLit ? [id, embeddingLit] : [id],
  );
}

export async function deleteCorrection(id: number): Promise<void> {
  await pool.query("DELETE FROM correction WHERE id = $1", [id]);
}

/** Geverifieerde correcties die op de vraag lijken — gezaghebbende override-laag. */
export async function relevantCorrections(question: string): Promise<string[]> {
  // Probeer vector-match (als embeddings beschikbaar zijn); val terug op recent.
  try {
    const [qv] = await embed([question], "query");
    const { rows } = await pool.query<{ text: string }>(
      `SELECT text FROM correction
        WHERE status = 'verified' AND embedding IS NOT NULL
        ORDER BY embedding <=> $1::vector ASC LIMIT 3`,
      [toVectorLiteral(qv)],
    );
    if (rows.length) return rows.map((r) => r.text);
  } catch {
    /* val door naar tekstmatch */
  }
  const { rows } = await pool.query<{ text: string }>(
    `SELECT text FROM correction WHERE status = 'verified' ORDER BY created_at DESC LIMIT 3`,
  );
  return rows.map((r) => r.text);
}
