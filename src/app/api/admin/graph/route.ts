import { NextResponse } from "next/server";

export const maxDuration = 120;

export async function POST() {
  try {
    const { graphSync, graphPing } = await import("@/lib/neo4j");
    const reachable = await graphPing();
    if (!reachable) {
      return NextResponse.json({ error: "Neo4j niet bereikbaar" }, { status: 503 });
    }
    return NextResponse.json(await graphSync());
  } catch (e) {
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
