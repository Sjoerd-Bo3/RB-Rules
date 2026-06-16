import { NextResponse } from "next/server";
import { pool } from "@/lib/db";

export const maxDuration = 300;

export async function GET() {
  const { rows } = await pool.query(
    `SELECT cf.id, cf.topic, cf.kind, cf.status, cf.detected_at,
            a.name AS source_a, b.name AS source_b
       FROM conflict cf
       LEFT JOIN source a ON a.id = cf.source_a_id
       LEFT JOIN source b ON b.id = cf.source_b_id
      ORDER BY cf.detected_at DESC`,
  );
  return NextResponse.json(rows);
}

export async function POST() {
  try {
    const { detectConflicts } = await import("@/ingest/conflicts");
    return NextResponse.json(await detectConflicts());
  } catch (e) {
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
