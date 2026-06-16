import { pool } from "@/lib/db";
import NotifyButton from "@/components/NotifyButton";

export const dynamic = "force-dynamic";

interface ChangeRow {
  id: number;
  source_name: string;
  trust_tier: number;
  change_type: string;
  severity: string;
  summary: string | null;
  meaning: string | null;
  diff: string | null;
  detected_at: string;
}

async function getChanges(): Promise<ChangeRow[]> {
  try {
    const { rows } = await pool.query<ChangeRow>(
      `SELECT c.id, s.name AS source_name, s.trust_tier, c.change_type,
              c.severity, c.summary, c.meaning, c.diff, c.detected_at
         FROM change c JOIN source s ON s.id = c.source_id
        ORDER BY c.detected_at DESC
        LIMIT 50`,
    );
    return rows;
  } catch {
    return [];
  }
}

export default async function ChangesPage() {
  const changes = await getChanges();

  return (
    <>
      <div className="admin-head">
        <h1>Wat is er veranderd</h1>
        <NotifyButton />
      </div>
      <p className="subtitle">
        Automatisch gedetecteerde wijzigingen in de gevolgde regelbronnen, nieuwste eerst.
      </p>

      {changes.length === 0 ? (
        <p className="empty">
          Nog geen wijzigingen geregistreerd. Draai <code>npm run db:init</code> en{" "}
          <code>npm run ingest</code> om de eerste scan uit te voeren.
        </p>
      ) : (
        changes.map((c) => (
          <article className="card" key={c.id}>
            <div className="card-head">
              <span className={`badge sev-${c.severity}`}>{c.severity}</span>
              <strong>{c.change_type}</strong>
              <span className="meta">
                · {c.source_name} (trust {c.trust_tier})
              </span>
            </div>
            <div className="meta">{new Date(c.detected_at).toLocaleString("nl-NL")}</div>
            {c.summary && <p>{c.summary}</p>}
            {c.meaning && (
              <p className="meaning">
                <strong>Wat betekent dit:</strong> {c.meaning}
              </p>
            )}
            {c.diff && <pre className="diff">{c.diff}</pre>}
          </article>
        ))
      )}
    </>
  );
}
