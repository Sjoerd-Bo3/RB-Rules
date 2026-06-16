// Synchroniseer de kaart-database (en bouw de card-graph als Neo4j bereikbaar is).
// Cron'baar voor nieuwe sets/updates: npm run sync:cards
import "dotenv/config";
import { pool } from "@/lib/db";
import { syncCards } from "@/ingest/cards";
import { driver, graphPing, syncCardGraph } from "@/lib/neo4j";

async function main() {
  console.log("Kaart-sync (Riftcodex)…");
  const r = await syncCards();
  console.log(`✓ ${r.sets} sets, ${r.cards} kaarten gesynct`);

  if (await graphPing()) {
    const g = await syncCardGraph();
    console.log(`✓ graph: ${g.cards} Card-knopen`);
    await driver().close();
  } else {
    console.log("· Neo4j niet bereikbaar — graph overgeslagen");
  }

  await pool.end();
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
