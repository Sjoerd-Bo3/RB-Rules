namespace RbRules.Domain;

/// <summary>Seed voor het bronnenregister (alleen bij ontbreken — /admin is
/// daarna de bron van waarheid, identiek aan de PoP-semantiek).
/// De Core Rules/Tournament Rules PDF's komen in S2 mét echte PDF-parser;
/// tot die tijd bewust géén 'pdf'-bron seeden (dode enum in de PoP was een
/// audit-bevinding).</summary>
public static class SourceSeed
{
    public static IReadOnlyList<Source> Defaults =>
    [
        new()
        {
            // De genummerde Core Rules zélf (dé audit-fix): url = ontdek-pagina,
            // de actuele PDF-link wordt per run van de hub geplukt.
            Id = "core-rules-pdf",
            Name = "Core Rules PDF (officieel)",
            Url = "https://playriftbound.com/en-us/rules-hub/",
            Type = "official", TrustTier = 1, Rank = 110, Parser = "pdf", Cadence = "daily",
        },
        new()
        {
            Id = "tournament-rules-pdf",
            Name = "Tournament Rules PDF (officieel)",
            Url = "https://playriftbound.com/en-us/rules-hub/",
            Type = "official", TrustTier = 1, Rank = 105, Parser = "pdf", Cadence = "daily",
        },
        new()
        {
            Id = "rules-hub",
            Name = "Riftbound Rules Hub (officieel)",
            Url = "https://playriftbound.com/en-us/rules-hub/",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
        },
        new()
        {
            // Officiële learn-to-play-inhoud (het "boekje" in webvorm) —
            // basisvoer voor de spelbegrip-laag (docs/KNOWLEDGE.md).
            Id = "how-to-play",
            Name = "How to Play – Quick Start Guide (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/how-to-play-get-started/",
            Type = "official", TrustTier = 1, Rank = 95, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            Id = "gameplay-guide",
            Name = "Gameplay Guide – Core Rules (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/gameplay-guide-core-rules/",
            Type = "official", TrustTier = 1, Rank = 94, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            Id = "tr-changelog",
            Name = "Tournament Rules Update & Changelog (officieel)",
            Url = "https://riftbound.leagueoflegends.com/en-us/news/announcements/april-2026-tournament-rules-update-changelog/",
            Type = "official", TrustTier = 1, Rank = 90, Parser = "html", Cadence = "daily",
        },
        new()
        {
            Id = "card-errata",
            Name = "Card Errata (riftbound.gg, community mirror)",
            Url = "https://riftbound.gg/card-errata/",
            Type = "community", TrustTier = 3, Rank = 50, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            // Cloudflare blokkeert datacenter-IP's; officiële bans komen via de
            // Rules Hub. Aanzetten kan via /admin (evt. met OUTBOUND_PROXY).
            Id = "mobalytics-bans",
            Name = "Banned Cards (Mobalytics, community)",
            Url = "https://mobalytics.gg/riftbound/guides/banned-cards",
            Type = "community", TrustTier = 3, Rank = 40, Parser = "html", Cadence = "weekly",
            Enabled = false,
        },
    ];
}
