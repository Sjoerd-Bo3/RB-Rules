import { NextResponse } from "next/server";

export const maxDuration = 120;

export async function POST() {
  try {
    const { graphSync, syncCardGraph, syncGraphLinks, graphPing } = await import(
      "@/lib/neo4j"
    );
    const { logRun } = await import("@/lib/runlog");
    const reachable = await graphPing();
    if (!reachable) {
      await logRun("graph", null, "error", "Neo4j niet bereikbaar");
      return NextResponse.json({ error: "Neo4j niet bereikbaar" }, { status: 503 });
    }
    const rules = await graphSync();
    const cards = await syncCardGraph();
    const links = await syncGraphLinks();
    await logRun(
      "graph",
      null,
      "ok",
      `${cards.cards} cards, ${rules.ruleSections} secties, ${links.defined} keyword→regel, ${links.banned} bans`,
    );
    return NextResponse.json({ ...rules, ...cards, ...links });
  } catch (e) {
    const { logRun } = await import("@/lib/runlog");
    await logRun("graph", null, "error", e);
    return NextResponse.json({ error: String(e) }, { status: 500 });
  }
}
