using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>De voorrangsregel (#150 → #270): de leidende bron schrijft
/// onvoorwaardelijk, een aanvullende vult alleen lege velden. Dit is de plek
/// waar die regel bewezen wordt — zonder per-veld-herkomstadministratie kan
/// hij alleen kloppen als hij hier strikt is.</summary>
public class CardMergeTests
{
    private static Card Existing() => new()
    {
        RiftboundId = "ogn-001-298",
        Name = "Darius, Trifarian",
        Type = "Unit",
        Rarity = "Common",
        TextPlain = "Overwhelm.",
        Domains = ["Fury"],
        Energy = 2,
        Illustrator = "Envar Studio",
        ImageWidth = 744,
        ImageHeight = 1039,
        ImageAltText = "Riftbound Unit: Darius, Trifarian. Overwhelm.",
        ImageColorPrimary = "#222c44",
        Flags = ["New"],
    };

    // ---- leidende bron ----------------------------------------------------

    [Fact]
    public void Leidend_OverschrijftEenGevuldVeldVanDeAanvulling()
    {
        var card = Existing();
        var riot = new Card
        {
            RiftboundId = card.RiftboundId,
            Name = "Darius, Trifarian",
            Illustrator = "Six More Vodka",
            ImageAltText = "Riftbound Unit: Darius, Trifarian. Overwhelm the enemy.",
            ImageColorPrimary = "#ffffff",
        };

        var changes = CardMerge.Apply(card, riot, leading: true);

        Assert.Equal("Six More Vodka", card.Illustrator);
        Assert.Equal("#ffffff", card.ImageColorPrimary);
        Assert.Equal("Riftbound Unit: Darius, Trifarian. Overwhelm the enemy.", card.ImageAltText);
        Assert.True(changes.Any);
    }

    [Fact]
    public void Leidend_SchrijftOokLegeWaardenDoor()
    {
        // Ontbreekt een veld in Riots payload, dan hééft de kaart het niet —
        // dat is informatie, geen gat. Anders blijft een ingetrokken
        // mightBonus of een verlopen "New"-vlag eeuwig staan.
        var card = Existing();
        card.MightBonus = 3;
        var riot = new Card { RiftboundId = card.RiftboundId, Name = card.Name };

        CardMerge.Apply(card, riot, leading: true);

        Assert.Null(card.MightBonus);
        Assert.Null(card.Illustrator);
        Assert.Empty(card.Flags);
    }

    // ---- aanvullende bron -------------------------------------------------

    [Fact]
    public void Aanvullend_RaaktEenGevuldVeldNooitAan()
    {
        var card = Existing();
        var riftcodex = new Card
        {
            RiftboundId = card.RiftboundId,
            // Precies de schade van vóór #150: streepjes-naam en andere
            // rariteit. Een aanvulling mag daar niet meer bij kunnen.
            Name = "Darius - Trifarian",
            Rarity = "Showcase",
            Type = "Spell",
            TextPlain = "Iets anders.",
            Domains = ["Order"],
            Energy = 9,
            Illustrator = "Iemand anders",
            ImageWidth = 1039,
            ImageHeight = 744,
            ImageAltText = "Andere alt-tekst.",
            ImageColorPrimary = "#000000",
            Flags = [],
        };

        var changes = CardMerge.Apply(card, riftcodex, leading: false);

        Assert.Equal("Darius, Trifarian", card.Name);
        Assert.Equal("Common", card.Rarity);
        Assert.Equal("Unit", card.Type);
        Assert.Equal("Overwhelm.", card.TextPlain);
        Assert.Equal(["Fury"], card.Domains);
        Assert.Equal(2, card.Energy);
        Assert.Equal("Envar Studio", card.Illustrator);
        Assert.Equal(744, card.ImageWidth);
        Assert.Equal(1039, card.ImageHeight);
        Assert.Equal("Riftbound Unit: Darius, Trifarian. Overwhelm.", card.ImageAltText);
        Assert.Equal("#222c44", card.ImageColorPrimary);
        Assert.Equal(["New"], card.Flags);
        Assert.False(changes.Any);
    }

    [Fact]
    public void Aanvullend_VultAlleenDeLegeVelden()
    {
        // Het echte gat: Riot kent geen supertype en geen kleuren voor élke
        // kaart. Wat leeg is mag een aanvulling invullen.
        var card = Existing();
        card.Supertype = null;
        card.ImageColorSecondary = null;
        card.MightBonus = null;

        var changes = CardMerge.Apply(card, new Card
        {
            RiftboundId = card.RiftboundId,
            Name = "Darius - Trifarian",
            Supertype = "Champion",
            ImageColorSecondary = "#dcebf9",
            MightBonus = 2,
        }, leading: false);

        Assert.Equal("Champion", card.Supertype);
        Assert.Equal("#dcebf9", card.ImageColorSecondary);
        Assert.Equal(2, card.MightBonus);
        Assert.Equal("Darius, Trifarian", card.Name); // gevuld blijft gevuld
        Assert.True(changes.Any);
    }

    [Fact]
    public void Aanvullend_LegeTekstVultGeenGat()
    {
        // Een bron die "" of witruimte levert vult niets in — het gat blijft
        // staan tot iemand een échte waarde heeft.
        var card = Existing();
        card.Illustrator = null;

        var changes = CardMerge.Apply(card, new Card
        {
            RiftboundId = card.RiftboundId, Name = card.Name, Illustrator = "   ",
        }, leading: false);

        Assert.Null(card.Illustrator);
        Assert.False(changes.Any);
    }

    // ---- wijzigingssignaal (churn-guard) ----------------------------------

    [Fact]
    public void Ongewijzigd_MeldtGeenWijziging()
    {
        // Elke sync draait over de hele set; zonder dit signaal zou UpdatedAt
        // van 1178 kaarten elke run opnieuw verspringen en de embedding-
        // pijplijn onnodig werk krijgen.
        var card = Existing();
        var same = Existing();

        var changes = CardMerge.Apply(card, same, leading: true);

        Assert.False(changes.Any);
        Assert.False(changes.NameChanged);
        Assert.False(changes.TextChanged);
    }

    [Fact]
    public void NaamEnTekstwijziging_WordenApartGemeld()
    {
        // De sync hangt de embedding-invalidatie en het legen van de
        // gelijkenis-uitleg aan precies deze twee signalen.
        var card = Existing();
        var changes = CardMerge.Apply(card, new Card
        {
            RiftboundId = card.RiftboundId,
            Name = "Darius, Hand of Noxus",
            TextPlain = "Overwhelm. Deathblow: draw a card.",
        }, leading: true);

        Assert.True(changes.NameChanged);
        Assert.True(changes.TextChanged);
    }

    [Fact]
    public void AlleenPresentatieWijziging_RaaktNaamEnTekstsignaalNiet()
    {
        // Een nieuwe illustrator of kleur mag geen her-embed uitlokken.
        var card = Existing();
        var changes = CardMerge.Apply(card, new Card
        {
            RiftboundId = card.RiftboundId,
            Name = card.Name,
            TextPlain = card.TextPlain,
            Illustrator = "Kudos Productions",
        }, leading: true);

        Assert.True(changes.Any);
        Assert.False(changes.NameChanged);
        Assert.False(changes.TextChanged);
    }

    // ---- afgeleiden blijven van hun eigen pijplijn ------------------------

    [Fact]
    public void RaaktOnzeEigenAfgeleidenNiet()
    {
        var card = Existing();
        card.Mechanics = ["Overwhelm"];
        card.VariantOf = "ogn-000-298";
        card.EmbeddingModel = EmbeddingConfig.Model;

        CardMerge.Apply(card, new Card
        {
            RiftboundId = card.RiftboundId, Name = card.Name, TextPlain = card.TextPlain,
        }, leading: true);

        Assert.Equal(["Overwhelm"], card.Mechanics);
        Assert.Equal("ogn-000-298", card.VariantOf);
        Assert.Equal(EmbeddingConfig.Model, card.EmbeddingModel);
    }
}
