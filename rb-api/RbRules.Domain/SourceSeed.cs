namespace RbRules.Domain;

/// <summary>Seed voor het bronnenregister (alleen bij ontbreken — /admin is
/// daarna de bron van waarheid, identiek aan de PoP-semantiek).
/// De Core Rules/Tournament Rules PDF's komen in S2 mét echte PDF-parser;
/// tot die tijd bewust géén 'pdf'-bron seeden (dode enum in de PoP was een
/// audit-bevinding).</summary>
public static class SourceSeed
{
    /// <summary>De hub-index is óók de ontdek-pagina voor per-set-artikelen
    /// (#94): IngestService draait er HubDiscovery op en logt onbekende
    /// "… Patch Notes"/"… Errata"-links als bronvoorstel.</summary>
    public const string RulesHubId = "rules-hub";

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
            Id = RulesHubId,
            Name = "Riftbound Rules Hub (officieel)",
            Url = "https://playriftbound.com/en-us/rules-hub/",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "html", Cadence = "daily",
        },
        new()
        {
            // Per-set patch notes van de Rules Hub (#94): officiële rulings-
            // en interactie-uitleg ("questions and … rules and interactions
            // you'll commonly encounter") — goud voor de vraagbaak, dus vlak
            // onder de core-documenten. De hub linkt via het legacy-domein
            // riftbound.leagueoflegends.com; hier staat het canonieke
            // playriftbound.com-doel van die 301 (geverifieerd 2026-07-11,
            // HTTP 200). Nieuwe sets komen binnen via de hub-ontdekking
            // (HubDiscovery) als bronvoorstel, niet via deze seed.
            Id = "core-rules-patch-notes",
            Name = "Core Rules Patch Notes (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 99, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            Id = "spiritforged-patch-notes",
            Name = "Spiritforged Patch Notes (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-spiritforged-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 98, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            Id = "unleashed-patch-notes",
            Name = "Unleashed Patch Notes (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-core-rules-unleashed-patch-notes/",
            Type = "official", TrustTier = 1, Rank = 97, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            // Officiële per-set-errata van de Rules Hub (#94) — de dekking
            // die de community-mirror (card-errata hieronder) tot nu toe
            // alleen droeg. De Id-conventie "…errata" is dragend:
            // BanErrataSyncService structureert alle trust-1-bronnen met
            // "errata" in de Id. De set-context zit in de bronnaam en gaat
            // mee de LLM-extractie in. Zelfde legacy-domein-verhaal als de
            // patch notes hierboven; ranks 95-93 zijn de spelbegrip-gidsen,
            // vandaar 96 → 92/91.
            Id = "origins-errata",
            Name = "Origins Card Errata (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-origins-card-errata/",
            Type = "official", TrustTier = 1, Rank = 96, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            Id = "spiritforged-errata",
            Name = "Spiritforged Errata (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/riftbound-spiritforged-errata/",
            Type = "official", TrustTier = 1, Rank = 92, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            Id = "unleashed-errata",
            Name = "Unleashed Errata (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/unleashed-errata-updates/",
            Type = "official", TrustTier = 1, Rank = 91, Parser = "html", Cadence = "weekly",
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
            // Officiële deckbuilding-primer van Riot — zelfde patroon als
            // how-to-play/gameplay-guide hierboven, dus trust 1. Startbron
            // voor de spelbegrip-laag (#63, docs/KNOWLEDGE.md).
            Id = "deckbuilding-primer",
            Name = "Deckbuilding Primer (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/deckbuilding-primer/",
            Type = "official", TrustTier = 1, Rank = 93, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            // UVS Games is Riots organized-play-partner voor Riftbound:
            // officieel partnermateriaal, maar niet door Riot zelf
            // gepubliceerd → trust 2 (boven community, onder officieel).
            // Type "partner" breidt het official|community-vocabulaire uit;
            // niets switcht op deze string. Directe PDF-URL mét tekstlaag —
            // de ingest herkent de application/pdf-respons en slaat de
            // ontdek-stap (Rules Hub-patroon) over.
            // Geverifieerd 2026-07-11: HTTP 200 vanaf lokaal én de VM.
            Id = "uvs-how-to-play-pdf",
            Name = "How to Play – Proving Grounds PDF (UVS Games, partner)",
            Url = "https://uvsgames.com/wp-content/uploads/2025/11/OGS-ProvingGrounds-HowtoPlay-EN.pdf",
            Type = "partner", TrustTier = 2, Rank = 70, Parser = "pdf", Cadence = "weekly",
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
            // Sinds #94 alleen nog change-feed/claims-voer: de gestructureerde
            // errata komen van de officiële per-set-pagina's hierboven
            // (BanErrataSyncService selecteert op trust 1 — deze trust-3-
            // mirror structureert bewust niet meer mee; officieel wint).
            Id = "card-errata",
            Name = "Card Errata (riftbound.gg, community mirror)",
            Url = "https://riftbound.gg/card-errata/",
            Type = "community", TrustTier = 3, Rank = 50, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            // Community-beginnersgidsen voor de spelbegrip-laag (#63):
            // community krijgt nooit trust 1 (docs/KNOWLEDGE.md); trust 3
            // conform de bestaande community-bronnen — het gewicht per claim
            // komt van de corroboratie-regels (#50).
            // Geverifieerd 2026-07-11: HTTP 200 met server-side gerenderd
            // artikel, óók vanaf de VM (geen datacenter-blokkade zoals bij
            // Mobalytics/Riftcodex hieronder).
            Id = "beginners-guide-riftboundgg",
            Name = "Beginner's Guide – Basic Rules & How to Play (riftbound.gg, community)",
            Url = "https://riftbound.gg/riftbound-beginners-guide-basic-rules-how-to-play/",
            Type = "community", TrustTier = 3, Rank = 48, Parser = "html", Cadence = "weekly",
        },
        new()
        {
            // Zelfde afweging als de riftbound.gg-gids hierboven; ook vanaf
            // de VM bereikbaar geverifieerd op 2026-07-11.
            Id = "beginners-guide-fanfinity",
            Name = "Complete Beginner's Guide (Fanfinity, community)",
            Url = "https://www.fanfinity.gg/blog/riftbound-your-complete-beginners-guide-to-the-new-league-of-legends-trading-card-game/",
            Type = "community", TrustTier = 3, Rank = 46, Parser = "html", Cadence = "weekly",
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
