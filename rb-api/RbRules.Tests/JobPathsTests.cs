using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>Validatie van de padencatalogus (#190): elke stap moet naar een
/// bestaande JobCatalog-job verwijzen — een tikfout in JobPaths zou anders
/// pas bij het draaien van het pad (een InvalidOperationException uit
/// PathRunner) aan het licht komen in plaats van hier, statisch.</summary>
public class JobPathsTests
{
    [Fact]
    public void AlleStappen_VerwijzenNaarBestaandeJobCatalogJobs()
    {
        foreach (var path in JobPaths.AllPaths)
            foreach (var step in path.Steps)
                Assert.True(
                    JobCatalog.Find(step.JobName) is not null,
                    $"pad '{path.Name}': stap '{step.JobName}' bestaat niet in de JobCatalog");
    }

    [Fact]
    public void PadNamen_ZijnUniek()
    {
        var names = JobPaths.AllPaths.Select(p => p.Name).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    [Fact]
    public void PadNamen_BotsenNietMetJobNamen()
    {
        // #190: een pad verschijnt als "job" op /api/admin/status (Kind="job",
        // Ref=padnaam via JobRunner) — zou een padnaam gelijk zijn aan een
        // bestaande jobnaam, dan zijn hun run_log-historie/laatste-run niet
        // meer te onderscheiden.
        foreach (var path in JobPaths.AllPaths)
            Assert.True(
                JobCatalog.Find(path.Name) is null,
                $"padnaam '{path.Name}' botst met een bestaande job in de JobCatalog");
    }

    [Fact]
    public void ElkPad_HeeftMinstensEenStap()
    {
        foreach (var path in JobPaths.AllPaths)
            Assert.NotEmpty(path.Steps);
    }

    [Fact]
    public void RegenerateKnowledge_ZitInGeenEnkelPad()
    {
        // De wipe blijft een losse, expliciete Gevarenzone-actie (#187/#190) —
        // nooit onderdeel van een geautomatiseerd pad.
        foreach (var path in JobPaths.AllPaths)
            Assert.DoesNotContain(path.Steps, s => s.JobName == "regenerateknowledge");
    }

    [Fact]
    public void BreinReset_ZitInGeenEnkelPad()
    {
        // De gerichte brein-mining-reset (#263) is net als de wipe een expliciete,
        // destructieve Gevarenzone-actie — nooit onderdeel van een pad dat vanzelf
        // doorstroomt.
        foreach (var path in JobPaths.AllPaths)
            Assert.DoesNotContain(path.Steps, s => s.JobName.StartsWith("breinreset-"));
    }

    [Fact]
    public void BreinReset_BestaatInBeideScopes()
    {
        // Twee losse namen i.p.v. een verborgen modus-vlag: de scope-keuze moet in
        // het jobs-paneel én het run_log zichtbaar blijven (#263).
        Assert.NotNull(JobCatalog.Find("breinreset-interacties"));
        Assert.NotNull(JobCatalog.Find("breinreset-volledig"));
    }

    [Fact]
    public void KennisPad_HeeftClaimsClarifyRelationsRelationTriageAlsDrain_EnEindigtOpGraph()
    {
        var path = JobPaths.Find("knowledge");
        Assert.NotNull(path);
        Assert.Equal(
            new[] { "claims", "clarify", "relations", "relationtriage", "graph" },
            path!.Steps.Select(s => s.JobName).ToArray());
        Assert.True(path.Steps[0].Drain);
        Assert.True(path.Steps[1].Drain);
        Assert.True(path.Steps[2].Drain);
        Assert.True(path.Steps[3].Drain); // relationtriage (#199) is per-run gecapt
        Assert.False(path.Steps[4].Drain); // graph is niet gecapt — geen drain nodig
    }

    [Fact]
    public void VolledigPad_IsPrimerGevolgdDoorHetKennisPad_ZonderWipe()
    {
        var full = JobPaths.Find("full");
        var knowledge = JobPaths.Find("knowledge");
        Assert.NotNull(full);
        Assert.NotNull(knowledge);
        Assert.Equal("primer", full!.Steps[0].JobName);
        Assert.Equal(
            knowledge!.Steps.Select(s => s.JobName).ToArray(),
            full.Steps.Skip(1).Select(s => s.JobName).ToArray());
    }
}
