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

    // ── Het alarm moet doven door HERSTEL, niet door veroudering ─────────────
    // #282-review: er is geen enkel vanuit de UI bereikbaar pad dat een embed-ok-regel
    // schrijft (rb-web post alleen /api/admin/jobs/{name}, JobRunner logt Kind="job",
    // de scheduler logde bij succes niets). Zonder ok-regel blijft een oude foutregel
    // eeuwig de nieuwste embed-regel: loos alarm tot de rij veroudert.

    [Fact]
    public async Task Embed_GeslaagdeRunMetWerk_SchrijftEenOkRegel_ZodatHetAlarmDooft()
    {
        await using var db = NewDb();
        db.Cards.AddRange(Cards(5));
        await db.SaveChangesAsync();

        var r = await Pipeline(db, req => OkEmbeddings(BatchTexts(req))).RunAsync();

        Assert.Equal(5, r.Embedded);
        Assert.False(r.HasFailures);
        var log = Assert.Single(db.RunLogs.Where(l => l.Kind == "embed"));
        Assert.Equal("ok", log.Status);
    }

    [Fact]
    public async Task Embed_NaEenFout_LaatEenGeslaagdeRunDeNieuwsteRegelOkZijn()
    {
        await using var db = NewDb();
        db.Cards.AddRange(Cards(4));
        await db.SaveChangesAsync();

        // Run 1: Ollama ligt eruit.
        await Pipeline(db, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .RunAsync();
        // Run 2: Ollama is terug — precies het herstelpad dat het paneel moet doven.
        await Pipeline(db, req => OkEmbeddings(BatchTexts(req))).RunAsync();

        var newest = db.RunLogs.Where(l => l.Kind == "embed")
            .OrderByDescending(l => l.Id).First();
        Assert.Equal("ok", newest.Status);
    }

    [Fact]
    public async Task Embed_NietsTeDoen_SchrijftGeenRegel()
    {
        // Anders zou de scheduler-tick elk uur een lege regel produceren en het
        // run_log volstromen met "0 geembed".
        await using var db = NewDb();

        var r = await Pipeline(db, req => OkEmbeddings(BatchTexts(req))).RunAsync();

        Assert.Equal(0, r.Embedded);
        Assert.Empty(db.RunLogs);
    }

    // ── Niet eindeloos doorproberen ──────────────────────────────────────────

    [Fact]
    public async Task Embed_OllamaLigtEruit_BreektAfNaDrieOpeenvolgendeFouten()
    {
        // #282-review: vóór #282 kostte een dode Ollama één verzoek (de pijplijn
        // gooide). Doorlopen-per-batch mag dat niet in 179 × 5 min timeout ≈ 15 uur
        // veranderen — met de één-job-gate en de synchrone scheduler-aanroep ligt dan
        // alles stil.
        await using var db = NewDb();
        db.Cards.AddRange(Cards(80));   // 10 batches van 8
        await db.SaveChangesAsync();

        var calls = 0;
        var r = await Pipeline(db, _ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout);
        }).RunAsync();

        Assert.Equal(3, calls);          // niet alle 10
        Assert.True(r.Aborted);
        Assert.Equal(24, r.Failed);      // 3 batches × 8
        Assert.Equal(56, r.Remaining);   // nooit geprobeerd
        Assert.Contains("afgebroken", r.Summary);
        Assert.Contains("niet geprobeerd", r.Summary);
    }

    [Fact]
    public async Task Embed_LosseHik_BreektDeRunNietAf()
    {
        // Een geslaagde batch zet de teller terug: één hapering mag een lange run niet
        // afkappen, anders ruilen we de ene stille schade voor de andere.
        await using var db = NewDb();
        db.Cards.AddRange(Cards(40));   // 5 batches
        await db.SaveChangesAsync();

        var calls = 0;
        var r = await Pipeline(db, req =>
        {
            calls++;
            // Batch 2 en 4 haperen, met steeds een geslaagde ertussen.
            return calls is 2 or 4
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : OkEmbeddings(BatchTexts(req));
        }).RunAsync();

        Assert.Equal(5, calls);          // alle batches geprobeerd
        Assert.False(r.Aborted);
        Assert.Equal(24, r.Embedded);
        Assert.Equal(16, r.Failed);
    }

    [Fact]
    public async Task Embed_Annulering_BewaartDeTallyVanEerdereFouten()
    {
        // #282-review: TryEmbedAsync gooit door bij annulering. Zonder vangnet slaat
        // LogRunAsync over en verdwijnt de meting van batches die vóór de annulering
        // faalden — dezelfde les als JobRunner's "bewust zonder token"-afronding.
        await using var db = NewDb();
        db.Cards.AddRange(Cards(24));
        await db.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        var calls = 0;
        var pipeline = Pipeline(db, _ =>
        {
            if (++calls == 2) { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.RunAsync(ct: cts.Token));

        var log = Assert.Single(db.RunLogs.Where(l => l.Kind == "embed"));
        Assert.Equal("error", log.Status);
        Assert.Contains("5xx", log.Detail);
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

    // ── De foutregel mag niet uit het venster wegzakken ──────────────────────

    [Fact]
    public async Task EmbedGezondheid_OverleeftDeDrukteVanEenNachtrun()
    {
        // #282-review: het paneel las eerst `logs` — exact de 15 nieuwste run_log-rijen
        // uit /admin/status. Scenario: nachtrun 02:00, Ollama valt om in stap 5/8;
        // daarna schrijven stap 6-8, de job-afronding en de claims-/clarify-/
        // relations-/decks-jobs elk hun rijen. 's Ochtends staat de embed-fout buiten
        // die 15 → paneel leeg, tabel leeg, alles ziet er gezond uit. Exact #282.
        await using var db = NewDb();
        db.RunLogs.Add(new RunLog
        {
            Kind = "embed", Ref = "cards", Status = "error", Detail = "5xx×3",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-6),
        });
        for (var i = 0; i < 25; i++)
            db.RunLogs.Add(new RunLog
            {
                Kind = "job", Ref = $"stap{i}", Status = "ok",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
            });
        await db.SaveChangesAsync();

        // Het 15-rijen-venster ziet de fout niet meer …
        var window = await db.RunLogs.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt).Take(15).ToListAsync();
        Assert.DoesNotContain(window, l => l.Kind == "embed");

        // … de gerichte embed-query wel. Dit is de vorm die /admin/status als
        // `lastEmbed` teruggeeft.
        var lastEmbed = await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "embed")
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new { l.Status, l.Detail, l.CreatedAt })
            .FirstOrDefaultAsync();
        Assert.NotNull(lastEmbed);
        Assert.Equal("error", lastEmbed.Status);
    }

    // ── Instellingen ─────────────────────────────────────────────────────────

    [Fact]
    public void Settings_DefaultIsGehalveerdTenOpzichteVanVoor282()
    {
        Assert.Equal(8, EmbeddingSettings.Default.BatchSize);
        Assert.Equal(8000, EmbeddingSettings.Default.BatchChars);
    }

    [Fact]
    public void Settings_OnzinOfBuitenBereik_ValtTerugOpDeDefault_MaarNietStil()
    {
        // Een typfout in de .env mag de pijplijn niet op 1 tekst per verzoek zetten
        // (traag) of het plafond ontgrendelen (OOM terug). Maar in een PR over stille
        // degradatie mag die terugval óók niet zwijgen (#282-review): dan denk je te
        // hebben bijgesteld terwijl er niets veranderde — de NIGHTLY_ENABLED-klasse
        // fout (#268).
        using var _ = new EnvScope(("EMBED_BATCH_SIZE", "100"), ("EMBED_BATCH_CHARS", "0"));
        var warnings = new List<string>();

        var s = EmbeddingSettings.FromEnvironment(warnings.Add);

        Assert.Equal(EmbeddingSettings.DefaultBatchSize, s.BatchSize);
        Assert.Equal(EmbeddingSettings.DefaultBatchChars, s.BatchChars);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("EMBED_BATCH_SIZE") && w.Contains("100"));
    }

    [Fact]
    public void Settings_OngezetteVlag_WaarschuwtNiet()
    {
        // Niets ingesteld is de normale toestand, geen probleem.
        using var _ = new EnvScope(("EMBED_BATCH_SIZE", ""), ("EMBED_BATCH_CHARS", ""));
        var warnings = new List<string>();

        EmbeddingSettings.FromEnvironment(warnings.Add);

        Assert.Empty(warnings);
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
