import { pool } from "@/lib/db";
import { loadSources } from "@/lib/source-store";

const SYSTEM = `Je vergelijkt Riftbound-regelinhoud van een COMMUNITY-bron met de
OFFICIËLE inhoud. Zoek plekken waar de community-bron de officiële regels
tegenspreekt (contradiction) of verouderd is/achterloopt (stale). Antwoord
UITSLUITEND met een JSON-array; elk item: {"topic": "...", "kind": "stale"|"contradiction", "explanation": "..."}.
Geen verschil? Antwoord []. Geen tekst buiten de JSON.`;

interface ConflictItem {
  topic: string;
  kind: "stale" | "contradiction";
  explanation: string;
}

function parseArray(raw: string): ConflictItem[] {
  const start = raw.indexOf("[");
  const end = raw.lastIndexOf("]");
  if (start === -1 || end === -1) return [];
  try {
    const arr = JSON.parse(raw.slice(start, end + 1)) as ConflictItem[];
    return Array.isArray(arr) ? arr : [];
  } catch {
    return [];
  }
}

async function latestText(sourceId: string): Promise<string> {
  const r = await pool.query<{ content: string }>(
    "SELECT content FROM document WHERE source_id = $1 ORDER BY retrieved_at DESC LIMIT 1",
    [sourceId],
  );
  return r.rows[0]?.content ?? "";
}

/**
 * Detecteer tegenstrijdigheden: officieel vs. community. Regenereert de
 * 'open' conflicten. Vereist AI-auth; zonder AI wordt overgeslagen.
 */
export async function detectConflicts(): Promise<{ created: number; note?: string }> {
  if (!(process.env.CLAUDE_CODE_OAUTH_TOKEN || process.env.ANTHROPIC_API_KEY)) {
    return { created: 0, note: "geen AI-auth geconfigureerd" };
  }

  const sources = await loadSources(false);
  const official = sources.filter((s) => s.trust_tier === 1);
  const community = sources.filter((s) => s.trust_tier >= 3);
  if (official.length === 0) return { created: 0, note: "geen officiële bron" };

  const officialText = (
    await Promise.all(
      official.map(async (o) => `[${o.name}]\n${(await latestText(o.id)).slice(0, 6000)}`),
    )
  ).join("\n\n");
  if (!officialText.trim()) return { created: 0, note: "geen officiële content" };

  const { askClaude } = await import("@/lib/ai");
  await pool.query("DELETE FROM conflict WHERE status = 'open'");

  let created = 0;
  for (const c of community) {
    const cText = (await latestText(c.id)).slice(0, 6000);
    if (!cText.trim()) continue;

    const raw = await askClaude({
      task: "cheap",
      system: SYSTEM,
      prompt: `OFFICIEEL:\n${officialText}\n\nCOMMUNITY (${c.name}):\n${cText}`,
    });

    for (const it of parseArray(raw)) {
      await pool.query(
        `INSERT INTO conflict (topic, source_a_id, source_b_id, kind, winner_source_id, status)
         VALUES ($1, $2, $3, $4, $5, 'open')`,
        [
          String(it.topic ?? "onbekend").slice(0, 200),
          official[0].id,
          c.id,
          it.kind === "contradiction" ? "contradiction" : "stale",
          official[0].id, // officieel wint (hoogste trust)
        ],
      );
      created++;
    }
  }
  return { created };
}
