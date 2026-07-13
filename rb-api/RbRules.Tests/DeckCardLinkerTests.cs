using RbRules.Domain;

namespace RbRules.Tests;

/// <summary>Koppeling PA-variantnummer → canoniek riftbound_id (#15), langs
/// dezelfde variantgroepering als de rest van het systeem (#54/#57): een
/// deck-verwijzing naar een alt-art telt op de canonieke kaart.</summary>
public class DeckCardLinkerTests
{
    // Mini-snapshot in de echte id-vormen: basisprinting, alt-art (variant),
    // star-printing, token zonder setgrootte-staart.
    private static DeckCardLinker Linker() => new([
        Card("ogn-126-298", "Body Rune"),
        Card("ogn-126a-298", "Body Rune (Alternate Art)", variantOf: "ogn-126-298"),
        Card("unl-203-219", "Poppy, Keeper of the Hammer"),
        Card("unl-233-star-219", "Jinx, Loose Cannon (Overnumbered)", variantOf: "unl-233-219"),
        Card("unl-233-219", "Jinx, Loose Cannon"),
        Card("unl-t07", "Training Dummy"),
    ]);

    [Fact]
    public void PrintingCode_StriptDeSetgrootte()
    {
        Assert.Equal("ogn-126a", DeckCardLinker.PrintingCode("ogn-126a-298"));
        Assert.Equal("unl-233-star", DeckCardLinker.PrintingCode("unl-233-star-219"));
        // Token-ids hebben geen numerieke staart en zijn al een code.
        Assert.Equal("unl-t07", DeckCardLinker.PrintingCode("unl-t07"));
    }

    [Fact]
    public void ResolveCanonical_ExactVariantnummer_GeeftDeCanoniekeKaart()
    {
        var linker = Linker();
        // Basisprinting → zichzelf; alt-art → zijn canonieke basiskaart.
        Assert.Equal("ogn-126-298", linker.ResolveCanonical("OGN-126", "Body Rune"));
        Assert.Equal("ogn-126-298", linker.ResolveCanonical("OGN-126a", "Body Rune"));
        Assert.Equal("unl-233-219", linker.ResolveCanonical("UNL-233-star", null));
    }

    [Fact]
    public void ResolveCanonical_OnbekendeAltArt_ValtTerugOpDeBasisprinting()
    {
        // Een alt-art die de gallery (nog) niet kent ("OGN-203b" bestaat hier
        // niet) resolvet via het basisnummer.
        var linker = Linker();
        Assert.Equal("unl-203-219", linker.ResolveCanonical("UNL-203b", null));
    }

    [Fact]
    public void ResolveCanonical_OnbekendNummer_ValtTerugOpDeNaam()
    {
        var linker = Linker();
        // PA-namen dragen soms een printing-suffix — BaseName-groepering.
        Assert.Equal("unl-203-219", linker.ResolveCanonical(
            "XXX-999", "Poppy, Keeper of the Hammer (Alternate Art)"));
    }

    [Fact]
    public void ResolveCanonical_EchtOnbekend_GeeftNull_GeenCrash()
    {
        var linker = Linker();
        Assert.Null(linker.ResolveCanonical("ZZZ-001", "Toekomstige Kaart"));
        Assert.Null(linker.ResolveCanonical(null, null));
        Assert.Null(linker.ResolveCanonical(" ", ""));
    }

    private static Card Card(string id, string name, string? variantOf = null) =>
        new() { RiftboundId = id, Name = name, VariantOf = variantOf };
}
