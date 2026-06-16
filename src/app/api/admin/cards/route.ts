import { NextResponse } from "next/server";
import { logRun } from "@/lib/runlog";

export const maxDuration = 300;

// Synchroniseert de kaart-database (Riftcodex → Riot-fallback), idempotent.
export async function POST() {
  try {
    const { syncCards } = await import("@/ingest/cards");
    const r = await syncCards();
    await logRun("cards", r.source, "ok", `${r.sets} sets, ${r.cards} kaarten`);
    return NextResponse.json(r);
  } catch (e) {
    await logRun("cards", null, "error", e);
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
