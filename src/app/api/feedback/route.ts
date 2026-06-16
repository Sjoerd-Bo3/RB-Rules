import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { submitCorrection } from "@/lib/corrections";

// Publiek: een speler corrigeert een antwoord. Komt binnen als 'unverified' en
// wordt pas gezaghebbend na verificatie via /admin.
export async function POST(req: NextRequest) {
  const b = (await req.json().catch(() => ({}))) as {
    question?: string;
    text?: string;
  };
  if (!b.text || !b.text.trim()) {
    return NextResponse.json({ error: "Geen correctie opgegeven" }, { status: 400 });
  }
  const id = await submitCorrection({
    scope: "answer",
    ref: (b.question ?? "").slice(0, 300),
    text: b.text.trim(),
    question: b.question?.slice(0, 300),
    provenance: "speler via /ask",
  });
  return NextResponse.json({ ok: true, id });
}
