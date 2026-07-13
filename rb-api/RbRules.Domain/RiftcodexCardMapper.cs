using System.Text.Json.Nodes;

namespace RbRules.Domain;

/// <summary>Parser + normalisatie voor riftcodex-kaarten (#144). Riftcodex
/// gebruikt andere bronvormen dan de Riot-gallery: streepjes-namen
/// ("Soraka - Wanderer (Signature)" i.p.v. "Soraka, Wanderer") en ster-id's
/// ("sfd-239*-221" i.p.v. "sfd-239-star-221"). De adapter normaliseert bij
/// binnenkomst naar de canonieke (Riot-)vorm, zodat variantgroepering,
/// zoeken en /ask één schrijfwijze zien. Puur — netwerk zit in Infrastructure.</summary>
public static class RiftcodexCardMapper
{
    /// <summary>Mapt één riftcodex-kaartobject; het id wordt direct naar de
    /// canonieke suffixvorm genormaliseerd. Naamnormalisatie gebeurt apart
    /// (<see cref="ResolveName"/>) — die heeft bewijs uit de bestaande
    /// kaartenset nodig.</summary>
    public static Card? MapCard(JsonObject c, string fallbackSetId)
    {
        var rid = c["riftbound_id"]?.GetValue<string>() ?? c["id"]?.GetValue<string>();
        if (rid is null) return null;
        rid = RiftboundIds.Normalize(rid);
        return new Card
        {
            RiftboundId = rid,
            Name = c["name"]?.GetValue<string>() ?? rid,
            Type = c["classification"]?["type"]?.GetValue<string>(),
            Supertype = c["classification"]?["supertype"]?.GetValue<string>(),
            Rarity = c["classification"]?["rarity"]?.GetValue<string>(),
            Domains = c["classification"]?["domain"] is JsonArray d
                ? [.. d.Select(x => x?.GetValue<string>()).OfType<string>()]
                : [],
            Energy = c["attributes"]?["energy"]?.GetValue<int?>(),
            Might = c["attributes"]?["might"]?.GetValue<int?>(),
            Power = c["attributes"]?["power"]?.GetValue<int?>(),
            SetId = (c["set"]?["set_id"]?.GetValue<string>() ?? fallbackSetId).ToUpperInvariant(),
            SetLabel = c["set"]?["label"]?.GetValue<string>(),
            CollectorNumber = c["collector_number"]?.GetValue<int?>(),
            TextPlain = c["text"]?["plain"]?.GetValue<string>(),
            ImageUrl = c["media"]?["image_url"]?.GetValue<string>(),
            Tags = c["tags"] is JsonArray t
                ? [.. t.Select(x => x?.GetValue<string>()).OfType<string>()]
                : [],
        };
    }

    private const string DashSeparator = " - ";

    /// <summary>Riftcodex-separator ("Naam - Epithet") aanwezig?</summary>
    public static bool HasDashName(string name) =>
        name.Contains(DashSeparator, StringComparison.Ordinal);

    /// <summary>Welke naam wint bij de riftcodex-upsert: een bestaande naam
    /// wint ALTIJD (riftcodex vult alleen gaten) — óók als die zelf een
    /// separator draagt, want de Riot-gallery kent échte streepjes-namen
    /// ("Dark Child - Starter") die riftcodex anders noemt; die hier laten
    /// overschrijven gaf een naam-flip-flop per bronwissel. Dash-artefacten
    /// van vóór de normalisatie herstelt uitsluitend het bewijs-pad van de
    /// reparatie (CardSyncService.RepairSourceFormsAsync).</summary>
    public static string ResolveName(
        string? existingName, string riftcodexName, IReadOnlySet<string> knownCommaBaseNames)
    {
        if (existingName is not null) return existingName;
        return NormalizeName(riftcodexName, knownCommaBaseNames);
    }

    /// <summary>Zet het riftcodex-separator-patroon "Naam - Epithet" om naar de
    /// komma-vorm van de Riot-gallery — maar ALLEEN als de komma-basisnaam al
    /// als kaart bekend is (aantoonbaar dezelfde kaart; variantgroepering
    /// groepeert op precies die basisnaam). Zonder bewijs blijft de naam
    /// staan: "Dark Child - Starter" is een écht Riot-naampatroon, geen
    /// riftcodex-artefact. Alleen de eerste separator telt — binnenwoordse
    /// streepjes ("Chem-Baroness") hebben geen spaties en blijven intact.</summary>
    public static string NormalizeName(string name, IReadOnlySet<string> knownCommaBaseNames)
    {
        var split = name.IndexOf(DashSeparator, StringComparison.Ordinal);
        if (split <= 0) return name;
        var candidate = $"{name[..split]}, {name[(split + DashSeparator.Length)..]}";
        return knownCommaBaseNames.Contains(CardText.BaseName(candidate)) ? candidate : name;
    }

    /// <summary>Bewijsverzameling voor <see cref="NormalizeName"/>: alle
    /// basisnamen in komma-vorm die de kaartenset al kent.</summary>
    public static HashSet<string> CommaBaseNames(IEnumerable<string> cardNames) =>
        [.. cardNames
            .Where(n => n.Contains(", ", StringComparison.Ordinal))
            .Select(CardText.BaseName)];
}
