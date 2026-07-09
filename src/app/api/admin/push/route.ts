import { NextResponse } from "next/server";
import { pool } from "@/lib/db";
import { pushConfigured, sendPush } from "@/lib/push";

// Aantal abonnees + of push geconfigureerd is.
export async function GET() {
  const { rows } = await pool.query<{ n: string }>(
    "SELECT count(*)::text AS n FROM push_subscription",
  );
  return NextResponse.json({ configured: pushConfigured(), subscribers: Number(rows[0]?.n ?? 0) });
}

// Test-notificatie naar alle abonnees.
export async function POST() {
  if (!pushConfigured()) {
    return NextResponse.json({ error: "VAPID-sleutels niet ingesteld" }, { status: 400 });
  }
  const r = await sendPush({
    title: "Testnotificatie",
    body: "Push werkt 🎉 — je krijgt voortaan belangrijke wijzigingen.",
    url: "/",
  });
  return NextResponse.json(r);
}
