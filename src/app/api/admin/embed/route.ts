import { NextResponse } from "next/server";
import { logRun } from "@/lib/runlog";

export const maxDuration = 300;

export async function POST() {
  try {
    const { buildIndex } = await import("@/lib/rag");
    const results = await buildIndex();
    const total = results.reduce((n, r) => n + r.chunks, 0);
    await logRun("embed", null, "ok", `${results.length} bronnen, ${total} chunks`);
    return NextResponse.json({ results });
  } catch (e) {
    await logRun("embed", null, "error", e);
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
