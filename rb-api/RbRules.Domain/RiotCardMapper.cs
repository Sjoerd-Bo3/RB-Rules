using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Parser voor Riot's officiële card-gallery JSON (de fallback-bron
/// die datacenter-IP's niet blokkeert). Puur — netwerk zit in Infrastructure.</summary>
public static partial class RiotCardMapper
{
    [GeneratedRegex("""/_next/static/([^/"]+)/_buildManifest""")]
    private static partial Regex BuildIdRegex();

    [GeneratedRegex(""""buildId":"([^"]+)"""")]
    private static partial Regex BuildIdJsonRegex();

    public static string? ExtractBuildId(string html)
    {
        var m = BuildIdRegex().Match(html);
        if (!m.Success) m = BuildIdJsonRegex().Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Verzamel alle kaart-items (genest onder …cards.items) en dedup op id.</summary>
    public static IReadOnlyList<Card> ParseGallery(JsonNode pageProps)
    {
        var found = new Dictionary<string, Card>();
        Walk(pageProps, found);
        return [.. found.Values];
    }

    private static void Walk(JsonNode? node, Dictionary<string, Card> found)
    {
        switch (node)
        {
            case JsonArray arr:
                foreach (var item in arr) Walk(item, found);
                break;
            case JsonObject obj:
                if (obj["items"] is JsonArray items)
                {
                    foreach (var item in items)
                    {
                        if (item is JsonObject o && o["id"] is not null && o["name"] is not null)
                        {
                            var card = MapCard(o);
                            found[card.RiftboundId] = card;
                        }
                    }
                }
                foreach (var (_, child) in obj) Walk(child, found);
                break;
        }
    }

    public static Card MapCard(JsonObject c)
    {
        var textHtml = c["text"]?["richText"]?["body"]?.GetValue<string>();
        return new Card
        {
            RiftboundId = c["id"]!.GetValue<string>(),
            Name = c["name"]!.GetValue<string>(),
            Type = c["cardType"]?["type"]?[0]?["label"]?.GetValue<string>(),
            Rarity = c["rarity"]?["value"]?["label"]?.GetValue<string>(),
            Domains = DomainsOf(c),
            Energy = NumOf(c["energy"]?["value"]),
            Might = NumOf(c["might"]?["value"]),
            Power = NumOf(c["power"]?["value"]),
            SetId = c["set"]?["value"]?["id"]?.GetValue<string>()?.ToUpperInvariant(),
            SetLabel = c["set"]?["value"]?["label"]?.GetValue<string>(),
            CollectorNumber = c["collectorNumber"]?.GetValue<int?>(),
            TextPlain = textHtml is null ? null : TextUtils.HtmlToText(textHtml),
            ImageUrl = c["cardImage"]?["url"]?.GetValue<string>(),
            Tags = c["tags"]?["tags"] is JsonArray t
                ? [.. t.Select(x => x?.GetValue<string>()).OfType<string>()]
                : [],
        };
    }

    private static string[] DomainsOf(JsonObject c)
    {
        // Riot gallery: domain.values = [{id,label}]
        if (c["domain"]?["values"] is JsonArray values)
            return [.. values.Select(v => v?["label"]?.GetValue<string>()).OfType<string>()];
        return [];
    }

    private static int? NumOf(JsonNode? value)
    {
        if (value?["id"] is JsonNode idNode && int.TryParse(idNode.ToString(), out var n)) return n;
        if (value?["label"] is JsonNode lbl && int.TryParse(lbl.GetValue<string>(), out var m)) return m;
        return null;
    }
}
