// Bronnen-register als config: toevoegen/herwegen = data-wijziging, geen code.
// trust_tier: 1 = officieel (Riot), 2 = officieel-aanverwant, 3 = betrouwbare
// community, 4 = overig. rank = fijnafstemming binnen een tier.
export type Parser = "html" | "pdf" | "json_api";

export interface SourceDef {
  id: string;
  name: string;
  url: string;
  type: "official" | "community";
  trust_tier: 1 | 2 | 3 | 4;
  rank: number;
  parser: Parser;
  cadence: "daily" | "weekly";
  enabled: boolean;
}

export const SOURCES: SourceDef[] = [
  {
    id: "rules-hub",
    name: "Riftbound Rules Hub (officieel)",
    url: "https://riftbound.leagueoflegends.com/en-us/rules-hub/",
    type: "official",
    trust_tier: 1,
    rank: 100,
    parser: "html",
    cadence: "daily",
    enabled: true,
  },
  {
    id: "tr-changelog",
    name: "Tournament Rules Update & Changelog (officieel)",
    url: "https://riftbound.leagueoflegends.com/en-us/news/announcements/april-2026-tournament-rules-update-changelog/",
    type: "official",
    trust_tier: 1,
    rank: 90,
    parser: "html",
    cadence: "daily",
    enabled: true,
  },
  {
    id: "card-errata",
    name: "Card Errata (riftbound.gg, community mirror)",
    url: "https://riftbound.gg/card-errata/",
    type: "community",
    trust_tier: 3,
    rank: 50,
    parser: "html",
    cadence: "weekly",
    enabled: true,
  },
  {
    // Cloudflare blokkeert datacenter-IP's (bv. de Azure-VM). Officiële bans komen
    // al via de Rules Hub (trust 1); zet OUTBOUND_PROXY + enabled via /admin als je
    // deze cross-check toch wilt.
    id: "mobalytics-bans",
    name: "Banned Cards (Mobalytics, community)",
    url: "https://mobalytics.gg/riftbound/guides/banned-cards",
    type: "community",
    trust_tier: 3,
    rank: 40,
    parser: "html",
    cadence: "weekly",
    enabled: false,
  },
];
