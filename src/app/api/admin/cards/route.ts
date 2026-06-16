import { NextResponse } from "next/server";

export const maxDuration = 300;

// Synchroniseert de kaart-database (Riftcodex) — sets + kaarten, idempotent.
export async function POST() {
  try {
    const { syncCards } = await import("@/ingest/cards");
    return NextResponse.json(await syncCards());
  } catch (e) {
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
