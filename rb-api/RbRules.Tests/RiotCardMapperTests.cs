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

    // ---- presentatievelden (#257/#269/#270) -------------------------------

    /// <summary>Letterlijke vorm van een battlefield uit de live gallery-JSON
    /// (unl-205-219): liggend, met alle presentatievelden die Riot meelevert
    /// maar die we tot #270 weggooiden.</summary>
    private const string LandscapeBattlefield = """
        {
          "id": "unl-205-219",
          "collectorNumber": 205,
          "name": "Abandoned Hall",
          "publicCode": "UNL-205/219",
          "set": {"label": "Card Set", "value": {"id": "UNL", "label": "Unleashed"}},
          "cardType": {"label": "Card Type", "type": [{"id": "battlefield", "label": "Battlefield"}]},
          "rarity": {"label": "Rarity", "value": {"id": "uncommon", "label": "Uncommon"}},
          "domain": {"label": "Domain", "values": [{"id": "colorless", "label": "Colorless"}]},
          "cardImage": {
            "type": "image",
            "url": "https://cmsassets.rgpub.io/sanity/images/dsfx7636/game_data_live/7447b04d1e78192509e89e5ff3556368ea5c471a-1039x744.png?accountingTag=RB",
            "accessibilityText": "Riftbound Battlefield: Abandoned Hall. When a player plays a spell, they may give a unit they control here +1 [S] this turn.",
            "dimensions": {"width": 1039, "height": 744, "aspectRatio": 1.396505376344086},
            "colors": {"primary": "#222C44", "secondary": "#DCEBF9", "label": "#060B18"},
            "mimeType": "image/png"
          },
          "orientation": "landscape",
          "illustrator": {"label": "Artist", "values": [{"id": "envarstudio", "label": "Envar Studio"}]},
          "flags": [{"id": "new", "label": "New"}],
          "mightBonus": {"label": "Might Bonus", "value": {"id": 3, "label": "+3"}},
          "effect": {"label": "Effect", "richText": {"type": "html", "body": "<p>At the end of your turn, unattach this.</p>"}},
          "text": {"label": "Ability", "richText": {"type": "html", "body": "<p>When a player plays a spell, they may give a unit +1 :rb_might: this turn.</p>"}}
        }
        """;

    [Fact]
    public void MapCard_MapsPresentationFields()
    {
        var card = RiotCardMapper.MapCard((JsonObject)JsonNode.Parse(LandscapeBattlefield)!);
        Assert.Equal("UNL-205/219", card.PublicCode);
        Assert.Equal("Envar Studio", card.Illustrator);
        Assert.Equal(3, card.MightBonus);
        Assert.Equal("At the end of your turn, unattach this.", card.EffectPlain);
        Assert.Equal(["New"], card.Flags);
        Assert.Equal("#222c44", card.ImageColorPrimary);
        Assert.Equal("#dcebf9", card.ImageColorSecondary);
        Assert.StartsWith("Riftbound Battlefield: Abandoned Hall.", card.ImageAltText);
    }

    [Fact]
    public void MapCard_BattlefieldBlijftLiggend()
    {
        // Regressie #269: zonder afmetingen kreeg elke tegel 744/1039 met
        // object-fit: cover, en werden de 66 battlefields bijgesneden.
        var card = RiotCardMapper.MapCard((JsonObject)JsonNode.Parse(LandscapeBattlefield)!);
        Assert.Equal(1039, card.ImageWidth);
        Assert.Equal(744, card.ImageHeight);
        Assert.True(CardPresentation.IsLandscape(card.ImageWidth, card.ImageHeight));
    }

    [Fact]
    public void MapCard_LeidtAfmetingenUitDeUrlAfAlsDimensionsOntbreken()
    {
        var json = (JsonObject)JsonNode.Parse(LandscapeBattlefield)!;
        ((JsonObject)json["cardImage"]!).Remove("dimensions");
        var card = RiotCardMapper.MapCard(json);
        Assert.Equal(1039, card.ImageWidth);
        Assert.Equal(744, card.ImageHeight);
    }

    [Fact]
    public void MapCard_SteltAltTekstZelfSamenAlsRiotErGeenLevert()
    {
        // Afgeleid, uitsluitend voor alt= — nooit als officiële kaarttekst.
        var json = (JsonObject)JsonNode.Parse(LandscapeBattlefield)!;
        ((JsonObject)json["cardImage"]!).Remove("accessibilityText");
        var card = RiotCardMapper.MapCard(json);
        Assert.Equal(
            "Riftbound Battlefield: Abandoned Hall. When a player plays a spell, " +
            "they may give a unit +1 [might] this turn.",
            card.ImageAltText);
    }

    [Fact]
    public void MapCard_ZonderPresentatievelden_LaatZeLeeg()
    {
        // 1084 van de 1178 kaarten hebben geen flags, 1138 geen mightBonus:
        // afwezig betekent "heeft het niet", niet "onbekend".
        var card = RiotCardMapper.MapCard((JsonObject)JsonNode.Parse(SampleCard)!);
        Assert.Empty(card.Flags);
        Assert.Null(card.MightBonus);
        Assert.Null(card.EffectPlain);
        Assert.Null(card.PublicCode);
        Assert.Null(card.ImageColorPrimary);
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
