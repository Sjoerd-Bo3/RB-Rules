using RbRules.Domain.Ontology;
using RbRules.Domain.Reasoning;

namespace RbRules.Tests;

/// <summary>De publieke ontologie-projectie die de uitlegpagina voedt. De
/// waarde van die pagina staat of valt met één eigenschap: wat er staat komt
/// uit <see cref="OntologySchema"/> zelf, niet uit een overgetypte kopie. Deze
/// tests bewaken precies dat — voegt iemand een klasse of relatie toe, dan
/// verschijnt die vanzelf, en verdwijnt er niets stilletjes.</summary>
public class OntologyPublicViewTests
{
    [Fact]
    public void Projectie_DektAlleKlassenEnRelatiesUitHetSchema()
    {
        var view = OntologyPublicProjection.Current;

        Assert.Equal(OntologySchema.Classes.Count, view.Klassen.Count);
        Assert.Equal(OntologySchema.Relations.Count, view.Relaties.Count);
        Assert.Equal(OntologySchema.DisjointPairs.Count, view.DisjuncteParen.Count);
    }

    [Fact]
    public void Projectie_DraagtDeVastgelegdeOntologieVersie()
    {
        Assert.Equal(
            OntologyBaseline.Version.ToString(), OntologyPublicProjection.Current.Versie);
    }

    [Fact]
    public void Relatie_DraagtEdgeNaamDomeinRangeEnKardinaliteit()
    {
        var view = OntologyPublicProjection.Current;
        var hasDomain = Assert.Single(view.Relaties, r => r.Edge == "HAS_DOMAIN");

        // 1..* uit het schema: elke kaart heeft minstens één domein (Colorless = 1).
        Assert.Equal("1..*", hasDomain.Kardinaliteit);
        Assert.Contains("Card", hasDomain.Van);
        Assert.Contains("Domain", hasDomain.Naar);
        Assert.False(hasDomain.Gereificeerd);
    }

    [Fact]
    public void GekwalificeerdeRelatie_IsAlsGereificeerdGemarkeerd()
    {
        // COUNTERS draagt altijd condities en mag daarom nooit een kale edge
        // zijn — de lezer van de uitlegpagina hoort dat onderscheid te zien.
        var counters = Assert.Single(
            OntologyPublicProjection.Current.Relaties, r => r.Edge == "COUNTERS");

        Assert.True(counters.Gereificeerd);
    }

    [Fact]
    public void Inferenties_KomenUitDeGegenereerdeRegistryZonderCypher()
    {
        var view = OntologyPublicProjection.Current;

        Assert.Equal(InferenceRuleRegistry.All.Count, view.Inferenties.Count);
        Assert.All(view.Inferenties, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Edge));
            Assert.False(string.IsNullOrWhiteSpace(i.Omschrijving));
        });
        // De Cypher is uitvoeringsdetail en hoort niet in een publieke projectie.
        Assert.DoesNotContain(view.Inferenties, i => i.Omschrijving.Contains("MERGE"));
    }

    [Fact]
    public void DisjuncteParen_ZijnLeesbaarGeformatteerd()
    {
        Assert.Contains("Spell ⟂ Object", OntologyPublicProjection.Current.DisjuncteParen);
    }
}
