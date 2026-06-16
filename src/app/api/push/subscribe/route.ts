import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { removeSubscription, saveSubscription } from "@/lib/push";

export async function POST(req: NextRequest) {
  const sub = (await req.json().catch(() => ({}))) as {
    endpoint?: string;
    keys?: { p256dh?: string; auth?: string };
  };
  if (!sub.endpoint || !sub.keys?.p256dh || !sub.keys?.auth) {
    return NextResponse.json({ error: "Ongeldig abonnement" }, { status: 400 });
  }
  await saveSubscription({
    endpoint: sub.endpoint,
    keys: { p256dh: sub.keys.p256dh, auth: sub.keys.auth },
  });
  return NextResponse.json({ ok: true });
}

export async function DELETE(req: NextRequest) {
  const { endpoint } = (await req.json().catch(() => ({}))) as { endpoint?: string };
  if (endpoint) await removeSubscription(endpoint);
  return NextResponse.json({ ok: true });
}
