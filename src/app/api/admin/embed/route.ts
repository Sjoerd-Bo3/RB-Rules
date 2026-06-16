import { NextResponse } from "next/server";

export const maxDuration = 300;

export async function POST() {
  try {
    const { buildIndex } = await import("@/lib/rag");
    return NextResponse.json({ results: await buildIndex() });
  } catch (e) {
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
