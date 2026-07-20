using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De VOLGORDE van de samengestelde ketens (#258). Sinds de opschoning
/// is het pad het enige ketenmechanisme: "all" en "nachtrun" zijn dunne aliassen
/// op een <see cref="PathDefinition"/> in plaats van met de hand geschreven
/// ketens in JobCatalog. Daarmee verhuist een risico mee — de volgorde staat nu
/// op één plek en een onschuldig ogende herschikking van een bouwsteen kan een
/// harde afhankelijkheid breken zonder dat iets faalt. Deze tests leggen de
/// afhankelijkheden vast die stilzwijgend fout kunnen gaan.</summary>
public class JobPathOrderTests
{
    private static IReadOnlyList<string> Steps(PathDefinition path) =>
        [.. path.Steps.Select(s => s.JobName)];

    [Fact]
    public void Nachtrun_ProjecteertHetBreinPasNaDeGraphRebuild()
    {
        // DE harde volgorde-eis (#258, uit de job-inventarisatie): "graph"
        // (GraphSyncService) doet een DETACH DELETE over ZIJN labelset en
        // "breinprojectie" (BreinProjectionService) schrijft een strikt
        // DISJUNCTE labelset. Ze zijn dus géén duplicaten — maar de basis-graaf
        // waar de brein-knopen aan hangen moet er wél eerst zijn. Draait de
        // projectie vóór de graph-rebuild, dan projecteert ze het brein op een
        // graaf die daarna alsnog wordt herbouwd: geen crash, geen foutmelding,
        // alleen een stil incompleet brein tot de volgende nacht.
        var steps = Steps(JobPaths.Nightly);
        var graph = steps.ToList().LastIndexOf("graph");
        var projection = steps.ToList().IndexOf("breinprojectie");

        Assert.True(graph >= 0, "de nachtrun hoort een graph-rebuild te bevatten");
        Assert.True(projection >= 0, "de nachtrun hoort de brein-projectie te bevatten");
        Assert.True(graph < projection,
            $"'graph' (index {graph}) moet vóór 'breinprojectie' (index {projection}) komen — "
            + "graph doet een DETACH DELETE over zijn labelset");
    }

    [Fact]
    public void BreinPad_ProjecteertPasNaDeMining_EnRedeneertPasNaDeProjectie()
    {
        // Binnen het brein-pad zelf: minen → projecteren → redeneren. De
        // reasoner leest de projectie; projecteren vóór het minen levert een
        // projectie van de vórige run op.
        var steps = Steps(JobPaths.Find("brein")!).ToList();

        Assert.True(steps.IndexOf("breinmine-predicaten") < steps.IndexOf("breinprojectie"));
        Assert.True(steps.IndexOf("breinprojectie") < steps.IndexOf("reason"));
    }

    [Fact]
    public void BreinPad_RegistreertEntiteitenVoorDePredicaatMining()
    {
        // #250: de predicaat-mining resolveert alleen tegen de canonieke
        // entiteitenlaag en registreert zelf niets. Staat "breinentiteiten" niet
        // vóór "breinmine-predicaten", dan vindt die stap NUL subjects — een
        // lege run die als succes wordt gerapporteerd.
        var steps = Steps(JobPaths.Find("brein")!).ToList();

        Assert.True(steps.IndexOf("breinentiteiten") < steps.IndexOf("breinmine-predicaten"),
            "zonder geregistreerde entiteiten vindt de predicaat-mining geen subjects (#250)");
    }

    [Fact]
    public void IngestPad_IndexeertRegelsEnBansNaDeScan()
    {
        // Het gat dat #258 dicht: "rules"/"bans" stonden alleen in de oude
        // "alles"-keten en ontbraken in het ingest-pad. Ze leunen op wat "scan"
        // zojuist binnenhaalde, dus de volgorde is een echte afhankelijkheid.
        var steps = Steps(JobPaths.Find("ingest")!).ToList();

        Assert.Equal(0, steps.IndexOf("scan"));
        Assert.Contains("rules-index", steps);
        Assert.Contains("bans", steps);
        Assert.True(steps.IndexOf("scan") < steps.IndexOf("rules-index"));
        Assert.True(steps.IndexOf("scan") < steps.IndexOf("bans"));
        // Changeconsolidatie ná classify (#206): ChangeType/Summary moeten
        // ingevuld zijn om kandidaat-paren te kunnen beoordelen.
        Assert.True(steps.IndexOf("classify") < steps.IndexOf("consolidatechanges"));
    }

    [Fact]
    public void IngestPad_GebruiktDeIncrementeleRegelindex_NietDeVolledigeHerbouw()
    {
        // "rules" herbouwt ALLES (force:true — bedoeld voor na een
        // parser-verbetering). In een keten die elke nacht draait zou dat de
        // complete regelindex elke nacht her-chunken én her-embedden. De keten
        // hoort de incrementele variant te gebruiken.
        Assert.DoesNotContain("rules", Steps(JobPaths.Find("ingest")!));
        Assert.DoesNotContain("rules", Steps(JobPaths.Nightly));
        Assert.DoesNotContain("rules", Steps(JobPaths.AllUpdate));
    }

    [Fact]
    public void Nachtrun_IsDeGecapteKetenMetDeCapsEraf_ZelfdeStappenInDezelfdeVolgorde()
    {
        // De nachtrun wordt programmatisch uit dezelfde bouwstenen afgeleid als
        // "all" + het brein-pad. Deze test is de anti-drift-vangrail: voegt
        // iemand een stap toe aan het ingest-, kaart- of brein-pad, dan hoort
        // die vanzelf in de nachtrun te staan.
        IReadOnlyList<string> verwacht =
            [.. Steps(JobPaths.AllUpdate), .. Steps(JobPaths.Find("brein")!)];

        Assert.Equal(verwacht, Steps(JobPaths.Nightly));
    }

    [Fact]
    public void Nachtrun_DraaitDeDureMinersOngecapt_EnDaaromZonderDrain()
    {
        // Uncapped en Drain sluiten elkaar uit: een ongecapte run verwerkt in
        // één keer alles wat binnen de deadline past, dus een drain-lus zou er
        // alleen overheen lopen tot de deadline al verstreken is.
        var uncapped = JobPaths.Nightly.Steps.Where(s => s.Uncapped).Select(s => s.JobName).ToList();

        Assert.Equal(
            new[] { "mine", "breinmine-interacties", "breinmine-predicaten" }.Order(),
            uncapped.Order());
        Assert.All(JobPaths.Nightly.Steps, s => Assert.False(s.Uncapped && s.Drain));
    }

    [Fact]
    public void AllesEnNachtrun_ZijnBestEffort_DeLosseHandmatigePadenNiet()
    {
        // CONVENTIONS: "een haperende externe dienst stopt nooit de hele run".
        // Dat gold voor RunAllAsync/RunNightlyAsync en moest bij de overgang
        // naar het pad-mechanisme behouden blijven — een nachtrun van uren mag
        // niet stranden op één 5xx van rb-ai. De handmatige paden houden bewust
        // stop-bij-fout: daar staat een beheerder naar te kijken.
        Assert.True(JobPaths.AllUpdate.ContinueOnError);
        Assert.True(JobPaths.Nightly.ContinueOnError);
        Assert.All(JobPaths.AllPaths, p => Assert.False(p.ContinueOnError));
    }

    [Fact]
    public void LegacyInteractieMiner_ZitInGeenEnkeleKeten()
    {
        // #258, de directe winst: de paar-lexicale miner (InteractionService,
        // opgevolgd door BreinInteractionMiningService) kostte élke nachtrun
        // LLM-budget dat de opvolger nodig heeft — en ze vechten om dezelfde
        // rb-ai-semafoor. Hij blijft handmatig startbaar zolang het leespad nog
        // op zijn tabel terugvalt, maar draait in geen enkele keten mee.
        IReadOnlyList<PathDefinition> alleKetens =
            [.. JobPaths.AllPaths, JobPaths.AllUpdate, JobPaths.Nightly];

        foreach (var path in alleKetens)
            Assert.DoesNotContain("interactions", Steps(path));
    }

    [Fact]
    public void SamengesteldeKetens_BotsenNietMetLosStartbarePaden()
    {
        // "all" en "nachtrun" zijn ketens áchter een bestaande JOBNAAM, niet
        // losse paden — ze horen dus niet in AllPaths (dat is wat
        // /api/admin/paths aanbiedt), anders zou je ze los kunnen starten
        // zónder de deadline die de nachtrun-job berekent.
        Assert.DoesNotContain(JobPaths.AllPaths, p => p.Name == JobPaths.Nightly.Name);
        Assert.DoesNotContain(JobPaths.AllPaths, p => p.Name == JobPaths.AllUpdate.Name);
        // En ze dragen wél de naam van de job die ze uitvoert, zodat de
        // per-stap-run_log-regels (Kind=padnaam) bij die job terug te vinden zijn.
        Assert.NotNull(JobCatalog.Find(JobPaths.Nightly.Name));
        Assert.NotNull(JobCatalog.Find(JobPaths.AllUpdate.Name));
    }

    [Fact]
    public void ElkeStapVanDeSamengesteldeKetens_BestaatInDeJobCatalog()
    {
        // Zelfde vangnet als JobPathsTests voor de losse paden — maar AllPaths
        // bevat de samengestelde ketens niet, dus die hebben hun eigen check.
        foreach (var path in new[] { JobPaths.AllUpdate, JobPaths.Nightly })
            foreach (var step in path.Steps)
                Assert.True(JobCatalog.Find(step.JobName) is not null,
                    $"keten '{path.Name}': stap '{step.JobName}' bestaat niet in de JobCatalog");
    }

    [Fact]
    public void OngecapteStappen_HebbenOokEchtEenOngecapteVariant()
    {
        // Een stap die Uncapped zet terwijl de job geen RunUncapped heeft, valt
        // stil terug op de GECAPTE run: de nachtrun denkt dan ongecapt te
        // draaien en levert nacht na nacht een cap-groot brokje af.
        foreach (var path in new[] { JobPaths.AllUpdate, JobPaths.Nightly })
            foreach (var step in path.Steps.Where(s => s.Uncapped))
                Assert.True(JobCatalog.Find(step.JobName)!.RunUncapped is not null,
                    $"stap '{step.JobName}' is als ongecapt gemarkeerd maar heeft geen RunUncapped-variant");
    }
}
