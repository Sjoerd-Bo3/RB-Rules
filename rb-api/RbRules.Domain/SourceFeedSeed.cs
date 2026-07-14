namespace RbRules.Domain;

/// <summary>Seed voor het feed-register (#167, zelfde semantiek als
/// <see cref="SourceSeed"/>: alleen bij ontbreken — /admin is daarna de bron
/// van waarheid). Drie hoofdfeeds, elk met een eigen structuurprofiel maar
/// dezelfde <see cref="RiotNewsFeed"/>-parser (geverifieerd tegen een echte
/// fetch van alle drie, 2026-07-14):
/// - de smalle rules-and-releases-index (alleen die categorie);
/// - de brede algemene nieuws-hub (bevat ook merch/esports-nieuws — het
///   CategoryFilter houdt alleen de regel-relevante rubrieken tegen);
/// - de artikel-carrousel onderaan de Rules Hub zelf (kleine, al-curated
///   selectie — geen filter nodig; overlap met de andere twee feeds dedupt
///   vanzelf op URL).
/// Alle drie zijn official/AutoApprove: Riot's eigen nieuwspagina's zijn
/// dezelfde trust-1-laag als de bestaande SourceSeed-entries.</summary>
public static class SourceFeedSeed
{
    public static IReadOnlyList<SourceFeed> Defaults =>
    [
        new()
        {
            Id = "riot-rules-and-releases-feed",
            Name = "Riftbound Rules & Releases (officieel)",
            Url = "https://playriftbound.com/en-us/news/rules-and-releases/",
            CategoryFilter = "rules-and-releases",
            AutoApprove = true, Enabled = true, Cadence = "daily",
        },
        new()
        {
            // Breder dan hierboven (bevat ook merch/esports/product-nieuws) —
            // het filter laat alleen de regel-relevante rubrieken door.
            Id = "riot-news-hub-feed",
            Name = "Riftbound Nieuws-hub (officieel)",
            Url = "https://playriftbound.com/en-us/news/",
            CategoryFilter = "rules-and-releases,announcements,organizedplay",
            AutoApprove = true, Enabled = true, Cadence = "daily",
        },
        new()
        {
            // De hub toont onderaan dezelfde kaartcomponent als de
            // nieuwspagina's (een klein, al-curated "laatste nieuws"-blok) —
            // geen filter nodig; overlap met de twee feeds hierboven dedupt
            // vanzelf op URL. De patch notes/errata-links die de hub zelf al
            // ontdekt (legacy-domein platte ankers) zijn HubDiscovery-terrein
            // in IngestService en blijven ongemoeid.
            Id = "riot-rules-hub-feed",
            Name = "Riftbound Rules Hub — artikel-carrousel (officieel)",
            Url = "https://playriftbound.com/en-us/rules-hub/",
            CategoryFilter = null,
            AutoApprove = true, Enabled = true, Cadence = "daily",
        },
    ];
}
