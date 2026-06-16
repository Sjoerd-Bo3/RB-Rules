import neo4j, { type Driver } from "neo4j-driver";
import { pool } from "@/lib/db";

const g = globalThis as unknown as { __neo4j?: Driver };

export function driver(): Driver {
  if (!g.__neo4j) {
    g.__neo4j = neo4j.driver(
      process.env.NEO4J_URI ?? "bolt://localhost:7687",
      neo4j.auth.basic(
        process.env.NEO4J_USER ?? "neo4j",
        process.env.NEO4J_PASSWORD ?? "neo4j",
      ),
    );
  }
  return g.__neo4j;
}

export async function graphPing(): Promise<boolean> {
  const s = driver().session();
  try {
    await s.run("RETURN 1");
    return true;
  } catch {
    return false;
  } finally {
    await s.close();
  }
}

/**
 * Minimale graph-sync (Fase 2 startpunt): maakt RuleSection-knopen uit de
 * geïndexeerde chunks. De volledige kaart↔regel↔errata↔ban-graph (uit de
 * card-database) volgt; dit zet de wiring + admin-trigger neer.
 */
export async function graphSync(): Promise<{ ruleSections: number }> {
  const { rows } = await pool.query<{ section_code: string }>(
    "SELECT DISTINCT section_code FROM rule_chunk WHERE section_code IS NOT NULL",
  );
  const session = driver().session();
  try {
    for (const r of rows) {
      await session.run("MERGE (:RuleSection {code: $code})", { code: r.section_code });
    }
    return { ruleSections: rows.length };
  } finally {
    await session.close();
  }
}
