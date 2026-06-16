import { pool } from "@/lib/db";
import type { SourceDef } from "../../config/sources";

// Bronnen leven in de DB (beheerbaar via /admin). config/sources.ts is alleen de
// seed bij db:init. Deze helper laadt de actuele bronnen voor scans.
export async function loadSources(onlyEnabled = false): Promise<SourceDef[]> {
  const { rows } = await pool.query(
    `SELECT id, name, url, type, trust_tier, rank, parser, cadence, enabled
       FROM source ${onlyEnabled ? "WHERE enabled" : ""}
      ORDER BY trust_tier ASC, rank DESC`,
  );
  return rows as SourceDef[];
}

export async function loadSource(id: string): Promise<SourceDef | null> {
  const { rows } = await pool.query(
    `SELECT id, name, url, type, trust_tier, rank, parser, cadence, enabled
       FROM source WHERE id = $1`,
    [id],
  );
  return (rows[0] as SourceDef) ?? null;
}
