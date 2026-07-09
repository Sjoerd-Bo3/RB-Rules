// Wekelijkse digest: bundel de wijzigingen van de afgelopen 7 dagen en stuur één
// push-notificatie. Cron'baar: npm run digest
import "dotenv/config";
import { pool } from "@/lib/db";
import { pushConfigured, sendPush } from "@/lib/push";

async function main() {
  if (!pushConfigured()) {
    console.log("· VAPID niet ingesteld — digest overgeslagen");
    await pool.end();
    return;
  }

  const { rows } = await pool.query<{ total: string; high: string }>(
    `SELECT count(*)::text AS total,
            count(*) FILTER (WHERE severity = 'high')::text AS high
       FROM change WHERE detected_at > now() - interval '7 days'`,
  );
  const total = Number(rows[0]?.total ?? 0);
  const high = Number(rows[0]?.high ?? 0);

  if (total === 0) {
    console.log("· geen wijzigingen deze week — geen digest verstuurd");
    await pool.end();
    return;
  }

  const r = await sendPush({
    title: "Wekelijkse Riftbound-digest",
    body: `${total} wijziging(en) deze week${high ? `, waarvan ${high} belangrijk` : ""}.`,
    url: "/",
  });
  console.log(`✓ digest verstuurd naar ${r.sent} abonnee(s) (${total} wijzigingen)`);
  await pool.end();
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
