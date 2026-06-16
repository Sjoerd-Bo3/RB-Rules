import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { clearLogs, listLogs } from "@/lib/runlog";

export async function GET(req: NextRequest) {
  const kind = req.nextUrl.searchParams.get("kind") ?? undefined;
  return NextResponse.json(await listLogs(kind || undefined));
}

export async function DELETE() {
  await clearLogs();
  return NextResponse.json({ ok: true });
}
