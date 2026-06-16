import { pool } from "@/lib/db";

export interface LogRow {
  id: number;
  kind: string;
  ref: string | null;
  status: string;
  detail: string | null;
  created_at: string;
}

/** Schrijf een regel naar het run-log. Faalt nooit hard (best-effort). */
export async function logRun(
  kind: string,
  ref: string | null,
  status: string,
  detail?: unknown,
): Promise<void> {
  try {
    await pool.query(
      `INSERT INTO run_log (kind, ref, status, detail) VALUES ($1, $2, $3, $4)`,
      [kind, ref, status, detail != null ? String(detail).slice(0, 2000) : null],
    );
  } catch {
    /* logging mag de operatie nooit breken */
  }
}

export async function listLogs(kind?: string): Promise<LogRow[]> {
  const { rows } = await pool.query<LogRow>(
    `SELECT id, kind, ref, status, detail, created_at
       FROM run_log ${kind ? "WHERE kind = $1" : ""}
      ORDER BY created_at DESC LIMIT 200`,
    kind ? [kind] : [],
  );
  return rows;
}

export async function clearLogs(): Promise<void> {
  await pool.query("DELETE FROM run_log");
}
