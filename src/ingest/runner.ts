import { pool } from "@/lib/db";
import { browserFetch } from "@/lib/fetch";
import { htmlToText, lineDiff, sha256 } from "@/lib/text";
import type { SourceDef } from "../../config/sources";

export interface IngestResult {
  sourceId: string;
  status: "unchanged" | "changed" | "new" | "error";
  detail?: string;
}

/**
 * Eén scan-run voor één bron:
 *   fetch → extract → hash → vergelijk met vorige → bij wijziging: change opslaan.
 * De hash-check zorgt dat ongewijzigde bronnen ~gratis zijn (geen LLM/embedding).
 */
export async function ingestSource(src: SourceDef): Promise<IngestResult> {
  try {
    const res = await browserFetch(src.url);
    if (!res.ok) {
      await touch(src.id);
      return { sourceId: src.id, status: "error", detail: `HTTP ${res.status}` };
    }

    const raw = await res.text();
    // MVP: alleen html-parser actief; pdf/json_api volgen in Fase 2.
    const text = src.parser === "html" ? htmlToText(raw) : raw;
    const hash = sha256(text);

    const prev = await pool.query<{ last_hash: string | null }>(
      "SELECT last_hash FROM source WHERE id = $1",
      [src.id],
    );
    const lastHash = prev.rows[0]?.last_hash ?? null;

    if (lastHash === hash) {
      await touch(src.id);
      return { sourceId: src.id, status: "unchanged" };
    }

    // Wijziging (of eerste keer): document opslaan + change loggen.
    const prevDoc = await pool.query<{ content: string }>(
      "SELECT content FROM document WHERE source_id = $1 ORDER BY retrieved_at DESC LIMIT 1",
      [src.id],
    );
    const oldText = prevDoc.rows[0]?.content ?? "";

    await pool.query(
      "INSERT INTO document (source_id, content, content_hash) VALUES ($1, $2, $3)",
      [src.id, text, hash],
    );

    const isNew = lastHash === null;
    if (!isNew) {
      const diff = lineDiff(oldText, text);

      // AI-classificatie (rijke uitleg + type/ernst). Lazy import zodat de
      // Agent SDK alleen geladen wordt als er auth is geconfigureerd.
      let cls = null as Awaited<ReturnType<typeof import("./classify").classifyChange>> | null;
      if (process.env.CLAUDE_CODE_OAUTH_TOKEN || process.env.ANTHROPIC_API_KEY) {
        try {
          const { classifyChange } = await import("./classify");
          cls = await classifyChange(src.name, diff);
        } catch {
          cls = null;
        }
      }

      await pool.query(
        `INSERT INTO change (source_id, change_type, severity, summary, meaning, diff)
         VALUES ($1, $2, $3, $4, $5, $6)`,
        [
          src.id,
          cls?.change_type ?? "unknown",
          cls?.severity ?? "medium",
          cls?.summary ?? null,
          cls?.meaning ?? null,
          diff,
        ],
      );
    }

    await pool.query(
      "UPDATE source SET last_hash = $1, last_checked = now() WHERE id = $2",
      [hash, src.id],
    );

    return { sourceId: src.id, status: isNew ? "new" : "changed" };
  } catch (err) {
    await touch(src.id);
    return { sourceId: src.id, status: "error", detail: String(err) };
  }
}

async function touch(id: string): Promise<void> {
  await pool.query("UPDATE source SET last_checked = now() WHERE id = $1", [id]);
}
