import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { ADMIN_COOKIE, expectedToken } from "@/lib/admin-auth";

// Beveiligt /admin en /api/admin. Zonder geldige sessie → login (of 401 voor API).
export async function middleware(req: NextRequest) {
  const { pathname } = req.nextUrl;

  // Login-pagina en login-API altijd toelaten.
  if (pathname === "/admin/login" || pathname === "/api/admin/login") {
    return NextResponse.next();
  }

  const token = await expectedToken();
  const cookie = req.cookies.get(ADMIN_COOKIE)?.value;
  if (token && cookie === token) return NextResponse.next();

  if (pathname.startsWith("/api/admin")) {
    return NextResponse.json({ error: "unauthorized" }, { status: 401 });
  }
  const url = req.nextUrl.clone();
  url.pathname = "/admin/login";
  return NextResponse.redirect(url);
}

export const config = {
  matcher: ["/admin/:path*", "/api/admin/:path*"],
};
