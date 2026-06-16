import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { ingestSource } from "@/ingest/runner";
import { loadSource, loadSources } from "@/lib/source-store";

// Handmatige scan vanuit /admin. Optioneel { sourceId } voor één bron.
export const maxDuration = 120;

export async function POST(req: NextRequest) {
  const { sourceId } = (await req.json().catch(() => ({}))) as { sourceId?: string };

  const sources = sourceId
    ? [await loadSource(sourceId)].filter(Boolean)
    : await loadSources(true);

  const results = [];
  for (const src of sources) {
    if (src) results.push(await ingestSource(src));
  }
  return NextResponse.json({ results });
}
