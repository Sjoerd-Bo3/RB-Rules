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
                            // De gallery-JSON bevat ook set-facetten ({id:'OGN',
                            // name:'Origins', collectorNumberMax}) — geen kaarten.
                            var id = o["id"]!.GetValue<string>();
                            if (!id.Contains('-')) continue;
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
        var textPlain = textHtml is null ? null : TextUtils.HtmlToText(textHtml);
        var effectHtml = c["effect"]?["richText"]?["body"]?.GetValue<string>();
        var image = c["cardImage"] as JsonObject;
        var imageUrl = image?["url"]?.GetValue<string>();
        // Riot geeft dimensions bij élke kaart; de URL-afleiding is de vangnet
        // voor als dat ooit ontbreekt, zodat een tegel nooit zonder verhouding
        // zit (#269).
        var size = SizeOf(image) ?? CardPresentation.SizeFromUrl(imageUrl);
        var name = c["name"]!.GetValue<string>();
        // Token-kaarten (unl-t04/t08) hebben een lege type-lijst — ?[0]
        // op een lege JsonArray gooit een index-fout.
        var type = c["cardType"]?["type"] is JsonArray { Count: > 0 } types
            ? types[0]?["label"]?.GetValue<string>()
            : null;
        return new Card
        {
            PublicCode = c["publicCode"]?.GetValue<string>(),
            // illustrator.values is een lijst; in de hele gallery heeft elke
            // kaart er precies één.
            Illustrator = c["illustrator"]?["values"] is JsonArray { Count: > 0 } ill
                ? ill[0]?["label"]?.GetValue<string>()
                : null,
            MightBonus = NumOf(c["mightBonus"]?["value"]),
            EffectPlain = effectHtml is null ? null : TextUtils.HtmlToText(effectHtml),
            Flags = c["flags"] is JsonArray flags
                ? [.. flags.Select(f => f?["label"]?.GetValue<string>()).OfType<string>()]
                : [],
            ImageWidth = size?.Width,
            ImageHeight = size?.Height,
            ImageColorPrimary =
                CardPresentation.NormalizeHexColor(image?["colors"]?["primary"]?.GetValue<string>()),
            ImageColorSecondary =
                CardPresentation.NormalizeHexColor(image?["colors"]?["secondary"]?.GetValue<string>()),
            // Riots eigen accessibilityText waar die er is; anders zelf
            // samenstellen — uitsluitend voor alt=, nooit als kaarttekst (#270).
            // De gallery kent geen supertype, dus die blijft hier leeg.
            ImageAltText = image?["accessibilityText"]?.GetValue<string>()
                ?? CardPresentation.ComposeAltText(name, null, type, textPlain),
            RiftboundId = c["id"]!.GetValue<string>(),
            Name = name,
            Type = type,
            Rarity = c["rarity"]?["value"]?["label"]?.GetValue<string>(),
            Domains = DomainsOf(c),
            Energy = NumOf(c["energy"]?["value"]),
            Might = NumOf(c["might"]?["value"]),
            Power = NumOf(c["power"]?["value"]),
            SetId = c["set"]?["value"]?["id"]?.GetValue<string>()?.ToUpperInvariant(),
            SetLabel = c["set"]?["value"]?["label"]?.GetValue<string>(),
            CollectorNumber = c["collectorNumber"]?.GetValue<int?>(),
            // Icon-tokens (:rb_energy_1: e.d.) blijven rauw in de opslag —
            // de site rendert ze als echte iconen; embeddings/LLM krijgen
            // de leesbare variant via CardText.HumanizeIcons.
            TextPlain = textPlain,
            ImageUrl = imageUrl,
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

    /// <summary>cardImage.dimensions = {width, height, aspectRatio}; alleen
    /// width/height overnemen — de ratio is daaruit af te leiden en twee
    /// bronnen voor dezelfde waarheid kunnen gaan afwijken (#269).</summary>
    private static (int Width, int Height)? SizeOf(JsonObject? image)
    {
        var dims = image?["dimensions"];
        if (dims?["width"]?.GetValue<int?>() is not { } w ||
            dims["height"]?.GetValue<int?>() is not { } h ||
            w <= 0 || h <= 0) return null;
        return (w, h);
    }

    private static int? NumOf(JsonNode? value)
    {
        if (value?["id"] is JsonNode idNode && int.TryParse(idNode.ToString(), out var n)) return n;
        if (value?["label"] is JsonNode lbl && int.TryParse(lbl.GetValue<string>(), out var m)) return m;
        return null;
    }
}
