using System.Text.Json.Nodes;
using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Getest tegen de echte veld-vormen van Riot's card-gallery JSON
/// (live geverifieerd tijdens de PoP-fase, incl. de domain.values-vorm).</summary>
public class RiotCardMapperTests
{
    private const string SampleCard = """
        {
          "id": "ogn-056-298",
          "collectorNumber": 56,
          "name": "Adaptatron",
          "set": {"label": "Card Set", "value": {"id": "OGN", "label": "Origins"}},
          "cardType": {"label": "Card Type", "type": [{"id": "unit", "label": "Unit"}]},
          "rarity": {"label": "Rarity", "value": {"id": "uncommon", "label": "Uncommon"}},
          "domain": {"label": "Domain", "values": [{"id": "calm", "label": "Calm"}]},
          "energy": {"label": "Energy", "value": {"id": 4, "label": "4"}},
          "might": {"label": "Might", "value": {"id": 3, "label": "3"}},
          "text": {"label": "Ability", "richText": {"type": "html", "body": "<p>When I conquer, buff me.</p>"}},
          "cardImage": {"type": "image", "url": "https://example.com/card.png"},
          "tags": {"label": "Tags", "tags": ["Mech", "Piltover"]}
        }
        """;

    [Fact]
    public void MapCard_MapsAllFields()
    {
        var card = RiotCardMapper.MapCard((JsonObject)JsonNode.Parse(SampleCard)!);
        Assert.Equal("ogn-056-298", card.RiftboundId);
        Assert.Equal("Adaptatron", card.Name);
        Assert.Equal("Unit", card.Type);
        Assert.Equal("Uncommon", card.Rarity);
        Assert.Equal(["Calm"], card.Domains);
        Assert.Equal(4, card.Energy);
        Assert.Equal(3, card.Might);
        Assert.Equal("OGN", card.SetId);
        Assert.Equal(56, card.CollectorNumber);
        Assert.Equal("When I conquer, buff me.", card.TextPlain);
        Assert.Equal(["Mech", "Piltover"], card.Tags);
    }

    [Fact]
    public void ParseGallery_CollectsAndDedupsNestedItems()
    {
        var pageProps = JsonNode.Parse(
            "{\"page\": {\"blades\": [{\"cards\": {\"items\": [" +
            SampleCard + ", " + SampleCard + "]}}]}}")!;
        var cards = RiotCardMapper.ParseGallery(pageProps);
        Assert.Single(cards);
    }

    [Fact]
    public void ParseGallery_SkipsSetFacetItems()
    {
        // De live gallery-JSON bevat set-facetten in dezelfde items-vorm
        // ({id:'VEN', name:'Vendetta', collectorNumberMax}) — geen kaarten.
        var pageProps = JsonNode.Parse(
            "{\"blades\": [{\"filters\": {\"items\": [" +
            "{\"id\": \"VEN\", \"name\": \"Vendetta\", \"collectorNumberMax\": 300}," +
            "{\"id\": \"OGN\", \"name\": \"Origins\", \"collectorNumberMax\": 298}]}}," +
            "{\"cards\": {\"items\": [" + SampleCard + "]}}]}")!;
        var cards = RiotCardMapper.ParseGallery(pageProps);
        var card = Assert.Single(cards);
        Assert.Contains('-', card.RiftboundId);
    }

    [Fact]
    public void ExtractBuildId_FromStaticPath()
    {
        var id = RiotCardMapper.ExtractBuildId(
            "<script src=\"/_next/static/4Y1QN9_bmiQlSZfXckICU/_buildManifest.js\"></script>");
        Assert.Equal("4Y1QN9_bmiQlSZfXckICU", id);
    }

    [Theory]
    [InlineData("Teemo - Swift Scout (Alternate Art)", "Teemo - Swift Scout")]
    [InlineData("Teemo - Swift Scout (Signature)", "Teemo - Swift Scout")]
    [InlineData("Teemo - Swift Scout (Overnumbered)", "Teemo - Swift Scout")]
    [InlineData("Teemo - Swift Scout", "Teemo - Swift Scout")]
    public void BaseName_StripsPrintingSuffix(string input, string expected) =>
        Assert.Equal(expected, CardText.BaseName(input));

    [Fact]
    public void HumanizeIcons_MakesTokensReadable()
    {
        Assert.Equal(
            "You may pay (1) to hide a card with [Hidden] instead of [rune rainbow].(1), [exhaust]: Put a Teemo unit you own into your hand.",
            CardText.HumanizeIcons(
                "You may pay :rb_energy_1: to hide a card with [Hidden] instead of :rb_rune_rainbow:.:rb_energy_1:, :rb_exhaust:: Put a Teemo unit you own into your hand."));
    }

    [Fact]
    public void MapCard_HandlesMissingOptionalFields()
    {
        var card = RiotCardMapper.MapCard(
            (JsonObject)JsonNode.Parse("""{"id": "x-1", "name": "Kaal"}""")!);
        Assert.Equal("x-1", card.RiftboundId);
        Assert.Null(card.Energy);
        Assert.Empty(card.Domains);
        Assert.Empty(card.Tags);
    }

    [Fact]
    public void MapCard_HandlesEmptyTypeList()
    {
        // Live-regressie: token-kaarten (unl-t04/t08) hebben cardType.type = []
        // — dat gaf 'Index was out of range' en brak de hele kaarten-sync.
        var card = RiotCardMapper.MapCard((JsonObject)JsonNode.Parse(
            """{"id": "unl-t04", "name": "Mushroom Token", "cardType": {"label": "Card Type", "type": []}}""")!);
        Assert.Equal("unl-t04", card.RiftboundId);
        Assert.Null(card.Type);
    }
}
