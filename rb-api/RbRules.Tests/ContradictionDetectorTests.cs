using RbRules.Domain.Ontology;
using RbRules.Domain.Reasoning;

namespace RbRules.Tests;

/// <summary>Redeneer-laag (fase 3, #227, §5): de PURE contradictie-patroon-
/// constructie (grotendeels uit de ontologie gegenereerd), de routering naar het
/// juiste kanaal en de vertaling van een treffer naar een <see cref="ReasoningConflict"/>-
/// rij (de "Conflict-rij → misvattingen/reviewqueue-koppeling"). Plus de
/// OWL2-RL-skeleton-zelftoets.</summary>
public class ContradictionDetectorTests
{
    // ── patroon-constructie ──────────────────────────────────────────────────

    [Fact]
    public void All_BevatDeVasteEnDeGegenereerdeDisjointnessPatronen()
    {
        var kinds = ContradictionDetector.All.Select(p => p.Kind).ToHashSet();
        Assert.Contains(ReasoningConflictKind.ClaimContradictsOfficial, kinds);
        Assert.Contains(ReasoningConflictKind.RulingCollision, kinds);
        Assert.Contains(ReasoningConflictKind.DisjointnessViolation, kinds);
    }

    [Fact]
    public void ClaimContradictsOfficial_IsBoundedMetNotExists()
    {
        // De bounded WHERE-NOT-EXISTS-guard: alleen een claim ZONDER officiële
        // dekking spreekt de regel echt tegen.
        Assert.Contains("NOT EXISTS", ContradictionDetector.ClaimContradictsOfficial.Cypher);
        Assert.Contains("RETURN", ContradictionDetector.ClaimContradictsOfficial.Cypher);
    }

    [Fact]
    public void DisjointnessPatterns_GegenereerdUitDeOntologie_VangenUnitSpell()
    {
        // Unit ⊑ Object, dus Unit ⟂ Spell — een :Unit:Spell-knoop is kaart-sync-
        // schade (à la #150). Canonieke labelvolgorde is alfabetisch (Spell < Unit).
        var patterns = ContradictionDetector.DisjointnessPatterns();
        Assert.Contains(patterns, p => p.Cypher.Contains("(n:Spell:Unit)"));
        // De gedeclareerde as Keyword ⟂ Mechanic hoort er ook bij.
        Assert.Contains(patterns, p => p.Cypher.Contains("(n:Keyword:Mechanic)"));
    }

    [Fact]
    public void DisjointnessPatterns_ZijnOngeordendGededupliceerd()
    {
        var patterns = ContradictionDetector.DisjointnessPatterns();
        var ids = patterns.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // A/B en B/A vallen samen: geen enkel paar verschijnt twee keer.
        Assert.All(patterns, p => Assert.StartsWith("disjointness:", p.Id));
    }

    [Fact]
    public void DisjointnessPatterns_DekkenAlleEffectieveDisjuncteParen()
    {
        var types = Enum.GetValues<EntityType>()
            .Where(OntologySchema.Classes.ContainsKey)
            .ToList();
        var expected = 0;
        var seen = new HashSet<string>();
        foreach (var a in types)
            foreach (var b in types)
            {
                if (a == b || !OntologySchema.AreDisjoint(a, b)) continue;
                var (x, y) = string.CompareOrdinal(a.ToString(), b.ToString()) < 0
                    ? (a.ToString(), b.ToString()) : (b.ToString(), a.ToString());
                if (seen.Add($"{x}|{y}")) expected++;
            }

        Assert.Equal(expected, ContradictionDetector.DisjointnessPatterns().Count);
    }

    // ── routering ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ReasoningConflictKind.ClaimContradictsOfficial, ConflictChannel.Misconception)]
    [InlineData(ReasoningConflictKind.RulingCollision, ConflictChannel.Escalation)]
    [InlineData(ReasoningConflictKind.DisjointnessViolation, ConflictChannel.ReviewQueue)]
    public void Router_RouteertElkSoortNaarZijnKanaal(string kind, ConflictChannel expected) =>
        Assert.Equal(expected, ConflictRouter.Route(kind));

    [Fact]
    public void Router_OnbekendSoort_GaatNaarMenselijkeOgen()
    {
        // Nooit stil laten vallen: default = reviewqueue.
        Assert.Equal(ConflictChannel.ReviewQueue, ConflictRouter.Route("iets-nieuws"));
    }

    [Fact]
    public void Patronen_KrijgenHunKanaalUitDeRouter()
    {
        foreach (var p in ContradictionDetector.All)
            Assert.Equal(ConflictRouter.Route(p.Kind), p.Channel);
    }

    // ── treffer → conflict-rij ───────────────────────────────────────────────

