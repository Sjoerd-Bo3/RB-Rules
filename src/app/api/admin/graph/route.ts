import { NextResponse } from "next/server";

export const maxDuration = 120;

export async function POST() {
  try {
    const { graphSync, syncCardGraph, syncGraphLinks, graphPing } = await import(
      "@/lib/neo4j"
    );
    const reachable = await graphPing();
    if (!reachable) {
      return NextResponse.json({ error: "Neo4j niet bereikbaar" }, { status: 503 });
    }
    const rules = await graphSync();
    const cards = await syncCardGraph();
    const links = await syncGraphLinks();
    return NextResponse.json({ ...rules, ...cards, ...links });
  } catch (e) {
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
