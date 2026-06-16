import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";

export const maxDuration = 60;

export async function POST(req: NextRequest) {
  const { question } = (await req.json().catch(() => ({}))) as { question?: string };
  if (!question || !question.trim()) {
    return NextResponse.json({ error: "Geen vraag opgegeven" }, { status: 400 });
  }
  try {
    const { ask } = await import("@/lib/rag");
    return NextResponse.json(await ask(question.trim()));
  } catch (e) {
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
