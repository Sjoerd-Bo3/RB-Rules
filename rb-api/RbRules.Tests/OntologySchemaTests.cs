using RbRules.Domain.Ontology;

namespace RbRules.Tests;

/// <summary>Ontologie-schema v0 (brein-fundament): de deterministische
/// schema-poort <see cref="OntologyValidationService"/> en de afleidingen op
/// <see cref="OntologySchema"/>. Dekt domain/range mét subclass-polymorfie,
/// disjointness, kardinaliteit, de reïficatie-dwang en de transitieve/
/// acyclische eigenschappen — plus de interne consistentie van het register
/// zelf (de ÉNE bron waaruit later TBox/prompt-enums/constraints komen).</summary>
public class OntologySchemaTests
{
    // ── Domain / range ───────────────────────────────────────────────────────

    [Fact]
    public void ValidateTriple_DomeinEnRangeKloppen_IsGeldig()
    {
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Keyword, RelationType.Invokes, EntityType.Mechanic);

        Assert.True(r.IsValid);
        Assert.Null(r.Reason);
        Assert.Empty(r.Violations);
    }

    [Fact]
    public void ValidateTriple_SubjectBuitenDomein_GeeftDomainMismatch()
    {
        // INVOKES verlangt Keyword→Mechanic; een Mechanic als subject valt buiten.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Mechanic, RelationType.Invokes, EntityType.Mechanic);

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.DomainMismatch);
    }

    [Fact]
    public void ValidateTriple_ObjectBuitenRange_GeeftRangeMismatch()
    {
        // HAS_KEYWORD verlangt Card→Keyword; een Mechanic als object valt buiten.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Unit, RelationType.HasKeyword, EntityType.Mechanic);

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.RangeMismatch);
    }

    // ── Subclass-polymorfie ──────────────────────────────────────────────────

    [Fact]
    public void ValidateTriple_UnitVoldoetAanCardDomein_IsGeldig()
    {
        // IN_DOMAIN heeft domein Card; een Unit (⊑ Card) moet voldoen zonder join.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Unit, RelationType.InDomain, EntityType.Domain);

        Assert.True(r.IsValid);
    }

    [Fact]
    public void ValidateTriple_UnitVoldoetAanObjectDomein_IsGeldig()
    {
        // HAS_STATUS heeft domein Object; een Unit is via multi-parent ook Object.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Unit, RelationType.HasStatus, EntityType.Status);

        Assert.True(r.IsValid);
    }

    [Fact]
    public void ValidateTriple_SpellIsGeenObject_FaaltOpObjectDomein()
    {
        // Spell is een Card maar DISJUNCT van Object → voldoet niet aan HAS_STATUS.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Spell, RelationType.HasStatus, EntityType.Status);

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.DomainMismatch);
    }

    [Fact]
    public void IsA_IsReflexiefEnTransitief()
    {
        Assert.True(OntologySchema.IsA(EntityType.Unit, EntityType.Unit));   // reflexief
        Assert.True(OntologySchema.IsA(EntityType.Unit, EntityType.Card));   // direct
        Assert.True(OntologySchema.IsA(EntityType.Unit, EntityType.Object)); // multi-parent
        Assert.True(OntologySchema.IsA(EntityType.Unit, EntityType.Thing));  // transitief
        Assert.False(OntologySchema.IsA(EntityType.Spell, EntityType.Object));
    }

    // ── Disjointness ─────────────────────────────────────────────────────────

    [Fact]
    public void ValidateEntityLabels_KeywordEnMechanicSamen_IsDisjointnessSchending()
    {
        var r = OntologyValidationService.ValidateEntityLabels(
            [EntityType.Keyword, EntityType.Mechanic]);

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.Disjointness);
    }

    [Fact]
    public void ValidateEntityLabels_UnitEnSpellSamen_ErftDisjointnessViaObject()
    {
        // Unit ⊑ Object en Object ⟂ Spell → Unit ⟂ Spell (overerving telt mee).
        var r = OntologyValidationService.ValidateEntityLabels(
            [EntityType.Unit, EntityType.Spell]);

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.Disjointness);
    }

    [Fact]
    public void ValidateEntityLabels_GeldigMultiLabel_IsGeldig()
    {
        // De canonieke object-kaart labels: :Object:Card:Unit — geen disjointness.
        var r = OntologyValidationService.ValidateEntityLabels(
            [EntityType.Object, EntityType.Card, EntityType.Unit]);

        Assert.True(r.IsValid);
    }

    [Fact]
    public void AreDisjoint_DekkAlleGedeclareerdeAssen()
    {
        Assert.True(OntologySchema.AreDisjoint(EntityType.Keyword, EntityType.Mechanic));
        Assert.True(OntologySchema.AreDisjoint(EntityType.Keyword, EntityType.Status));
        Assert.True(OntologySchema.AreDisjoint(EntityType.Mechanic, EntityType.Status));
        Assert.True(OntologySchema.AreDisjoint(EntityType.Spell, EntityType.Object));
        Assert.False(OntologySchema.AreDisjoint(EntityType.Keyword, EntityType.Zone));
    }

    // ── Kardinaliteit ────────────────────────────────────────────────────────

    [Fact]
    public void ValidateTriple_ErrataOfEerste_IsGeldig()
    {
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Erratum, RelationType.ErrataOf, EntityType.Card);

        Assert.True(r.IsValid);
    }

    [Fact]
    public void ValidateTriple_ErrataOfTweede_SchendtFunctioneel()
    {
        // ERRATA_OF is functioneel (precies 1): een tweede uitgaande edge vanuit
        // hetzelfde Erratum overschrijdt de bovengrens.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Erratum, RelationType.ErrataOf, EntityType.Card,
            new TripleContext(ExistingOutgoingCount: 1));

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.CardinalityExceeded);
    }

    [Fact]
    public void ValidateTriple_IntroducedInTweede_SchendtFunctioneel()
    {
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Card, RelationType.IntroducedIn, EntityType.Set,
            new TripleContext(ExistingOutgoingCount: 1));

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.CardinalityExceeded);
    }

    [Fact]
    public void ValidateTriple_HasKeywordMeervoudig_IsGeldig()
    {
        // HAS_KEYWORD is 0..* — een tweede edge is prima.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Unit, RelationType.HasKeyword, EntityType.Keyword,
            new TripleContext(ExistingOutgoingCount: 3));

        Assert.True(r.IsValid);
    }

    // ── Reïficatie-dwang ─────────────────────────────────────────────────────

    [Fact]
    public void ValidateTriple_CountersAlsKaleEdge_WordtAfgekeurd()
    {
        // COUNTERS is gekwalificeerd → verboden als kale edge, moet via Interaction.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Keyword, RelationType.Counters, EntityType.Keyword);

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.ReificationRequired);
    }

    [Theory]
    [InlineData(RelationType.Counters)]
    [InlineData(RelationType.Modifies)]
    [InlineData(RelationType.Grants)]
    [InlineData(RelationType.Requires)]
    public void ValidateTriple_GekwalificeerdeRelatieKaal_WordtAfgekeurd(RelationType relation)
    {
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Card, relation, EntityType.Card);

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.ReificationRequired);
    }

    [Fact]
    public void ValidateTriple_CountersGereïficeerd_IsGeldig()
    {
        // Dezelfde relatie is wél toegestaan als ze via een Interaction is gereïficeerd.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Keyword, RelationType.Counters, EntityType.Keyword,
            new TripleContext(Reified: true));

        Assert.True(r.IsValid);
    }

    // ── Transitieve / acyclische eigenschappen ───────────────────────────────

    [Fact]
    public void ValidateTriple_SubclassOfDieCyclusSluit_WordtAfgekeurd()
    {
        // Unit ⊑ Object geldt al; Object ⊑ Unit beweren zou een cyclus sluiten.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Object, RelationType.SubclassOf, EntityType.Unit);

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.SubclassCycle);
    }

    [Fact]
    public void ValidateTriple_SubclassOfSpellObject_SchendtDisjointness()
    {
        // Spell ⊑ Object zou de bindende disjointness Spell ⟂ Object breken.
        var r = OntologyValidationService.ValidateTriple(
            EntityType.Spell, RelationType.SubclassOf, EntityType.Object);

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.Disjointness);
    }

    [Fact]
    public void RelatieEigenschappen_TransitiefAcyclischSymmetrischFunctioneel()
    {
        Assert.True(OntologySchema.Relations[RelationType.Supersedes].Traits
            .HasFlag(RelationTraits.Transitive));
        Assert.True(OntologySchema.Relations[RelationType.Supersedes].Traits
            .HasFlag(RelationTraits.Acyclic));
        Assert.True(OntologySchema.Relations[RelationType.SubclassOf].Traits
            .HasFlag(RelationTraits.Transitive | RelationTraits.Acyclic));
        Assert.True(OntologySchema.Relations[RelationType.Contradicts].Traits
            .HasFlag(RelationTraits.Symmetric));
        Assert.True(OntologySchema.Relations[RelationType.ErrataOf].IsFunctional);
        Assert.True(OntologySchema.Relations[RelationType.IntroducedIn].IsFunctional);
    }

    // ── String-poort (rauwe LLM-output) ──────────────────────────────────────

    [Fact]
    public void ValidateTriple_StringVariantMetEdgeNaam_ResolvedNamenGeenUnknown()
    {
        var r = OntologyValidationService.ValidateTriple("keyword", "HAS_KEYWORD", "Keyword");

        // "keyword" HAS_KEYWORD "Keyword": Keyword is geen Card → range/domain faalt,
        // maar de namen resolven wél (geen Unknown*).
        Assert.DoesNotContain(r.Violations, v =>
            v.Code is OntologyViolationCode.UnknownEntityType or OntologyViolationCode.UnknownRelation);
    }

    [Fact]
    public void ValidateTriple_StringVariantGeldigeTriple_IsGeldig()
    {
        var r = OntologyValidationService.ValidateTriple("Unit", "IN_DOMAIN", "Domain");
        Assert.True(r.IsValid);
    }

    [Fact]
    public void ValidateTriple_OnbekendType_GeeftUnknownEntityType()
    {
        var r = OntologyValidationService.ValidateTriple("Sorcery", "HAS_KEYWORD", "Keyword");

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.UnknownEntityType);
    }

    [Fact]
    public void ValidateTriple_OnbekendeRelatie_GeeftUnknownRelation()
    {
        var r = OntologyValidationService.ValidateTriple("Card", "DESTROYS", "Card");

        Assert.False(r.IsValid);
        Assert.Contains(r.Violations, v => v.Code == OntologyViolationCode.UnknownRelation);
    }

    // ── Register-integriteit (de ÉNE bron) ───────────────────────────────────

    [Fact]
    public void Schema_KlassenhiërarchieIsAcyclisch()
    {
        // Ancestors termineert alleen als de hiërarchie acyclisch is; check per klasse
        // dat een type niet zijn eigen voorouder is.
        foreach (var type in Enum.GetValues<EntityType>())
        {
            Assert.True(OntologySchema.Classes.ContainsKey(type), $"{type} ontbreekt in het register.");
            Assert.DoesNotContain(type, OntologySchema.Ancestors(type));
        }
    }

    [Fact]
    public void Schema_AlleDirecteOudersBestaanEnLopenNaarThing()
    {
        foreach (var cls in OntologySchema.Classes.Values)
        {
            foreach (var parent in cls.DirectParents)
                Assert.True(OntologySchema.Classes.ContainsKey(parent),
                    $"Ouder {parent} van {cls.Type} ontbreekt.");
            if (cls.Type != EntityType.Thing)
                Assert.True(OntologySchema.IsA(cls.Type, EntityType.Thing),
                    $"{cls.Type} is niet verbonden met Thing.");
        }
    }

    [Fact]
    public void Schema_GedeclareerdeDisjointeParenZijnGeenSubklasseRelaties()
    {
        // Een disjoint paar mag geen subklasse-relatie hebben, anders is de klasse
        // onvervulbaar (Spell ⟂ Object mag niet samengaan met Spell ⊑ Object).
        foreach (var (a, b) in OntologySchema.DisjointPairs)
        {
            Assert.False(OntologySchema.IsA(a, b), $"{a} is subklasse van disjuncte {b}.");
            Assert.False(OntologySchema.IsA(b, a), $"{b} is subklasse van disjuncte {a}.");
        }
    }

    [Fact]
    public void Schema_HasKeywordBehoudtMagnitudeParameter()
    {
        // De magnitude-qualifier mag niet weggestript worden (Tank N, Accelerate N).
        Assert.Contains("magnitude", OntologySchema.Relations[RelationType.HasKeyword].Parameters);
    }

    [Fact]
    public void Schema_AlleRelatiesHebbenUniekeEdgeNaam()
    {
        var names = OntologySchema.Relations.Values.Select(r => r.EdgeName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Schema_NormativeSourceTakDraagtAuthorityRankClaimNiet()
    {
        // Kennispiramide type-afdwingbaar: RuleSection/Ruling/Erratum ⊑ NormativeSource,
        // Claim staat er bewust buiten.
        Assert.True(OntologySchema.IsA(EntityType.Ruling, EntityType.NormativeSource));
        Assert.True(OntologySchema.IsA(EntityType.Erratum, EntityType.NormativeSource));
        Assert.False(OntologySchema.IsA(EntityType.Claim, EntityType.NormativeSource));
    }
}
