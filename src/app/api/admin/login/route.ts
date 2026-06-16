import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { ADMIN_COOKIE, expectedToken } from "@/lib/admin-auth";

export async function POST(req: NextRequest) {
  const pw = process.env.ADMIN_PASSWORD;
  if (!pw) {
    return NextResponse.json(
      { error: "ADMIN_PASSWORD is niet ingesteld in .env" },
      { status: 400 },
    );
  }

  const { password } = (await req.json().catch(() => ({}))) as { password?: string };
  if (password !== pw) {
    return NextResponse.json({ error: "Onjuist wachtwoord" }, { status: 401 });
  }

  const token = await expectedToken();
  const isHttps =
    req.nextUrl.protocol === "https:" ||
    req.headers.get("x-forwarded-proto") === "https";

  const res = NextResponse.json({ ok: true });
  res.cookies.set(ADMIN_COOKIE, token!, {
    httpOnly: true,
    sameSite: "lax",
    secure: isHttps,
    path: "/",
    maxAge: 60 * 60 * 24 * 30,
  });
  return res;
}

export async function DELETE() {
  const res = NextResponse.json({ ok: true });
  res.cookies.set(ADMIN_COOKIE, "", { path: "/", maxAge: 0 });
  return res;
}
