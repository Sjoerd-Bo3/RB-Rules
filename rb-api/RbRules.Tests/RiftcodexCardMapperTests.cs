using System.Text.Json.Nodes;
using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Riftcodex-adapter (#144), getest op een echte API-snapshot
/// (Fixtures/riftcodex-cards-2026-07-13.json — fixtures spiegelen live data):
/// ster-id's worden de Riot-suffixvorm, streepjes-namen worden alleen mét
/// bewijs de komma-vorm, en bestaande Riot-namen winnen altijd.</summary>
public class RiftcodexCardMapperTests
{
    private static readonly IReadOnlyList<JsonObject> Fixture = LoadFixture();

    private static IReadOnlyList<JsonObject> LoadFixture()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "riftcodex-cards-2026-07-13.json")));
        return [.. ((JsonArray)json!["items"]!).OfType<JsonObject>()];
    }

    private static Card Map(string riftboundId)
    {
        var raw = Fixture.Single(c => c["riftbound_id"]!.GetValue<string>() == riftboundId);
        return RiftcodexCardMapper.MapCard(raw, "SFD")!;
    }

    [Fact]
    public void MapCard_NormalizesStarIdToRiotSuffixForm()
    {
        var card = Map("sfd-227*-221");
        Assert.Equal("sfd-227-star-221", card.RiftboundId);
        Assert.Equal("Ahri - Inquisitive (Signature)", card.Name); // naam pas via ResolveName
        Assert.Equal("SFD", card.SetId);
        Assert.Equal(227, card.CollectorNumber);
    }

    [Fact]
    public void MapCard_KeepsRegularIdsAndFieldsIntact()
    {
        var card = Map("sfd-057a-221");
        Assert.Equal("sfd-057a-221", card.RiftboundId);
        Assert.Equal("Irelia - Fervent (Alternate Art)", card.Name);
        Assert.Equal("Unit", card.Type);
        Assert.Equal("Spiritforged", card.SetLabel);
    }

    [Theory]
    // Bewijs aanwezig: de komma-basisnaam is al een bekende kaart.
    [InlineData("Soraka - Wanderer (Signature)", "Soraka, Wanderer", "Soraka, Wanderer (Signature)")]
    [InlineData("Ahri - Inquisitive (Signature)", "Ahri, Inquisitive", "Ahri, Inquisitive (Signature)")]
    [InlineData("Irelia - Fervent", "Irelia, Fervent", "Irelia, Fervent")]
    // Binnenwoordse streepjes ("Chem-Baroness") hebben geen spaties en blijven staan.
    [InlineData("Renata Glasc - Chem-Baroness (Overnumbered)", "Renata Glasc, Chem-Baroness",
        "Renata Glasc, Chem-Baroness (Overnumbered)")]
    public void NormalizeName_ConvertsDashToCommaWithEvidence(
        string riftcodex, string evidence, string expected) =>
        Assert.Equal(expected, RiftcodexCardMapper.NormalizeName(
            riftcodex, new HashSet<string> { evidence }));

    [Theory]
    // Zonder bewijs geen conversie: "Dark Child - Starter" is een écht
    // Riot-naampatroon (OGS-starterdecks), geen riftcodex-artefact.
    [InlineData("Dark Child - Starter")]
    [InlineData("Soraka - Wanderer (Signature)")]
    // Namen zonder separator blijven sowieso staan.
    [InlineData("Pakaa Cub")]
    public void NormalizeName_WithoutEvidenceKeepsTheSourceForm(string name) =>
        Assert.Equal(name, RiftcodexCardMapper.NormalizeName(
            name, new HashSet<string> { "Iets, Anders" }));

    [Fact]
    public void ResolveName_ExistingRiotNameWins()
    {
        // Conflict op hetzelfde (set, collector-nummer): de bestaande
        // Riot-gallery-naam wint — riftcodex vult alleen gaten.
        var name = RiftcodexCardMapper.ResolveName(
            "Irelia, Fervent", "Irelia - Fervent", new HashSet<string>());
        Assert.Equal("Irelia, Fervent", name);
    }

    [Fact]
    public void ResolveName_FillsGapsNormalizedWhenEvidenceExists()
    {
        var name = RiftcodexCardMapper.ResolveName(
            existingName: null, "Soraka - Wanderer (Signature)",
            new HashSet<string> { "Soraka, Wanderer" });
        Assert.Equal("Soraka, Wanderer (Signature)", name);
    }

    [Fact]
    public void ResolveName_ExistingDashNameAlsoWinsFromRiftcodex()
    {
        // De Riot-gallery kent échte streepjes-namen (OGS-starters, bv.
        // "Dark Child - Starter") die riftcodex anders noemt. Zou de sync
        // die overschrijven, dan zet de Riot-fallback ze weer terug:
        // naam-flip-flop per bronwissel. Een bestaande naam wint dus ALTIJD
        // van riftcodex — dash-artefacten van vóór de normalisatie herstelt
        // uitsluitend het bewijs-pad van RepairSourceFormsAsync.
        var name = RiftcodexCardMapper.ResolveName(
            "Dark Child - Starter", "Annie - Dark Child (Starter)",
            new HashSet<string> { "Annie, Dark Child" });
        Assert.Equal("Dark Child - Starter", name);
    }

    [Fact]
    public void CommaBaseNames_CollectsOnlyCommaFormsAsBaseNames()
    {
        var known = RiftcodexCardMapper.CommaBaseNames(
            ["Soraka, Wanderer", "Ahri, Inquisitive (Signature)", "Pakaa Cub", "Yone - Blademaster"]);
        Assert.Equal(["Ahri, Inquisitive", "Soraka, Wanderer"], known.Order().ToArray());
    }

    [Fact]
    public void MapCard_FixtureCoversTheRealSourceForms()
    {
        // De snapshot bevat de vormen waar #144 om draait: ster-id's,
        // alt-art-suffixen, overnumbered en promo's met streepjes-namen.
        var cards = Fixture.Select(c => RiftcodexCardMapper.MapCard(c, "SFD")!).ToList();
        Assert.Contains(cards, c => c.RiftboundId == "sfd-233-star-221");
        Assert.Contains(cards, c => c.RiftboundId == "sfd-249-221"
            && c.Name == "Renata Glasc - Chem-Baroness (Overnumbered)");
        Assert.Contains(cards, c => c.RiftboundId == "opp-010-024"
            && c.Name == "Annie - Stubborn");
        Assert.DoesNotContain(cards, c => c.RiftboundId.Contains('*'));
    }

    // ---- presentatievelden (#270) -----------------------------------------

    [Fact]
    public void MapCard_NeemtDePresentatievelenOverDieRiftcodexWelHeeft()
    {
        // Nagetrokken tegen de live API (en deze snapshot): anders dan de
        // aanname in #270 levert riftcodex artist, accessibility_text en de
        // "new"-vlag wél. Dat dekt de ~141 kaarten die alléén via riftcodex
        // binnenkomen (JDG-promo's).
        var card = Map("sfd-227*-221");
        Assert.Equal("Shawn Tan", card.Illustrator);
        Assert.Equal(
            "Riftbound Unit: Ahri, Inquisitive. When I attack or defend, give an enemy " +
            "unit here -2 [S] this turn, to a minimum of 1 [S].",
            card.ImageAltText);
        Assert.Empty(card.Flags);
    }

    [Fact]
    public void MapCard_LeidtAfmetingenUitDeImageUrlAf()
    {
        // Riftcodex wijst naar dezelfde Sanity-CDN, dus de maat staat in de
        // bestandsnaam — daarmee klopt de tegelverhouding ook voor kaarten
        // die Riot niet levert (#269).
        var card = Map("sfd-227*-221");
        Assert.Equal(744, card.ImageWidth);
        Assert.Equal(1039, card.ImageHeight);
        Assert.False(CardPresentation.IsLandscape(card.ImageWidth, card.ImageHeight));
    }

    [Fact]
    public void MapCard_ValtTerugOpOrientationAlsDeUrlGeenMaatDraagt()
    {
        var raw = (JsonObject)JsonNode.Parse("""
            {
              "riftbound_id": "jdg-001-024",
              "name": "Judge Promo",
              "classification": {"type": "Battlefield"},
              "media": {"image_url": "https://example.com/promo.png"},
              "orientation": "landscape"
            }
            """)!;
        var card = RiftcodexCardMapper.MapCard(raw, "JDG")!;
        Assert.True(CardPresentation.IsLandscape(card.ImageWidth, card.ImageHeight));
    }

    [Fact]
    public void MapCard_SteltAltTekstZelfSamenAlsRiftcodexErGeenHeeft()
    {
        // Afgeleid, uitsluitend voor alt= — nooit als officiële kaarttekst.
        var raw = (JsonObject)JsonNode.Parse("""
            {
              "riftbound_id": "jdg-002-024",
              "name": "Judge Promo",
              "classification": {"type": "Unit", "supertype": "Champion"},
              "text": {"plain": "Deal 1 :rb_might: damage."},
              "media": {"image_url": "https://example.com/promo-744x1039.png"},
              "new": true
            }
            """)!;
        var card = RiftcodexCardMapper.MapCard(raw, "JDG")!;
        Assert.Equal("Riftbound Champion Unit: Judge Promo. Deal 1 [might] damage.",
            card.ImageAltText);
        Assert.Equal(["New"], card.Flags);
    }

    [Fact]
    public void MapCard_LaatWatRiftcodexNietHeeftLeeg()
    {
        // Kleuren, mightBonus, effect en publicCode kent hun API niet —
        // die blijven leeg tot Riot ze levert (voorrangsregel #270).
        var card = Map("sfd-227*-221");
        Assert.Null(card.ImageColorPrimary);
        Assert.Null(card.ImageColorSecondary);
        Assert.Null(card.MightBonus);
        Assert.Null(card.EffectPlain);
        Assert.Null(card.PublicCode);
    }
}
