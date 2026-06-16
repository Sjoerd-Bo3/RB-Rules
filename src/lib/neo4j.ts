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

/**
 * Bouw de kaart-graph uit de card-tabel: Card-knopen met FROM_SET / HAS_DOMAIN /
 * HAS_KEYWORD relaties. Basis voor echte GraphRAG (kaart↔regel-interacties).
 */
export async function syncCardGraph(): Promise<{ cards: number }> {
  const { rows } = await pool.query<{
    riftbound_id: string;
    name: string;
    type: string | null;
    rarity: string | null;
    energy: number | null;
    might: number | null;
    set_id: string | null;
    set_label: string | null;
    domains: string[];
    tags: string[];
  }>(
    `SELECT riftbound_id, name, type, rarity, energy, might, set_id, set_label, domains, tags
       FROM card`,
  );

  const session = driver().session();
  try {
    for (const c of rows) {
      await session.run(
        `MERGE (card:Card {id: $id})
           SET card.name = $name, card.type = $type, card.rarity = $rarity,
               card.energy = $energy, card.might = $might`,
        {
          id: c.riftbound_id,
          name: c.name,
          type: c.type,
          rarity: c.rarity,
          energy: c.energy,
          might: c.might,
        },
      );
      if (c.set_id) {
        await session.run(
          `MATCH (card:Card {id: $id})
           MERGE (s:Set {id: $set}) ON CREATE SET s.label = $label
           MERGE (card)-[:FROM_SET]->(s)`,
          { id: c.riftbound_id, set: c.set_id, label: c.set_label },
        );
      }
      if (c.domains?.length) {
        await session.run(
          `MATCH (card:Card {id: $id})
           UNWIND $domains AS d
           MERGE (dom:Domain {name: d}) MERGE (card)-[:HAS_DOMAIN]->(dom)`,
          { id: c.riftbound_id, domains: c.domains },
        );
      }
      if (c.tags?.length) {
        await session.run(
          `MATCH (card:Card {id: $id})
           UNWIND $tags AS t
           MERGE (k:Keyword {name: t}) MERGE (card)-[:HAS_KEYWORD]->(k)`,
          { id: c.riftbound_id, tags: c.tags },
        );
      }
    }
    return { cards: rows.length };
  } finally {
    await session.close();
  }
}
