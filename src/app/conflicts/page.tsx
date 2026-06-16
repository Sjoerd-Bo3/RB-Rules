import { pool } from "@/lib/db";

export const dynamic = "force-dynamic";

interface ConflictRow {
  id: number;
  topic: string;
  kind: string;
  status: string;
  detected_at: string;
  source_a: string | null;
  source_b: string | null;
}

async function getConflicts(): Promise<ConflictRow[]> {
  try {
    const { rows } = await pool.query<ConflictRow>(
      `SELECT cf.id, cf.topic, cf.kind, cf.status, cf.detected_at,
              a.name AS source_a, b.name AS source_b
         FROM conflict cf
         LEFT JOIN source a ON a.id = cf.source_a_id
         LEFT JOIN source b ON b.id = cf.source_b_id
        ORDER BY cf.detected_at DESC LIMIT 100`,
    );
    return rows;
  } catch {
    return [];
  }
}

export default async function ConflictsPage() {
  const conflicts = await getConflicts();

  return (
    <>
      <h1>Tegenstrijdigheden</h1>
      <p className="subtitle">
        Plekken waar community-bronnen de officiële regels tegenspreken of achterlopen.
        Officieel wint altijd.
      </p>

      {conflicts.length === 0 ? (
        <p className="empty">
          Geen open tegenstrijdigheden. Draai een check via /admin → &quot;Conflicten checken&quot;.
        </p>
      ) : (
        conflicts.map((c) => (
          <article className="card" key={c.id}>
            <div className="card-head">
              <span className={`badge ${c.kind === "contradiction" ? "sev-high" : "sev-medium"}`}>
                {c.kind === "contradiction" ? "tegenstrijdig" : "loopt achter"}
              </span>
              <strong>{c.topic}</strong>
            </div>
            <p className="meta">
              {c.source_b ?? "?"} ↔ officieel ({c.source_a ?? "?"}) ·{" "}
              {new Date(c.detected_at).toLocaleString("nl-NL")} · {c.status}
            </p>
          </article>
        ))
      )}
    </>
  );
}
