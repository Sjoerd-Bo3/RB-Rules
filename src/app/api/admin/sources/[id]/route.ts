import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { pool } from "@/lib/db";

const EDITABLE = ["name", "url", "type", "trust_tier", "rank", "parser", "cadence", "enabled"];

export async function PATCH(
  req: NextRequest,
  { params }: { params: Promise<{ id: string }> },
) {
  const { id } = await params;
  const b = (await req.json().catch(() => ({}))) as Record<string, unknown>;

  const sets: string[] = [];
  const vals: unknown[] = [];
  for (const key of EDITABLE) {
    if (key in b) {
      sets.push(`${key} = $${sets.length + 1}`);
      vals.push(b[key]);
    }
  }
  if (sets.length === 0) {
    return NextResponse.json({ error: "niets te updaten" }, { status: 400 });
  }
  vals.push(id);
  await pool.query(`UPDATE source SET ${sets.join(", ")} WHERE id = $${vals.length}`, vals);
  return NextResponse.json({ ok: true });
}

export async function DELETE(
  _req: NextRequest,
  { params }: { params: Promise<{ id: string }> },
) {
  const { id } = await params;
  // Eerst afhankelijke rijen los: changes/documents verwijzen via FK.
  await pool.query("DELETE FROM change WHERE source_id = $1", [id]);
  await pool.query("DELETE FROM document WHERE source_id = $1", [id]);
  await pool.query("DELETE FROM source WHERE id = $1", [id]);
  return NextResponse.json({ ok: true });
}
