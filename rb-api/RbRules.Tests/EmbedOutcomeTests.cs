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
        Assert.Equal(6000, EmbeddingSettings.Default.BatchChars);
    }

    // ── DE REGRESSIETEST VAN #293 ────────────────────────────────────────────
    // #282 koos EMBED_BATCH_CHARS=8000 op gevoel en dat bleek EXACT de waarde waarop
    // llama-server omvalt: de begrenzing stond op de klip in plaats van eronder.
    // Meetreeks op productie (POST /api/embed, bge-m3, rb-v2-ollama):
    //   500 / 2400 / 3908 / 4500 / 5000 / 6000 / 7000 → HTTP 200
    //   8000                                          → HTTP 400, 3 van de 3
    //   20000                                         → HTTP 400
    // Foutbody `do embedding request: … EOF`; `dmesg | grep -c llama-server` liep
    // tijdens de reeks van 10 naar 30 — elke 400 is één OOM-kill.
    // Deze twee tests worden rood zodra de default weer boven die gemeten grens komt.

    [Fact]
    public void Settings_DefaultBlijftOnderDeGemetenKlip()
    {
        // UITGESCHREVEN LITERALS, met opzet. Vergelijken met de constanten uit
        // EmbeddingSettings zou zelfreferentieel zijn: wie de default op 8000 zet én de
        // meetconstanten meeverschuift (waar de doc-comment "alleen na een nieuwe
        // meting" letterlijk toe uitnodigt) houdt dan een groene test met exact de
        // waarde die OOM-kilt. De 7000/8000 hieronder zijn WAARNEMINGEN aan een draaiend
        // systeem, geen ontwerpkeuze — ze horen niet mee te bewegen met de code, en een
        // nieuwe meting hoort deze test bewust rood te maken zodat iemand de reeks
        // opnieuw langsloopt in plaats van een getal te verzetten.
        Assert.Equal(7000, EmbeddingSettings.MeasuredSafeMaxBatchChars);
        Assert.Equal(8000, EmbeddingSettings.MeasuredFailingBatchChars);

        Assert.True(
            EmbeddingSettings.DefaultBatchChars <= 7000,
            $"EMBED_BATCH_CHARS-default {EmbeddingSettings.DefaultBatchChars} ligt boven de "
            + "hoogste GEMETEN veilige waarde (7000). Bij 8000 tekens sterft llama-server "
            + "aan een OOM-kill (#293) — verhogen mag pas ná een nieuwe meting én een hogere "
            + "memory:-cap voor rb-v2-ollama in deploy/server-setup-v2/docker-compose.yml.");

        // En niet vlák onder de klifrand: 7000 is de laatste waarde die het HAALDE,
        // dus de echte grens ligt daar ergens boven en schuift mee met wat Postgres/
        // Neo4j/rb-ai op dat moment van de 8 GB-VM claimen.
        Assert.True(
            EmbeddingSettings.DefaultBatchChars <= 6300,
            $"De default ({EmbeddingSettings.DefaultBatchChars}) hoort met marge onder de "
            + "klip te liggen (≤ 6300, oftewel ~10% onder de laatste geslaagde meting), "
            + "niet er vlak onder.");
    }

    [Fact]
    public void Settings_ClampLaatDeDefaultDoor_EnWeertDeGemetenKlipwaarde()
    {
        // De clamp mag de default niet stilletjes wegfilteren — dan zou een expliciete
        // EMBED_BATCH_CHARS=6000 in de .env terugvallen op … 6000, maar mét een
        // waarschuwing die niets betekent. Dit is óók de vangnettest voor de
        // omgekeerde fout: zet de default boven MeasuredSafeMaxBatchChars en deze
        // test valt om, want dan weigert de clamp zijn eigen fallback-waarde.
        using (new EnvScope(("EMBED_BATCH_CHARS", $"{EmbeddingSettings.DefaultBatchChars}")))
        {
            var warnings = new List<string>();
            var s = EmbeddingSettings.FromEnvironment(warnings.Add);

            Assert.Equal(EmbeddingSettings.DefaultBatchChars, s.BatchChars);
            Assert.Empty(warnings);
        }

        // De gemeten klipwaarde zelf gaat er níét meer in — het plafond is sinds #293
        // de meetwaarde en geen ruime 100000 meer. Wel luid: stil terugvallen is de
        // NIGHTLY_ENABLED-klasse fout (#268).
        using (new EnvScope(
            ("EMBED_BATCH_CHARS", $"{EmbeddingSettings.MeasuredFailingBatchChars}")))
        {
            var warnings = new List<string>();
            var s = EmbeddingSettings.FromEnvironment(warnings.Add);

            Assert.Equal(EmbeddingSettings.DefaultBatchChars, s.BatchChars);
            Assert.Contains(warnings, w => w.Contains("EMBED_BATCH_CHARS"));
        }
    }

    // ── De 4xx-hint stuurde de beheerder de verkeerde kant op ────────────────

    [Fact]
    public void Tally_4xxNoemtNietLangerHetModel_EnLaatOllamaZelfAanHetWoord()
    {
        // "4xx (model niet gepulld?)" was de enige aanwijzing in de job-melding, en
        // hij was fout: bge-m3:latest stond er gewoon (1,2 GB). De echte oorzaak was
        // een OOM-kill van llama-server onder een te grote invoer.
        var tally = new EmbedOutcomeTally();
        tally.Add(EmbedCallOutcome.ClientError, 4,
            """Ollama antwoordde 400: {"error":"do embedding request: Post \"http://127.0.0.1:43215/v1/embeddings\": EOF"}""");

        Assert.DoesNotContain("gepulld", tally.Summary);
        Assert.Contains("4xx", tally.Summary);
        // De ruwe foutbody erbij: onze duiding is een hypothese, Ollama's eigen
        // woorden zijn het bewijs.
        Assert.Contains("do embedding request", tally.Summary);
        Assert.Contains("EOF", tally.Summary);
    }

    [Fact]
    public void Tally_HerhaaltDezelfdeFoutbodyNietVeertigKeer()
    {
        var tally = new EmbedOutcomeTally();
        for (var i = 0; i < 40; i++)
            tally.Add(EmbedCallOutcome.ClientError, 8, "Ollama antwoordde 400: EOF");

        Assert.Equal("4xx (backend overleden? te grote invoer?)×40 "
            + "[Ollama antwoordde 400: EOF]", tally.Summary);
    }

    [Fact]
    public async Task Embed_4xx_NeemtDeRuweFoutbodyMeeNaarHetRunLog()
    {
        // De hele keten: Ollama's body → EmbedBatchResult.Error → tally → run_log.
        // Zonder deze keten staat er alleen "4xx×1" en begint het gokken opnieuw.
        await using var db = NewDb();
        db.Cards.AddRange(Cards(4));
        await db.SaveChangesAsync();

        var r = await Pipeline(db, _ => Json(HttpStatusCode.BadRequest,
            """{"error":"do embedding request: EOF"}""")).RunAsync();

        Assert.Contains("do embedding request", r.FailureSummary);
        var log = Assert.Single(db.RunLogs.Where(l => l.Kind == "embed"));
        Assert.Contains("do embedding request", log.Detail);
        Assert.DoesNotContain("gepulld", log.Detail);
    }

    // ── Eén item boven het budget: kappen, en het zeggen ─────────────────────

    [Fact]
    public void Cap_KortGenoegeTekstenBlijvenOngemoeid()
    {
        var texts = new List<string> { "kort", new('x', 6000) };

        var capped = EmbedBatching.CapItems(texts, 6000);

        Assert.Equal(0, capped.CappedCount);
        Assert.Equal(texts, capped.Texts);
        Assert.Equal(6000, capped.LongestOriginal);
    }

    [Fact]
    public void Cap_TeLangeTekstWordtIngekort_EnGeteld()
    {
        var texts = new List<string> { "kort", new('x', 20_000), new('y', 6001) };

        var capped = EmbedBatching.CapItems(texts, 6000);

        Assert.Equal(2, capped.CappedCount);
        Assert.Equal(20_000, capped.LongestOriginal);
        Assert.Equal([4, 6000, 6000], capped.Texts.Select(t => t.Length));
        Assert.StartsWith("xxx", capped.Texts[1]);   // begin behouden, staart eraf
    }

    [Fact]
    public void Cap_KnipptNooitMiddenInEenSurrogatePair()
    {
        // Een halve surrogate pair is geen geldige UTF-16 en zou als vervangingsteken
        // de JSON in gaan. Budget 5, tekst "aaaa" + 🂡 (2 chars) → knip op 4.
        var texts = new List<string> { "aaaa\U0001F0A1" };

        var capped = EmbedBatching.CapItems(texts, 5);

        Assert.Equal("aaaa", capped.Texts[0]);
        Assert.Equal(1, capped.CappedCount);
    }

    [Fact]
    public void Cap_MaaktDeBatchgarantieHard_GeenEnkelVerzoekBovenHetBudget()
    {
        // Dit is de kern van #293. EmbedBatching.Split gaf een uitschieter bewust een
        // eigen verzoek (nooit weglaten, #282) — maar dat ene verzoek lag dan alsnog
        // boven de klip en viel elke run opnieuw om. Met CapItems ervóór is élke
        // batch gegarandeerd binnen het budget.
        var texts = new List<string> { "kort", new('x', 20_000), "kort", new('y', 9000) };

        var capped = EmbedBatching.CapItems(texts, 6000);
        var batches = EmbedBatching.Split(capped.Texts, maxCount: 8, maxChars: 6000);

        Assert.All(batches, b =>
        {
            var (offset, count) = b.GetOffsetAndLength(capped.Texts.Count);
            Assert.True(capped.Texts.Skip(offset).Take(count).Sum(t => t.Length) <= 6000);
        });
        // En er is nog steeds niets weggelaten: 4 teksten in, 4 teksten verdeeld.
        Assert.Equal(4, batches.Sum(b => b.GetOffsetAndLength(texts.Count).Length));
    }

    [Fact]
    public async Task Embed_KaartBovenHetBudget_WordtGekapt_MaarNooitStil()
    {
        // Stil afkappen is precies de klasse fout die #282/#284 wegnamen, dus het
        // aantal en de kaplengte horen in de run-melding.
        await using var db = NewDb();
        var origineel = string.Concat(Enumerable.Repeat("woord ", 3000));
        db.Cards.Add(new Card
        {
            RiftboundId = "ogn-999", Name = "Reuzentekst", Type = "Unit",
            TextPlain = origineel,
        });
        await db.SaveChangesAsync();

        var sent = new List<int>();
        var r = await Pipeline(db, req =>
        {
            sent.AddRange(InputLengths(req));
            return OkEmbeddings(BatchTexts(req));
        }).RunAsync();

        // 1. Wat er de deur uit ging past binnen het budget — geen OOM-kill meer.
        Assert.All(sent, len => Assert.True(len <= EmbeddingSettings.DefaultBatchChars));
        // 2. De kaart is wél geembed; overslaan zou hem elke run opnieuw laten falen.
        Assert.Equal(1, r.Embedded);
        Assert.Equal(1, await db.Cards.CountAsync(c => c.Embedding != null));
        // 3. De OPGESLAGEN tekst is niet aangeraakt — alleen de embed-invoer is gekort.
        //    Dit is de riskantste claim van #293: hij staat in de PR-body, in
        //    ARCHITECTURE (Q14b) en in de run-melding die de beheerder leest ("de
        //    opgeslagen tekst blijft volledig"), en zonder deze assertie is een
        //    `card.TextPlain = texts[i]` in de embed-lus een groene regressie.
        var kaart = await db.Cards.SingleAsync();
        Assert.Equal(origineel, kaart.TextPlain);
        Assert.True(kaart.TextPlain!.Length > EmbeddingSettings.DefaultBatchChars,
            "de testkaart moet juist LANGER zijn dan het budget, anders bewijst de "
            + "assertie hierboven niets");
        // 4. En het staat er met zoveel woorden.
        Assert.Equal(1, r.Capped);
        Assert.Contains("afgekapt", r.Summary);
        Assert.Contains($"{EmbeddingSettings.DefaultBatchChars}", r.Summary);
        Assert.Contains("afgekapt", Assert.Single(db.RunLogs).Detail);
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

    /// <summary>De lengte van elke tekst die daadwerkelijk de deur uit ging — zo meten
    /// we de kap op de WIRE en niet alleen in de rekensom ernaartoe (#293).</summary>
    private static IEnumerable<int> InputLengths(HttpRequestMessage req)
    {
        var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return [.. System.Text.Json.JsonDocument.Parse(body)
            .RootElement.GetProperty("input").EnumerateArray()
            .Select(e => e.GetString()!.Length)];
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
