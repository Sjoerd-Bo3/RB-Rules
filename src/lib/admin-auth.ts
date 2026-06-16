// Admin-auth: cookie-token afgeleid van ADMIN_PASSWORD (sha256). Werkt in zowel
// de edge-middleware als node-routes via Web Crypto.
export const ADMIN_COOKIE = "rb_admin";

export async function expectedToken(): Promise<string | null> {
  const pw = process.env.ADMIN_PASSWORD;
  if (!pw) return null;
  const data = new TextEncoder().encode(`${pw}::rb-rules`);
  const buf = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, "0")).join("");
}
