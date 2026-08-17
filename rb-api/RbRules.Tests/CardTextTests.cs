using Pgvector;
using RbRules.Domain;

namespace RbRules.Tests;

public class CardTextTests
{
    private static Card Sample() => new()
    {
        RiftboundId = "ogn-056-298",
        Name = "Adaptatron",
        Type = "Unit",
        Supertype = "Champion",
        Domains = ["Calm"],
        Energy = 4,
        Might = 3,
        TextPlain = "When I conquer, buff me.",
        Tags = ["Mech", "Piltover"],
    };

    [Fact]
    public void Compose_IncludesAllFacets()
    {
        var text = CardText.Compose(Sample());
        Assert.Contains("Adaptatron", text);
        Assert.Contains("Champion Unit", text);
        Assert.Contains("Domains: Calm", text);
        Assert.Contains("Energy 4", text);
        Assert.Contains("Might 3", text);
        Assert.Contains("When I conquer", text);
        Assert.Contains("Tags: Mech, Piltover", text);
    }

    [Fact]
    public void Compose_SkipsEmptyFacets()
    {
        var text = CardText.Compose(new Card { RiftboundId = "x", Name = "Kaal" });
        Assert.Equal("Kaal", text);
    }

    [Fact]
    public void AfgeleideAltTekstBlijftBuitenEmbeddingEnPrompt()
    {
        // Harde grens (#270): ImageAltText kan lokaal samengesteld zijn.
        // Afgeleid is niet officieel — het hoort in een alt= en mag dus nooit
        // in de embeddingtekst of een LLM-prompt terechtkomen.
        var card = Sample();
        card.ImageAltText = "Riftbound Champion Unit: Adaptatron. AFGELEIDE ALT-TEKST.";
        card.ImageColorPrimary = "#222c44";
        card.Illustrator = "Envar Studio";

        Assert.DoesNotContain("AFGELEIDE ALT-TEKST", CardText.Compose(card));
        Assert.DoesNotContain("AFGELEIDE ALT-TEKST", CardText.DescribeForPrompt(card));
        // Krediet en laadkleur zijn presentatie, geen kaartfeit voor het model.
        Assert.DoesNotContain("Envar Studio", CardText.Compose(card));
        Assert.DoesNotContain("#222c44", CardText.Compose(card));
    }

    [Fact]
    public void NeedsEmbedding_WhenMissing() =>
        Assert.True(CardText.NeedsEmbedding(Sample()));

    [Fact]
    public void NeedsEmbedding_WhenModelOutdated()
    {
        var card = Sample();
        card.Embedding = new Vector(new float[EmbeddingConfig.Dimensions]);
        card.EmbeddingModel = "oud-model";
        Assert.True(CardText.NeedsEmbedding(card));
    }

    [Fact]
    public void NeedsEmbedding_FalseWhenCurrent()
    {
        var card = Sample();
        card.Embedding = new Vector(new float[EmbeddingConfig.Dimensions]);
        card.EmbeddingModel = EmbeddingConfig.Model;
        Assert.False(CardText.NeedsEmbedding(card));
    }
}
