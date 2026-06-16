// Draai één scan-run over alle ingeschakelde bronnen.
// Gebruik: npm run ingest   (cron'baar, bv. dagelijks op de Mac mini)
import "dotenv/config";
import { pool } from "@/lib/db";
import { ingestSource } from "@/ingest/runner";
import { SOURCES } from "../config/sources";

async function main() {
  const enabled = SOURCES.filter((s) => s.enabled);
  console.log(`Scan van ${enabled.length} bronnen…`);

  for (const src of enabled) {
    const r = await ingestSource(src);
    const mark =
      r.status === "changed" || r.status === "new" ? "●" : r.status === "error" ? "✗" : "·";
    console.log(`${mark} ${src.id}: ${r.status}${r.detail ? ` (${r.detail})` : ""}`);
  }

  await pool.end();
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
