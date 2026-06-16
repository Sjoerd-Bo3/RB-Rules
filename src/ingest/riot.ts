import { pool } from "@/lib/db";
import { browserFetch } from "@/lib/fetch";
import { htmlToText } from "@/lib/text";

// Officiële Riot card-gallery als kaart-bron/fallback. Gezaghebbend (trust 1) en
// blokkeert datacenter-IP's niet zoals Riftcodex/Cloudflare dat kan doen.
const GALLERY = "https://riftbound.leagueoflegends.com/en-us/card-gallery/";

async function buildId(): Promise<string> {
  const html = await (await browserFetch(GALLERY)).text();
  const m =
    html.match(/\/_next\/static\/([^/"]+)\/_buildManifest/) ||
    html.match(/"buildId":"([^"]+)"/);
  if (!m) throw new Error("Riot build-id niet gevonden");
  return m[1];
}

// Verzamel alle kaart-items (genest onder pageProps.…cards.items).
function collectItems(o: unknown, out: RiotCard[] = []): RiotCard[] {
  if (Array.isArray(o)) {
    for (const v of o) collectItems(v, out);
  } else if (o && typeof o === "object") {
    const rec = o as Record<string, unknown>;
    const items = rec.items;
    if (Array.isArray(items)) {
      for (const it of items) {
        const c = it as RiotCard;
        if (c && typeof c === "object" && c.id && c.name) out.push(c);
      }
    }
    for (const k in rec) collectItems(rec[k], out);
  }
  return out;
}

interface RiotCard {
  id: string;
  name: string;
  collectorNumber?: number;
  set?: { value?: { id?: string; label?: string } };
  cardType?: { type?: { id?: string; label?: string }[] };
  rarity?: { value?: { label?: string } };
  domain?: { values?: { id?: string; label?: string }[]; value?: unknown; tags?: string[] };
  energy?: { value?: { id?: number; label?: string } };
  might?: { value?: { id?: number; label?: string } };
  power?: { value?: { id?: number; label?: string } };
  text?: { richText?: { body?: string } };
  cardImage?: { url?: string };
  tags?: { tags?: string[] };
}

function num(v: { id?: number; label?: string } | undefined): number | null {
  const n = Number(v?.id ?? v?.label);
  return Number.isFinite(n) ? n : null;
}

function domainsOf(c: RiotCard): string[] {
  // Riot gallery: domain.values = [{id,label}]
  if (Array.isArray(c.domain?.values)) {
    return c.domain!.values.map((d) => d.label).filter((l): l is string => Boolean(l));
  }
  // Riftcodex-achtige vormen als fallback
  const v = c.domain?.value;
  if (Array.isArray(v)) {
    return v
      .map((d) => (d && typeof d === "object" ? ((d as Record<string, unknown>).label as string) : String(d)))
      .filter(Boolean);
  }
  return c.domain?.tags ?? [];
}

/** Pure mapping Riot-kaart → DB-rij (testbaar zonder DB). */
export function mapRiotCard(c: RiotCard) {
  return {
    riftbound_id: c.id,
    name: c.name,
    type: c.cardType?.type?.[0]?.label ?? null,
    supertype: null as string | null,
    rarity: c.rarity?.value?.label ?? null,
    domains: domainsOf(c),
    energy: num(c.energy?.value),
    might: num(c.might?.value),
    power: num(c.power?.value),
    set_id: (c.set?.value?.id ?? "").toUpperCase() || null,
    set_label: c.set?.value?.label ?? null,
    collector_number: c.collectorNumber ?? null,
    text_plain: c.text?.richText?.body ? htmlToText(c.text.richText.body) : null,
    image_url: c.cardImage?.url ?? null,
    tags: c.tags?.tags ?? [],
  };
}

export async function syncCardsFromRiot(): Promise<{ sets: number; cards: number }> {
  const bid = await buildId();
  const res = await browserFetch(
    `https://riftbound.leagueoflegends.com/_next/data/${bid}/en-us/card-gallery.json`,
  );
  if (!res.ok) throw new Error(`Riot gallery HTTP ${res.status}`);
  const j = (await res.json()) as { pageProps?: unknown };

  const uniq = new Map<string, RiotCard>();
  for (const c of collectItems(j.pageProps)) uniq.set(c.id, c);

  const setIds = new Set<string>();
  for (const c of uniq.values()) {
    const r = mapRiotCard(c);
    if (r.set_id) setIds.add(r.set_id);
    await pool.query(
      `INSERT INTO card
         (riftbound_id, name, type, supertype, rarity, domains, energy, might, power,
          set_id, set_label, collector_number, text_plain, image_url, tags)
       VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
       ON CONFLICT (riftbound_id) DO UPDATE SET
         name=EXCLUDED.name, type=EXCLUDED.type, supertype=EXCLUDED.supertype,
         rarity=EXCLUDED.rarity, domains=EXCLUDED.domains, energy=EXCLUDED.energy,
         might=EXCLUDED.might, power=EXCLUDED.power, set_id=EXCLUDED.set_id,
         set_label=EXCLUDED.set_label, collector_number=EXCLUDED.collector_number,
         text_plain=EXCLUDED.text_plain, image_url=EXCLUDED.image_url,
         tags=EXCLUDED.tags, updated_at=now()`,
      [
        r.riftbound_id, r.name, r.type, r.supertype, r.rarity, r.domains, r.energy,
        r.might, r.power, r.set_id, r.set_label, r.collector_number, r.text_plain,
        r.image_url, r.tags,
      ],
    );
  }
  for (const sid of setIds) {
    await pool.query(
      `INSERT INTO card_set (set_id, name) VALUES ($1, $1)
       ON CONFLICT (set_id) DO UPDATE SET synced_at = now()`,
      [sid],
    );
  }
  return { sets: setIds.size, cards: uniq.size };
}
