import { pool } from "@/lib/db";
import { browserFetch } from "@/lib/fetch";

// Kaart-ingest via de Riftcodex API (open, geen auth). Update-bestendig: nieuwe
// sets verschijnen in /sets en worden vanzelf meegenomen; kaarten worden geüpsert.
// Fallback op de officiële Riot card-gallery wanneer Riftcodex onbereikbaar is
// (bv. Cloudflare blokkeert datacenter-IP's).
const BASE = process.env.RIFTCODEX_BASE ?? "https://api.riftcodex.com";
const MAX_PAGES = 200;

interface RcSet {
  set_id?: string;
  name?: string;
  published_on?: string;
  card_count?: number;
}

interface RcCard {
  id?: string;
  riftbound_id?: string;
  name?: string;
  collector_number?: number;
  attributes?: { energy?: number; might?: number; power?: number };
  classification?: { type?: string; supertype?: string; rarity?: string; domain?: string[] };
  text?: { plain?: string; rich?: string };
  set?: { set_id?: string; label?: string };
  media?: { image_url?: string };
  tags?: string[];
}

async function getJson(url: string): Promise<unknown> {
  const r = await browserFetch(url);
  if (!r.ok) throw new Error(`HTTP ${r.status} ${url}`);
  return r.json();
}

// De API kan {data:[…]} / {items:[…]} / [...] teruggeven — normaliseer.
function listOf(j: unknown): unknown[] {
  if (Array.isArray(j)) return j;
  const o = (j ?? {}) as Record<string, unknown>;
  for (const key of ["data", "items", "results", "cards", "sets"]) {
    if (Array.isArray(o[key])) return o[key] as unknown[];
  }
  return [];
}

export async function syncSets(): Promise<string[]> {
  const ids: string[] = [];
  for (let page = 1; page <= MAX_PAGES; page++) {
    const rows = listOf(await getJson(`${BASE}/sets?page=${page}&size=100`)) as RcSet[];
    if (rows.length === 0) break;
    for (const s of rows) {
      if (!s.set_id) continue;
      const setId = s.set_id.toUpperCase();
      await pool.query(
        `INSERT INTO card_set (set_id, name, published_on, card_count)
         VALUES ($1, $2, $3, $4)
         ON CONFLICT (set_id) DO UPDATE SET
           name = EXCLUDED.name, published_on = EXCLUDED.published_on,
           card_count = EXCLUDED.card_count, synced_at = now()`,
        [setId, s.name ?? setId, s.published_on?.slice(0, 10) ?? null, s.card_count ?? null],
      );
      ids.push(s.set_id);
    }
    if (rows.length < 100) break;
  }
  return ids;
}

export async function syncCardsForSet(setId: string): Promise<number> {
  let count = 0;
  for (let page = 1; page <= MAX_PAGES; page++) {
    const url = `${BASE}/cards?set_id=${encodeURIComponent(setId)}&page=${page}&size=100`;
    const rows = listOf(await getJson(url)) as RcCard[];
    if (rows.length === 0) break;
    for (const c of rows) {
      const rid = c.riftbound_id ?? c.id;
      if (!rid) continue;
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
          rid,
          c.name ?? rid,
          c.classification?.type ?? null,
          c.classification?.supertype ?? null,
          c.classification?.rarity ?? null,
          c.classification?.domain ?? [],
          c.attributes?.energy ?? null,
          c.attributes?.might ?? null,
          c.attributes?.power ?? null,
          (c.set?.set_id ?? setId).toUpperCase(),
          c.set?.label ?? null,
          c.collector_number ?? null,
          c.text?.plain ?? null,
          c.media?.image_url ?? null,
          c.tags ?? [],
        ],
      );
      count++;
    }
    if (rows.length < 100) break;
  }
  return count;
}

async function syncViaRiftcodex(): Promise<{ sets: number; cards: number }> {
  const sets = await syncSets();
  let cards = 0;
  for (const setId of sets) cards += await syncCardsForSet(setId);
  return { sets: sets.length, cards };
}

/**
 * Volledige sync: alle sets + hun kaarten. Idempotent en update-bestendig.
 * CARD_SOURCE = auto (default) | riftcodex | riot. 'auto' probeert Riftcodex en
 * valt bij een fout terug op de officiële Riot card-gallery.
 */
export async function syncCards(): Promise<{ sets: number; cards: number; source: string }> {
  const pref = process.env.CARD_SOURCE ?? "auto";

  if (pref === "riot") {
    const { syncCardsFromRiot } = await import("./riot");
    return { ...(await syncCardsFromRiot()), source: "riot" };
  }

  try {
    return { ...(await syncViaRiftcodex()), source: "riftcodex" };
  } catch (e) {
    if (pref === "riftcodex") throw e;
    const { syncCardsFromRiot } = await import("./riot");
    return { ...(await syncCardsFromRiot()), source: "riot (fallback)" };
  }
}
