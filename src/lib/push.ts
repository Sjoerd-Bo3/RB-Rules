import webpush from "web-push";
import { pool } from "@/lib/db";

// Web-push (VAPID). Genereer sleutels eenmalig: npx web-push generate-vapid-keys
// Zet VAPID_PUBLIC_KEY / VAPID_PRIVATE_KEY / VAPID_SUBJECT (mailto:...).
let inited = false;
function init(): boolean {
  const pub = process.env.VAPID_PUBLIC_KEY;
  const priv = process.env.VAPID_PRIVATE_KEY;
  if (!pub || !priv) return false;
  if (!inited) {
    webpush.setVapidDetails(process.env.VAPID_SUBJECT ?? "mailto:admin@example.com", pub, priv);
    inited = true;
  }
  return true;
}

export function pushConfigured(): boolean {
  return Boolean(process.env.VAPID_PUBLIC_KEY && process.env.VAPID_PRIVATE_KEY);
}

export interface SubInput {
  endpoint: string;
  keys: { p256dh: string; auth: string };
}

export async function saveSubscription(sub: SubInput): Promise<void> {
  await pool.query(
    `INSERT INTO push_subscription (endpoint, p256dh, auth) VALUES ($1, $2, $3)
     ON CONFLICT (endpoint) DO UPDATE SET p256dh = EXCLUDED.p256dh, auth = EXCLUDED.auth`,
    [sub.endpoint, sub.keys.p256dh, sub.keys.auth],
  );
}

export async function removeSubscription(endpoint: string): Promise<void> {
  await pool.query("DELETE FROM push_subscription WHERE endpoint = $1", [endpoint]);
}

export interface PushPayload {
  title: string;
  body: string;
  url?: string;
}

/** Stuur een notificatie naar alle abonnees; ruimt dode abonnementen op. */
export async function sendPush(payload: PushPayload): Promise<{ sent: number }> {
  if (!init()) return { sent: 0 };
  const { rows } = await pool.query<{ endpoint: string; p256dh: string; auth: string }>(
    "SELECT endpoint, p256dh, auth FROM push_subscription",
  );
  let sent = 0;
  for (const r of rows) {
    try {
      await webpush.sendNotification(
        { endpoint: r.endpoint, keys: { p256dh: r.p256dh, auth: r.auth } },
        JSON.stringify(payload),
      );
      sent++;
    } catch (e) {
      const code = (e as { statusCode?: number }).statusCode;
      if (code === 404 || code === 410) await removeSubscription(r.endpoint);
    }
  }
  return { sent };
}
