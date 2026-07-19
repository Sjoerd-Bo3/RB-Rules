using RbRules.Domain;
using RbRules.Domain.Ontology;

namespace RbRules.Tests;

/// <summary>Fase live-graph (#227, §3.5) — de pure brein-projectie-rij-bouwer
/// (<see cref="BrainProjection"/>). Dekt labels/props/idempotentie-sleutels per
/// node-/edge-type, de eigen ref-namespace (collision-vrij met <see cref="BrainRef"/>),
/// dictionaries-only-params, de determinisme-garantie (spiegel van Postgres) en de
/// scope-keuzes (rejected predicaat overslaan, dangling merge/subject → knoop zonder
/// edge, ontologie-versie-ordening + current-vlag + PRECEDES-keten).</summary>
public class BrainProjectionTests
{
    private static CanonicalEntity Entity(
        long id, string kind, string label, string[]? alt = null,
        string status = CanonicalEntityStatus.Canonical, long? mergedInto = null,
        string? definition = null, string run = "RUN01") =>
        new()
        {
            Id = id, Kind = kind, CanonicalLabel = label, AltLabels = alt ?? [],
            Status = status, MergedIntoId = mergedInto, Definition = definition,
            CreatedByRunId = run,
        };

    private static MechanicPredicateAssertion Predicate(
        long id, long subject, string predicate, string token,
        string status = MechanicPredicateStatus.Reviewed, string run = "RUN01") =>
        new()
        {
            Id = id, SubjectEntityId = subject, Predicate = predicate, ObjectToken = token,
            Status = status, CreatedByRunId = run,
        };

    private static OntologyVersionRecord Version(
        long id, string version, string bump = "minor", string run = "RUN01",
        DateTimeOffset? appliedAt = null) =>
        new()
        {
            Id = id, Version = version, Fingerprint = $"fp-{version}", BumpKind = bump,
            Notes = $"notes-{version}", RunId = run,
            AppliedAt = appliedAt ?? DateTimeOffset.UtcNow,
        };

    // ── CanonicalEntity ──────────────────────────────────────────────────────

    [Fact]
    public void CanonicalEntity_Row_CarriesLabelsPropsAndOwnRefKey()
    {
        var rows = BrainProjection.Build(
            [Entity(7, CanonicalEntityKinds.Mechanic, "Assault", alt: ["Assault 2", "Assaulting"],
                definition: "deal extra damage")],
            [], []);

        var e = Assert.Single(rows.CanonicalEntities);
        // Eigen ref-namespace — NIET de mechanic:-BrainRef (die als property meerijdt).
        Assert.Equal("entity:7", e["ref"]);
        Assert.Equal("mechanic:Assault", e["brainRef"]);
        Assert.Equal("mechanic", e["kind"]);
        Assert.Equal("Assault", e["canonicalLabel"]);
        Assert.Equal("canonical", e["status"]);
        Assert.Equal("deal extra damage", e["definition"]);
        Assert.Equal("RUN01", e["createdByRun"]);
        // Alt-labels als List (de driver serialiseert geen array-in-collectie zoals
        // een anonymous type — dictionaries-only + List is de huis-conventie).
        var alt = Assert.IsType<List<string>>(e["altLabels"]);
        Assert.Equal(["Assault 2", "Assaulting"], alt);
    }

    [Fact]
    public void CanonicalEntity_Ref_NeverCollidesWithBrainRefAlphabet()
    {
        var rows = BrainProjection.Build(
            [Entity(1, CanonicalEntityKinds.Mechanic, "Deflect")], [], []);
        var refValue = (string)rows.CanonicalEntities[0]["ref"]!;
        // De eigen prefix mag GEEN geldige BrainRef zijn — anders wordt een
        // label-loze DERIVED_FROM/RELATES_TO-match in GraphSyncService ambigu.
        Assert.StartsWith("entity:", refValue);
        Assert.False(BrainRef.TryParse(refValue, out _));
    }

    [Fact]
    public void MergedEntity_ProjectsMergedIntoEdge_BetweenEntityRefs()
    {
        var rows = BrainProjection.Build(
            [
                Entity(1, CanonicalEntityKinds.Mechanic, "Deflect"),
                Entity(2, CanonicalEntityKinds.Mechanic, "Deflecting",
                    status: CanonicalEntityStatus.Merged, mergedInto: 1),
            ], [], []);

        var edge = Assert.Single(rows.MergedIntoEdges);
        Assert.Equal("entity:2", edge["from"]);
        Assert.Equal("entity:1", edge["to"]);
        // De tombstone blijft óók als knoop bestaan (herstelpad-historie).
        Assert.Equal(2, rows.CanonicalEntities.Count);
    }

    [Fact]
    public void MergedEntity_DanglingTarget_ProducesNoEdge()
    {
        // Doel-entiteit niet in de projectie → knoop zonder edge (stille mis-match,
        // zelfde gedrag als een niet-matchende ABOUT in GraphSyncService).
        var rows = BrainProjection.Build(
            [Entity(2, CanonicalEntityKinds.Mechanic, "Deflecting",
                status: CanonicalEntityStatus.Merged, mergedInto: 99)],
            [], []);
        Assert.Empty(rows.MergedIntoEdges);
        Assert.Single(rows.CanonicalEntities);
    }

    // ── MechanicPredicate ────────────────────────────────────────────────────

