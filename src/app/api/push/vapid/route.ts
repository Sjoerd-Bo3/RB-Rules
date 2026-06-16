import { NextResponse } from "next/server";

// Publieke VAPID-sleutel voor de client (runtime, niet build-time inlined).
export async function GET() {
  return NextResponse.json({ publicKey: process.env.VAPID_PUBLIC_KEY ?? null });
}
