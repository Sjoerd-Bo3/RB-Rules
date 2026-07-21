using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>De eerlijke RELATES_TO-telling in de graph-rebuild (#321, ADR-20):
/// beide statements geven <c>RETURN count(r)</c> terug, GraphSyncResult meldt
/// wat er wérkelijk geschreven is (niet het te-doen-aantal), en het verschil
/// met het aanbod staat per oorzaak in de run-melding — de pijplijn schrijft
/// die zelf (#282b), want de scheduler-aanroep gooit het resultaat weg.
///
/// Er is geen Neo4j in CI (gedocumenteerde schuld, ARCHITECTURE §6.3), dus de
/// count komt hier uit de driver-fake; wat deze tests pinnen is dat de service
/// de meting LEEST en er eerlijk over rapporteert — de mutatie
/// "Relations = relationRows.Count" (het oude te-doen-aantal) maakt ze rood.</summary>
public class GraphSyncRelatesToTallyTests
{
    private const string SourceId = "core-rules-pdf";

    // Herkenning van de twee RELATES_TO-statements in de opgenomen Cypher: het
    // #116-statement SET't trust, de qualifier-cache SET't window.
    private static bool IsRelationStatement(string q) =>
        q.Contains("RELATES_TO") && q.Contains("r.trust");

    private static bool IsCacheStatement(string q) =>
        q.Contains("RELATES_TO") && q.Contains("r.window");

    [Fact]
    public async Task Sync_TeltWatNeo4jSchreef_NietHetTeDoenAantal()
    {
        await using var db = TestGraphDb.New();
        await SeedRelationsAsync(db,
            ("mechanic:Tank", $"section:{SourceId}/466"),
            ("card:ogn-001", "mechanic:Tank"),
            ("card:ogn-001", "ruling:12"));

        // De driver meldt 2 geschreven edges voor 3 aangeboden rijen — het
        // ruling:-eindpunt valt per constructie buiten de WHERE-disjunctie.
        var driver = new RecordingDriver(q => IsRelationStatement(q) ? 2L : null);
        var result = await new GraphSyncService(db, driver).SyncAsync();

        Assert.Equal(2, result.Relations);
        Assert.NotNull(result.RelationEdges);
        Assert.Equal(3, result.RelationEdges!.Offered);
        Assert.Equal(2, result.RelationEdges.Written);
        Assert.Equal(1, result.RelationEdges.OutsideProjection);
        Assert.Equal(0, result.RelationEdges.MissingNode);
    }

    [Fact]
    public async Task Sync_GatZonderBuitensoort_HeetRefZonderKnoop()
    {
        await using var db = TestGraphDb.New();
        await SeedRelationsAsync(db,
            ("mechanic:Tank", $"section:{SourceId}/466"),
            ("mechanic:Tank", "mechanic:Verdwenen"));

        var driver = new RecordingDriver(q => IsRelationStatement(q) ? 1L : null);
        var result = await new GraphSyncService(db, driver).SyncAsync();

        Assert.Equal(1, result.Relations);
        Assert.Equal(0, result.RelationEdges!.OutsideProjection);
        Assert.Equal(1, result.RelationEdges.MissingNode);
        Assert.Contains("1 ref zonder knoop", result.RelationsDropNote);
    }

    [Fact]
    public async Task Sync_BijVerlies_SchrijftDePijplijnZelfEenWarnRegel()
    {
        await using var db = TestGraphDb.New();
        await SeedRelationsAsync(db,
            ("mechanic:Tank", $"section:{SourceId}/466"),
            ("card:ogn-001", "ruling:12"));

        var driver = new RecordingDriver(q => IsRelationStatement(q) ? 1L : null);
        await new GraphSyncService(db, driver).SyncAsync();

        // Pijplijn-eigen melding (#282b): ook langs het scheduler-pad — dat het
        // resultaat weggooit — is het verlies zichtbaar, mét oorzaak.
        var log = await db.RunLogs.SingleAsync(l => l.Kind == "graph");
        Assert.Equal("warn", log.Status);
        Assert.Equal("relates-to", log.Ref);
        Assert.Contains("relaties 1 van 2 geschreven", log.Detail);
        Assert.Contains("1 eindpunt-soort buiten de projectie", log.Detail);
    }

    [Fact]
    public async Task Sync_AllesGeland_GeenWarnEnGeenNote()
    {
        await using var db = TestGraphDb.New();
        await SeedRelationsAsync(db,
            ("mechanic:Tank", $"section:{SourceId}/466"));

        var driver = new RecordingDriver(q =>
            IsRelationStatement(q) ? 1L : IsCacheStatement(q) ? 0L : null);
        var result = await new GraphSyncService(db, driver).SyncAsync();

        Assert.Equal(1, result.Relations);
        Assert.Null(result.RelationsDropNote);
        Assert.Equal(0, await db.RunLogs.CountAsync());
    }

    [Fact]
    public async Task Sync_QualifierCache_TeltMeeInDeNote()
    {
        await using var db = TestGraphDb.New();
        // Volledige fixture: bevat één relatie-rij én één verankerde interactie
        // (= één qualifier-cache-rij, agent card:/patient mechanic:).
        await ProjectieCorpus.VulAsync(db);

        var driver = new RecordingDriver(q =>
            IsRelationStatement(q) ? 1L : IsCacheStatement(q) ? 0L : null);
        var result = await new GraphSyncService(db, driver).SyncAsync();

        Assert.NotNull(result.QualifierCacheEdges);
        Assert.Equal(1, result.QualifierCacheEdges!.Offered);
        Assert.Equal(0, result.QualifierCacheEdges.Written);
        Assert.Contains("qualifier-cache 0 van 1 geschreven", result.RelationsDropNote);
        Assert.Contains("1 ref zonder knoop", result.RelationsDropNote);
    }

    [Fact]
    public async Task Sync_ZonderCountRij_ValtTerugOpHetDeterministischeDeel()
    {
        // De opnemende driver van de guard-tests levert geen count(r)-rij; dan
        // is alleen het buiten-de-projectie-deel bekend (de WHERE weigert het
        // per constructie) en blijft de rest als geschreven gelden — geen
        // verzonnen "ref zonder knoop"-melding zonder meting.
        await using var db = TestGraphDb.New();
        await SeedRelationsAsync(db,
            ("mechanic:Tank", $"section:{SourceId}/466"),
            ("card:ogn-001", "ruling:12"));

        var driver = new RecordingDriver();
        var result = await new GraphSyncService(db, driver).SyncAsync();

        Assert.Equal(1, result.Relations);
        Assert.Equal(1, result.RelationEdges!.OutsideProjection);
        Assert.Equal(0, result.RelationEdges.MissingNode);
        Assert.Contains("1 eindpunt-soort buiten de projectie", result.RelationsDropNote);
    }

    /// <summary>Minimale stand met een kaart, een sectie en een geaccepteerd
    /// kind, plus de opgegeven relatie-rijen (status accepted, kind
    /// "counters" — passeert de reviewpoort).</summary>
    private static async Task SeedRelationsAsync(
        RbRulesDbContext db, params (string From, string To)[] rows)
    {
        db.Sources.Add(new Source
        {
            Id = SourceId, Name = "Core Rules", Url = "https://example.com/core",
            Type = "official", TrustTier = 1, Rank = 100, Parser = "pdf", Cadence = "weekly",
        });
        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-001", Name = "Shieldbearer", Mechanics = ["Tank"],
        });
        db.Documents.Add(new Document
        {
            Id = 1, SourceId = SourceId, Content = "466 Combat.", ContentHash = "hash-1",
        });
        db.RuleChunks.Add(new RuleChunk
        {
            DocumentId = 1, SourceId = SourceId, SectionCode = "466", ChunkIndex = 0,
            Text = "Combat.",
        });
        foreach (var (from, to) in rows)
            db.Relations.Add(new Relation
            {
                FromRef = from, ToRef = to, Kind = "counters",
                Explanation = "test", Provenance = "test", Trust = 0.7,
                Status = "accepted",
            });
        await db.SaveChangesAsync();
    }
}