    [Fact]
    public void Predicate_Reviewed_ProjectsNodeAndHasPredicateEdge()
    {
        var rows = BrainProjection.Build(
            [Entity(5, CanonicalEntityKinds.Mechanic, "Accelerate")],
            [Predicate(11, subject: 5, "prevents", "exhaust")],
            []);

        var p = Assert.Single(rows.Predicates);
        Assert.Equal("predicate:11", p["ref"]);
        Assert.Equal("prevents", p["predicate"]);
        Assert.Equal("exhaust", p["objectToken"]);
        Assert.Equal("reviewed", p["status"]);
        Assert.Equal("RUN01", p["createdByRun"]);

        var edge = Assert.Single(rows.HasPredicateEdges);
        Assert.Equal("entity:5", edge["entity"]);
        Assert.Equal("predicate:11", edge["predicate"]);
    }

    [Fact]
    public void Predicate_Rejected_IsSkipped()
    {
        // Rejected voedt de motor niet en is geen knoop (audit-spoor blijft in Postgres).
        var rows = BrainProjection.Build(
            [Entity(5, CanonicalEntityKinds.Mechanic, "Accelerate")],
            [
                Predicate(11, 5, "prevents", "exhaust", status: MechanicPredicateStatus.Rejected),
                Predicate(12, 5, "grants", "tank", status: MechanicPredicateStatus.Candidate),
            ],
            []);

        var p = Assert.Single(rows.Predicates);
        Assert.Equal("predicate:12", p["ref"]);
        Assert.Equal("candidate", p["status"]);
    }

    [Fact]
    public void Predicate_MissingSubjectEntity_ProjectsNodeButNoEdge()
    {
        var rows = BrainProjection.Build(
            [],  // geen entiteiten geprojecteerd
            [Predicate(11, subject: 5, "prevents", "exhaust")],
            []);
        Assert.Single(rows.Predicates);
        Assert.Empty(rows.HasPredicateEdges);
    }

    // ── OntologyVersion ──────────────────────────────────────────────────────

    [Fact]
    public void OntologyVersions_OrderedBySemVer_CurrentIsHighest_WithPrecedesChain()
    {
        // Bewust door elkaar aangeleverd — de bouwer sorteert op SemVer.
        var rows = BrainProjection.Build([], [],
            [Version(3, "1.2.0"), Version(1, "1.0.0"), Version(2, "1.1.0")]);

        Assert.Equal(["1.0.0", "1.1.0", "1.2.0"],
            rows.OntologyVersions.Select(v => (string)v["version"]!));
        // Alleen de hoogste is current.
        Assert.Equal([false, false, true],
            rows.OntologyVersions.Select(v => (bool)v["current"]!));
        Assert.Equal("ontologyversion:1", rows.OntologyVersions[0]["ref"]);

        // PRECEDES: ouder → nieuwer, opeenvolgend (n-1 edges).
        Assert.Equal(2, rows.PrecedesEdges.Count);
        Assert.Equal("ontologyversion:1", rows.PrecedesEdges[0]["from"]);
        Assert.Equal("ontologyversion:2", rows.PrecedesEdges[0]["to"]);
        Assert.Equal("ontologyversion:2", rows.PrecedesEdges[1]["from"]);
        Assert.Equal("ontologyversion:3", rows.PrecedesEdges[1]["to"]);
    }

    [Fact]
    public void OntologyVersions_UnparseableVersion_SortsLast()
    {
        var rows = BrainProjection.Build([], [],
            [Version(2, "not-a-semver"), Version(1, "2.0.0")]);
        Assert.Equal(["2.0.0", "not-a-semver"],
            rows.OntologyVersions.Select(v => (string)v["version"]!));
        // De laatst-gesorteerde (onparseerbaar) draagt current.
        Assert.True((bool)rows.OntologyVersions[^1]["current"]!);
    }

    [Fact]
    public void OntologyVersions_Empty_ProducesNoRowsOrEdges()
    {
        var rows = BrainProjection.Build([], [], []);
        Assert.Empty(rows.OntologyVersions);
        Assert.Empty(rows.PrecedesEdges);
    }

    // ── determinisme / idempotentie-sleutels ─────────────────────────────────

    [Fact]
    public void Build_IsDeterministic_SameInputSameRows()
    {
        CanonicalEntity[] entities =
            [Entity(1, CanonicalEntityKinds.Mechanic, "A"), Entity(2, CanonicalEntityKinds.Keyword, "B")];
        MechanicPredicateAssertion[] preds = [Predicate(9, 1, "grants", "tank")];
        OntologyVersionRecord[] versions = [Version(1, "1.0.0")];

        var a = BrainProjection.Build(entities, preds, versions);
        var b = BrainProjection.Build(entities, preds, versions);

        // Idempotente projectie: dezelfde MERGE-sleutels + props elke run.
        Assert.Equal(
            a.CanonicalEntities.Select(r => (string)r["ref"]!),
            b.CanonicalEntities.Select(r => (string)r["ref"]!));
        Assert.Equal(
            a.Predicates.Select(r => (string)r["ref"]!),
            b.Predicates.Select(r => (string)r["ref"]!));
    }

    [Fact]
    public void KeywordEntity_UsesTagBrainRef_ButOwnEntityRef()
    {
        // Keyword heeft geen eigen BrainRefKind → tag:-anker als brainRef-property;
        // de node-sleutel blijft de eigen entity:-ref.
        var rows = BrainProjection.Build(
            [Entity(3, CanonicalEntityKinds.Keyword, "Deflect")], [], []);
        var e = rows.CanonicalEntities[0];
        Assert.Equal("entity:3", e["ref"]);
        Assert.Equal("tag:Deflect", e["brainRef"]);
    }
}
