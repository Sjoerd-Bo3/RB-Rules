import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { deleteCorrection, listCorrections, verifyCorrection } from "@/lib/corrections";

export async function GET(req: NextRequest) {
  const status = req.nextUrl.searchParams.get("status") ?? undefined;
  return NextResponse.json(await listCorrections(status || undefined));
}

export async function POST(req: NextRequest) {
  const { id, action } = (await req.json().catch(() => ({}))) as {
    id?: number;
    action?: "verify" | "delete";
  };
  if (!id || !action) {
    return NextResponse.json({ error: "id en action vereist" }, { status: 400 });
  }
  try {
    if (action === "verify") await verifyCorrection(id);
    else if (action === "delete") await deleteCorrection(id);
    return NextResponse.json({ ok: true });
  } catch (e) {
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
