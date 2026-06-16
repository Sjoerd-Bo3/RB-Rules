import { pool } from "@/lib/db";

export const dynamic = "force-dynamic";

interface SourceRow {
  id: string;
  name: string;
  url: string;
  type: string;
  trust_tier: number;
  rank: number;
  cadence: string;
  enabled: boolean;
  last_checked: string | null;
}

async function getSources(): Promise<SourceRow[]> {
  try {
    const { rows } = await pool.query<SourceRow>(
      `SELECT id, name, url, type, trust_tier, rank, cadence, enabled, last_checked
         FROM source ORDER BY trust_tier ASC, rank DESC`,
    );
    return rows;
  } catch {
    return [];
  }
}

export default async function SourcesPage() {
  const sources = await getSources();

  return (
    <>
      <h1>Bronnen</h1>
      <p className="subtitle">
        Gevolgde regelbronnen met hun trust-tier en rang. Officieel verslaat community.
      </p>

      {sources.length === 0 ? (
        <p className="empty">
          Nog geen bronnen. Draai <code>npm run db:init</code>.
        </p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Bron</th>
              <th>Type</th>
              <th>Trust</th>
              <th>Rang</th>
              <th>Cadans</th>
              <th>Laatst gescand</th>
            </tr>
          </thead>
          <tbody>
            {sources.map((s) => (
              <tr key={s.id}>
                <td>
                  <a href={s.url} target="_blank" rel="noreferrer">
                    {s.name}
                  </a>
                </td>
                <td>{s.type}</td>
                <td>{s.trust_tier}</td>
                <td>{s.rank}</td>
                <td>{s.cadence}</td>
                <td className="meta">
                  {s.last_checked
                    ? new Date(s.last_checked).toLocaleString("nl-NL")
                    : "—"}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
