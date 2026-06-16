import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { pool } from "@/lib/db";
import { loadSources } from "@/lib/source-store";

export async function GET() {
  return NextResponse.json(await loadSources());
}

export async function POST(req: NextRequest) {
  const b = (await req.json().catch(() => ({}))) as Record<string, unknown>;
  const id = String(b.id ?? "").trim();
  const name = String(b.name ?? "").trim();
  const url = String(b.url ?? "").trim();
  if (!id || !name || !url) {
    return NextResponse.json({ error: "id, name en url zijn verplicht" }, { status: 400 });
  }

  try {
    await pool.query(
      `INSERT INTO source (id, name, url, type, trust_tier, rank, parser, cadence, enabled)
       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)`,
      [
        id,
        name,
        url,
        b.type === "official" ? "official" : "community",
        Number(b.trust_tier ?? 3),
        Number(b.rank ?? 0),
        ["html", "pdf", "json_api"].includes(String(b.parser)) ? b.parser : "html",
        b.cadence === "weekly" ? "weekly" : "daily",
        b.enabled !== false,
      ],
    );
    return NextResponse.json({ ok: true }, { status: 201 });
  } catch (e) {
    return NextResponse.json({ error: String(e) }, { status: 400 });
  }
}
