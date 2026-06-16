// Initialiseer Postgres: schema toepassen + bronnen-register vullen/syncen.
// Gebruik: npm run db:init   (vereist draaiende Postgres, bv. via docker compose)
import "dotenv/config";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { pool } from "@/lib/db";
import { SOURCES } from "../config/sources";

async function main() {
  const schema = readFileSync(join(process.cwd(), "db", "schema.sql"), "utf8");
  await pool.query(schema);
  console.log("✓ schema toegepast");

  for (const s of SOURCES) {
    await pool.query(
      `INSERT INTO source (id, name, url, type, trust_tier, rank, parser, cadence, enabled)
       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
       ON CONFLICT (id) DO UPDATE SET
         name = EXCLUDED.name, url = EXCLUDED.url, type = EXCLUDED.type,
         trust_tier = EXCLUDED.trust_tier, rank = EXCLUDED.rank,
         parser = EXCLUDED.parser, cadence = EXCLUDED.cadence, enabled = EXCLUDED.enabled`,
      [s.id, s.name, s.url, s.type, s.trust_tier, s.rank, s.parser, s.cadence, s.enabled],
    );
  }
  console.log(`✓ ${SOURCES.length} bronnen gesynct`);
  await pool.end();
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
