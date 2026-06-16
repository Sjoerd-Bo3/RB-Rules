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

/**
 * Bouw de "intelligente" edges die GraphRAG mogelijk maken:
 *  - Keyword -[:DEFINED_BY]-> RuleSection  (keyword genoemd in regeltekst)
 *  - Card    -[:BANNED_IN]->  BanEntry     (kaartnaam in een ban-context, officieel)
 * Heuristisch maar bron-gebaseerd; draait na graphSync/syncCardGraph.
 */
export async function syncGraphLinks(): Promise<{ defined: number; banned: number }> {
  // 1. Keyword → RuleSection
  const kw = await pool.query<{ name: string }>(
    `SELECT DISTINCT unnest(tags) AS name FROM card`,
  );
  const chunks = await pool.query<{ section_code: string; text: string }>(
    `SELECT section_code, lower(text) AS text FROM rule_chunk WHERE section_code IS NOT NULL`,
  );

  const session = driver().session();
  let defined = 0;
  let banned = 0;
  try {
    for (const k of kw.rows) {
      const needle = k.name.toLowerCase();
      if (needle.length < 3) continue;
      const sections = new Set<string>();
      for (const c of chunks.rows) {
        if (c.text.includes(needle)) sections.add(c.section_code);
      }
      if (sections.size === 0) continue;
      await session.run(
        `MATCH (k:Keyword {name: $name})
         UNWIND $sections AS code
         MERGE (rs:RuleSection {code: code})
         MERGE (k)-[:DEFINED_BY]->(rs)`,
        { name: k.name, sections: [...sections].slice(0, 25) },
      );
      defined += Math.min(sections.size, 25);
    }

    // 2. Card → BanEntry (officiële docs, zinnen met 'ban' + kaartnaam)
    const docs = await pool.query<{ content: string }>(
      `SELECT d.content FROM document d JOIN source s ON s.id = d.source_id
        WHERE s.trust_tier = 1
        ORDER BY d.retrieved_at DESC LIMIT 10`,
    );
    const cards = await pool.query<{ riftbound_id: string; name: string }>(
      `SELECT riftbound_id, name FROM card`,
    );
    const bannedIds = new Set<string>();
    for (const d of docs.rows) {
      const sentences = d.content.split(/(?<=\.)\s+/);
      for (const sent of sentences) {
        if (!/ban/i.test(sent)) continue;
        const low = sent.toLowerCase();
        for (const c of cards.rows) {
          if (c.name.length >= 4 && low.includes(c.name.toLowerCase())) {
            bannedIds.add(c.riftbound_id);
          }
        }
      }
    }
    if (bannedIds.size) {
      await session.run(`MERGE (:BanEntry {format: 'constructed'})`);
      for (const id of bannedIds) {
        await session.run(
          `MATCH (card:Card {id: $id}), (b:BanEntry {format: 'constructed'})
           MERGE (card)-[:BANNED_IN]->(b)`,
          { id },
        );
        banned++;
      }
    }
    return { defined, banned };
  } finally {
    await session.close();
  }
}

/** Graph-traversal: feiten over de gegeven kaarten (voor GraphRAG-context). */
export async function cardGraphContext(ids: string[]): Promise<string[]> {
  if (ids.length === 0) return [];
  const session = driver().session();
  try {
    const out: string[] = [];
    for (const id of ids.slice(0, 3)) {
      const r = await session.run(
        `MATCH (c:Card {id: $id})
         OPTIONAL MATCH (c)-[:HAS_DOMAIN]->(dom:Domain)
         OPTIONAL MATCH (c)-[:HAS_KEYWORD]->(k:Keyword)
         OPTIONAL MATCH (k)-[:DEFINED_BY]->(rs:RuleSection)
         OPTIONAL MATCH (c)-[:BANNED_IN]->(b:BanEntry)
         RETURN c.name AS name,
                collect(DISTINCT dom.name) AS domains,
                collect(DISTINCT k.name) AS keywords,
                collect(DISTINCT rs.code) AS sections,
                count(b) > 0 AS banned`,
        { id },
      );
      const rec = r.records[0];
      if (!rec) continue;
      const name = rec.get("name") as string;
      const domains = (rec.get("domains") as string[]).filter(Boolean);
      const keywords = (rec.get("keywords") as string[]).filter(Boolean);
      const sections = (rec.get("sections") as string[]).filter(Boolean);
      const bannedVal = rec.get("banned");
      const banned = typeof bannedVal === "boolean" ? bannedVal : Boolean(bannedVal);
      out.push(
        `${name}: domains ${domains.join(", ") || "—"}; keywords ${keywords.join(", ") || "—"}` +
          (sections.length ? `; relevante regelsecties ${sections.join(", ")}` : "") +
          (banned ? "; ⚠ STAAT OP DE BANLIJST (constructed)" : ""),
      );
    }
    return out;
  } finally {
    await session.close();
  }
}