    [Fact]
    public void ToConflict_ClaimTegenOfficieel_LandtInMisvattingenKanaalMetProvenance()
    {
        var hit = new ContradictionHit(
            ContradictionDetector.ClaimContradictsOfficial,
            "claim:17", "section:core-rules-pdf/7.4",
            "Deflect prevents all damage in a showdown");

        var c = ContradictionDetector.ToConflict(hit, "RUN9");

        Assert.Equal("claim-contradicts-official", c.PatternId);
        Assert.Equal(ReasoningConflictKind.ClaimContradictsOfficial, c.Kind);
        Assert.Equal("misconception", c.Channel);            // ConflictRouter → misvattingen-kanaal
        Assert.Equal("claim:17", c.SubjectRef);
        Assert.Equal("section:core-rules-pdf/7.4", c.CounterRef);
        Assert.Equal("RUN9", c.RunId);                       // provenance
        Assert.Equal(ReasoningConflictStatus.Open, c.Status);
    }

    [Fact]
    public void ToConflict_EnkelKnoopTegenspraak_HeeftGeenCounterRef()
    {
        var pattern = ContradictionDetector.DisjointnessPatterns()[0];
        var c = ContradictionDetector.ToConflict(
            new ContradictionHit(pattern, "card:ogn-011-298", null, "Spell + Unit"), "RUN1");

        Assert.Null(c.CounterRef);
        Assert.Equal("reviewqueue", c.Channel);
    }

    [Fact]
    public void ToConflict_DedupeKey_IsStabielVoorHetzelfdeParen()
    {
        var hit = new ContradictionHit(
            ContradictionDetector.RulingCollision, "ruling:1", "ruling:2", "botsing");
        var a = ContradictionDetector.ToConflict(hit, "RUN_A");
        var b = ContradictionDetector.ToConflict(hit, "RUN_B");

        // Dezelfde tegenspraak in twee runs → dezelfde sleutel (idempotent, geen
        // tweede rij), ondanks verschillende run-herkomst.
        Assert.Equal(a.DedupeKey, b.DedupeKey);
        Assert.Equal(ReasoningConflictDedupe.Key("ruling-collision", "ruling:1", "ruling:2"), a.DedupeKey);
    }

    // ── OWL2-RL-nachtaudit-skeleton ──────────────────────────────────────────

    [Fact]
    public void OntologyConsistencyAudit_DeAfgedwongenSchemaBronIsConsistent()
    {
        // De pure zelf-toets tegen OntologySchema: acyclisch, disjointness
        // vervulbaar, geen dangling domain/range. Consistent ⇒ geen bevindingen.
        //
        // Dit IS sinds #258 de gate. Er stond dezelfde toets ook als admin-job
        // ("owlaudit"), maar die las geen data en raakte geen database: hij kon
        // alleen falen op de gecompileerde OntologySchema en gaf dus pas ná de
        // merge — en alleen als iemand op de knop drukte — antwoord op een vraag
        // die de compiler-plus-CI hier vóór de merge beantwoordt. De job is weg;
        // deze assert houdt een inconsistent schema tegen.
        Assert.Empty(OntologyConsistencyAudit.Run());
    }

    // De drie detectie-takken tegen een VERZONNEN inconsistent schema (#227-review,
    // finding #3): tegen de statische, per-constructie-consistente OntologySchema
    // vuurt geen tak ooit — daarom voedt elke test hieronder de pure check expliciet
    // een inconsistente invoer, zodat de tak zelf bewezen kan afgaan.

    [Fact]
    public void OntologyConsistencyAudit_VangtEenSubclassCyclus()
    {
        // Ancestors is strikt-exclusief van zichzelf, dus tegen een geldige DAG is
        // deze tak dood; injecteer een voorouder-verzameling die het type zelf bevat.
        var findings = OntologyConsistencyAudit.CheckAcyclicHierarchy(
            [EntityType.Unit],
            _ => [EntityType.Unit]);                 // Unit ∈ voorouders(Unit) → cyclus

        var f = Assert.Single(findings);
        Assert.Equal("subclass-cycle", f.Code);
    }

    [Fact]
    public void OntologyConsistencyAudit_VangtEenOnvervulbareKlasse()
    {
        // Een type dat subklasse is van BEIDE kanten van een disjunct paar.
        var findings = OntologyConsistencyAudit.CheckDisjointnessSatisfiable(
            [EntityType.Unit],
            [(EntityType.Object, EntityType.Spell)],
            (_, _) => true);                         // Unit ⊑ zowel Object als Spell

        var f = Assert.Single(findings);
        Assert.Equal("unsatisfiable-class", f.Code);
    }

    [Fact]
    public void OntologyConsistencyAudit_VangtDanglingDomainEnRange()
    {
        // Een relatie met een niet-geregistreerd domein- én range-type.
        var rel = new OntologyRelation(
            RelationType.RelatesTo, "X_REL",
            Domain: [EntityType.Unit], Range: [EntityType.Spell],
            MinCardinality: 0, MaxCardinality: null,
            Traits: RelationTraits.None, Parameters: []);

        var findings = OntologyConsistencyAudit.CheckRelationDomainsRegistered(
            [rel], _ => false);                      // niets geregistreerd

        Assert.Contains(findings, x => x.Code == "dangling-domain");
        Assert.Contains(findings, x => x.Code == "dangling-range");
    }
}
