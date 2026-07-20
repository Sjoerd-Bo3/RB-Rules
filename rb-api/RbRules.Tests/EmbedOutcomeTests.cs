using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;
using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Tests;

/// <summary>#282 — de kernel schoot <c>llama-server</c> af op ~2,5 GB (de cgroup-cap
/// van <c>rb-v2-ollama</c>) en de embed-stap viel stil. Dat werd alleen zichtbaar
/// doordat iemand toevallig <c>dmesg</c> las: de pijplijn meldde het aantal
/// TE-DOEN kaarten als "geembed", en de scheduler ving de exception op met een
/// LogWarning naar de containerlog. Deze tests leggen vast dat uitval als data
/// terugkomt (per oorzaak geteld, in run_log, kaarten blijven staan) en dat de
/// batchgrootte begrensd is.</summary>
public class EmbedOutcomeTests
{
    // ── DE REGRESSIETEST ─────────────────────────────────────────────────────
    // Faalt zodra een gefaalde embed-stap weer stil wordt weggeslikt in plaats van
    // in het run-resultaat en het run_log te landen.

    [Fact]
    public async Task Embed_OllamaValtOm_MeldtUitval_EnSliktHemNietStilWeg()
    {
        await using var db = NewDb();
        db.Cards.AddRange(Cards(20));
        await db.SaveChangesAsync();

        // Ollama's model-runner is door de OOM-killer afgeschoten: 5xx op elk verzoek.
        var pipeline = Pipeline(db, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var r = await pipeline.RunAsync();

        // 1. Het RESULTAAT liegt niet meer: geen enkele kaart kreeg een vector.
        Assert.Equal(0, r.Embedded);
        Assert.Equal(20, r.Failed);
        Assert.True(r.HasFailures);
        // 2. Mét oorzaak, niet alleen "er ging iets mis".
        Assert.Contains("5xx", r.FailureSummary);
        Assert.Contains("mislukt", r.Summary);
        // 3. En het landt in run_log, ongeacht welke aanroeper de pijplijn startte —
        //    dát is wat de scheduler-tick voorheen naar de containerlog liet lekken.
        var log = Assert.Single(db.RunLogs.Where(l => l.Kind == "embed"));
        Assert.Equal("error", log.Status);
        Assert.Contains("5xx", log.Detail);
    }

    [Fact]
    public async Task Embed_OllamaValtOm_LaatKaartenStaanVoorDeVolgendeRun()
    {
        await using var db = NewDb();
        db.Cards.AddRange(Cards(12));
        await db.SaveChangesAsync();

        await Pipeline(db, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .RunAsync();

        // Niets half weggeschreven: elke kaart is nog steeds onembeddeerd en komt bij
        // de volgende run gewoon weer aan de beurt (de pijplijn selecteert op
        // Embedding == null).
        Assert.Equal(12, await db.Cards.CountAsync(c => c.Embedding == null));
        Assert.Equal(0, await db.Cards.CountAsync(c => c.EmbeddingModel != null));
    }

    [Fact]
    public async Task Embed_HalverwegeOmgevallen_TeltAlleenWatEchtGeembedIs()
    {
        await using var db = NewDb();
        db.Cards.AddRange(Cards(16));
        await db.SaveChangesAsync();

        // Batch 1 lukt, daarna valt Ollama om — het scenario uit het issue.
        var calls = 0;
        var pipeline = Pipeline(db, req => ++calls == 1
            ? OkEmbeddings(BatchTexts(req))
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var r = await pipeline.RunAsync();

        Assert.Equal(8, r.Embedded);     // precies één batch van EmbeddingSettings.Default
        Assert.Equal(8, r.Failed);
        Assert.Equal(8, await db.Cards.CountAsync(c => c.Embedding != null));
        Assert.Equal(8, await db.Cards.CountAsync(c => c.Embedding == null));
        Assert.Single(db.RunLogs.Where(l => l.Kind == "embed" && l.Status == "error"));
    }

    [Fact]
    public async Task Embed_GeslaagdeRun_SchrijftGeenFoutregel()
    {
        await using var db = NewDb();
        db.Cards.AddRange(Cards(5));
        await db.SaveChangesAsync();

        var r = await Pipeline(db, req => OkEmbeddings(BatchTexts(req))).RunAsync();

        Assert.Equal(5, r.Embedded);
        Assert.False(r.HasFailures);
        Assert.Equal("", r.FailureSummary);
        // De ok-regel hoort bij de aanroeper (endpoint/job) — de pijplijn zwijgt.
        Assert.Empty(db.RunLogs);
    }

    // ── Oorzaak per uitslag ──────────────────────────────────────────────────

    [Fact]
    public async Task Embed_ContainerWeg_IsOnbereikbaar_GeenServerfout()
    {
        // Container-OOM-kill / herstart midden in het verzoek: socketfout, geen 5xx.
        // Het onderscheid stuurt de fix: runner-kill = batch verkleinen,
        // container weg = de service zelf nakijken.
        var svc = Service(_ => throw new HttpRequestException("connection reset"));

        var r = await svc.TryEmbedAsync(["tekst"]);

        Assert.Equal(EmbedCallOutcome.Transport, r.Outcome);
        Assert.Null(r.Vectors);
    }

    [Fact]
    public async Task Embed_Timeout_IsTimeout_GeenTransportfout()
    {
        var svc = Service(_ => throw new TaskCanceledException("timeout"));

        var r = await svc.TryEmbedAsync(["tekst"]);

        Assert.Equal(EmbedCallOutcome.Timeout, r.Outcome);
    }

    [Fact]
    public async Task Embed_404_IsClientfout_ModelNietGepulld()
    {
        var svc = Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var r = await svc.TryEmbedAsync(["tekst"]);

        Assert.Equal(EmbedCallOutcome.ClientError, r.Outcome);
        Assert.Equal(404, r.StatusCode);
    }

    [Fact]
    public async Task Embed_TeWeinigVectoren_IsOnvolledig_NooitEenScheveKoppeling()
    {
        // Twee teksten in, één vector terug. Stil doorlopen zou vector[0] aan de
        // TWEEDE kaart plakken — erger dan geen embedding.
        var svc = Service(_ => Json(HttpStatusCode.OK, Embeddings(1)));

        var r = await svc.TryEmbedAsync(["een", "twee"]);

        Assert.Equal(EmbedCallOutcome.Incomplete, r.Outcome);
        Assert.Null(r.Vectors);
    }

    [Fact]
    public async Task Embed_VerkeerdeDimensie_BlijftEenHardeFout()
    {
        // Provenance is heilig: model + dimensie mogen niet stilzwijgend mixen. Een
        // kleinere batch is geen ander model — deze guard blijft ongewijzigd.
        var svc = Service(_ => Json(HttpStatusCode.OK,
            $$"""{"embeddings":[[{{string.Join(",", Enumerable.Repeat("0.1", 768))}}]]}"""));

        var r = await svc.TryEmbedAsync(["tekst"]);

        Assert.Equal(EmbedCallOutcome.DimensionMismatch, r.Outcome);
        Assert.Null(r.Vectors);
        Assert.Contains("1024", r.Error);
    }

    [Fact]
    public async Task EmbedAsync_BlijftGooienVoorDeInteractievePaden()
    {
        // /ask en de zoekpaden vangen de exception op en degraderen naar alleen-FTS;
        // dat contract mag #282 niet breken.
        var svc = Service(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.EmbedAsync(["tekst"]));
    }

    // ── Tally ────────────────────────────────────────────────────────────────

    [Fact]
    public void Tally_TeltPerOorzaak_EnHoeveelTekstenErBlevenLiggen()
    {
        var tally = new EmbedOutcomeTally();
        tally.Add(EmbedCallOutcome.Ok, 8);
        tally.Add(EmbedCallOutcome.ServerError, 8);
        tally.Add(EmbedCallOutcome.ServerError, 8);
        tally.Add(EmbedCallOutcome.Transport, 4);

        Assert.Equal(3, tally.Failures);
        Assert.Equal(20, tally.TextsLost);   // de 8 geslaagde tellen niet mee
        Assert.Equal("5xx (model-runner omgevallen?)×2, onbereikbaar×1", tally.Summary);
    }

    [Fact]
    public void Tally_ZonderUitval_HeeftEenLegeSamenvatting()
    {
        var tally = new EmbedOutcomeTally();
        tally.Add(EmbedCallOutcome.Ok, 8);

        Assert.False(tally.HasFailures);
        Assert.Equal("", tally.Summary);
        Assert.Equal(0, tally.TextsLost);
    }

    // ── Batchgrenzen ─────────────────────────────────────────────────────────

    [Fact]
    public void Batching_SluitOpAantal()
    {
        var texts = Enumerable.Repeat("kort", 20).ToList();

        var batches = EmbedBatching.Split(texts, maxCount: 8, maxChars: 100_000);

        Assert.Equal(3, batches.Count);
        Assert.Equal(new Range(0, 8), batches[0]);
        Assert.Equal(new Range(16, 20), batches[^1]);
    }

    [Fact]
    public void Batching_SluitOokOpTekens_WantLengteBepaaltDePiek()
    {
        // De kern van #282: 8 regel-secties van 2400 tekens zijn een heel ander
        // verzoek dan 8 kaartteksten van 300. Alleen op aantal begrenzen laat de
        // zwaarste verzoeken ongemoeid.
        var texts = Enumerable.Repeat(new string('x', 2400), 8).ToList();

        var batches = EmbedBatching.Split(texts, maxCount: 8, maxChars: 8000);

        Assert.Equal(3, batches.Count);   // 3+3+2, niet één verzoek van 19200 tekens
        Assert.All(batches, b =>
            Assert.True(texts[b].Sum(t => t.Length) <= 8000));
    }

    [Fact]
    public void Batching_EnkeleTeLangeTekstGaatAlleenMee_NooitWeggelaten()
    {
        // Weglaten zou een kaart stil zonder embedding laten — precies de degradatie
        // die #282 opheft.
        var texts = new List<string> { "kort", new('x', 20_000), "kort" };

        var batches = EmbedBatching.Split(texts, maxCount: 8, maxChars: 8000);

        Assert.Equal(3, batches.Count);
        Assert.Equal(new Range(1, 2), batches[1]);
        Assert.Equal(3, batches.Sum(b => b.GetOffsetAndLength(texts.Count).Length));
    }

    // ── Instellingen ─────────────────────────────────────────────────────────

    [Fact]
    public void Settings_DefaultIsGehalveerdTenOpzichteVanVoor282()
    {
        Assert.Equal(8, EmbeddingSettings.Default.BatchSize);
        Assert.Equal(8000, EmbeddingSettings.Default.BatchChars);
    }

    [Fact]
    public void Settings_OnzinOfBuitenBereik_ValtTerugOpDeDefault()
    {
        // Een typfout in de .env mag de pijplijn niet op 1 tekst per verzoek zetten
        // (traag) of het plafond ontgrendelen (OOM terug).
        using var _ = new EnvScope(("EMBED_BATCH_SIZE", "nul"), ("EMBED_BATCH_CHARS", "0"));

        var s = EmbeddingSettings.FromEnvironment();

        Assert.Equal(EmbeddingSettings.DefaultBatchSize, s.BatchSize);
        Assert.Equal(EmbeddingSettings.DefaultBatchChars, s.BatchChars);
    }

    [Fact]
    public void Settings_GeldigeWaardeWordtOvergenomen()
    {
        using var _ = new EnvScope(("EMBED_BATCH_SIZE", "4"), ("EMBED_BATCH_CHARS", "3000"));

        var s = EmbeddingSettings.FromEnvironment();

        Assert.Equal(4, s.BatchSize);
        Assert.Equal(3000, s.BatchChars);
    }

    // ── testinfra ────────────────────────────────────────────────────────────

    private static IEnumerable<Card> Cards(int n) =>
        Enumerable.Range(1, n).Select(i => new Card
        {
            RiftboundId = $"ogn-{i:000}", Name = $"Kaart {i}", Type = "Unit",
            TextPlain = "Deal 2 damage to target unit.",
        });

    private static CardEmbeddingPipeline Pipeline(
        RbRulesDbContext db, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(db, Service(respond), EmbeddingSettings.Default);

    private static EmbeddingService Service(
        Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://ollama.test") });

    /// <summary>Aantal teksten in het verzoek — zodat de stub precies zoveel vectoren
    /// teruggeeft als er gevraagd zijn.</summary>
    private static int BatchTexts(HttpRequestMessage req)
    {
        var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return System.Text.Json.JsonDocument.Parse(body)
            .RootElement.GetProperty("input").GetArrayLength();
    }

    private static HttpResponseMessage OkEmbeddings(int count) =>
        Json(HttpStatusCode.OK, Embeddings(count));

    private static string Embeddings(int count) =>
        $$"""{"embeddings":[{{string.Join(",",
            Enumerable.Repeat(
                $"[{string.Join(",", Enumerable.Repeat("0.1", EmbeddingConfig.Dimensions))}]",
                count))}}]}""";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Zet env-variabelen en herstelt ze — env is procesbreed, dus nooit
    /// zomaar laten staan voor de volgende test.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly (string Key, string? Old)[] _saved;

        public EnvScope(params (string Key, string Value)[] vars)
        {
            _saved = [.. vars.Select(v => (v.Key, Environment.GetEnvironmentVariable(v.Key)))];
            foreach (var (key, value) in vars) Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var (key, old) in _saved) Environment.SetEnvironmentVariable(key, old);
        }
    }

    private static RbRulesDbContext NewDb() => new InMemoryDbContext(
        new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>InMemory kent het pgvector-type niet: sla vectors op in hun
    /// tekstvorm (zelfde patroon als de AdminOverview-tests).</summary>
    private sealed class InMemoryDbContext(DbContextOptions<RbRulesDbContext> options)
        : RbRulesDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            foreach (var entity in b.Model.GetEntityTypes().ToList())
                foreach (var prop in entity.GetProperties()
                             .Where(p => p.ClrType == typeof(Vector)).ToList())
                    b.Entity(entity.ClrType).Property(prop.Name)
                        .HasConversion(new ValueConverter<Vector, string>(
                            v => v.ToString(), s => new Vector(s)));
        }
    }
}
