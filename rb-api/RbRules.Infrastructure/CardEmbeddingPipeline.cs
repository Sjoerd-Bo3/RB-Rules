using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitslag van één embed-run. <paramref name="Embedded"/> telt sinds #282
/// alleen kaarten die ECHT een vector kregen — daarvoor stond er domweg het aantal
/// te-doen kaarten in, dus een run waarin Ollama halverwege omviel meldde vrolijk
/// "1429 geembed".</summary>
/// <param name="Failed">Kaarten die deze run zonder embedding bleven doordat hun batch
/// faalde. Ze houden <c>Embedding == null</c> en komen bij de volgende run gewoon
/// weer aan de beurt.</param>
/// <param name="FailureSummary">Uitsplitsing per oorzaak ("5xx×3, timeout×1"), leeg
/// als er niets misging.</param>
/// <param name="Aborted">De run stopte vroegtijdig na te veel opeenvolgende gefaalde
/// batches (<see cref="EmbeddingSettings.MaxConsecutiveFailures"/>).</param>
/// <param name="Remaining">Kaarten die niet eens geprobeerd zijn doordat de run
/// afbrak. Los van <paramref name="Failed"/>: dát zijn kaarten die het wél
/// probeerden.</param>
public record EmbedRunResult(
    int Embedded, int Skipped, int Failed = 0, string FailureSummary = "",
    bool Aborted = false, int Remaining = 0)
{
    public bool HasFailures => Failed > 0 || FailureSummary.Length > 0;

    /// <summary>Regel voor run_log/beheer. Noemt uitval expliciet — zwijgen over een
    /// mislukte stap is precies wat #282 opheft. ÉLKE aanroeper hoort deze string te
    /// gebruiken (#282-review): een eigen samenvatting bouwen liet de uitval weer
    /// wegvallen, waardoor jobs een omgevallen stap als geslaagd meldden.</summary>
    public string Summary => HasFailures
        ? $"{Embedded} geembed, {Skipped} al actueel, {Failed} mislukt ({FailureSummary})"
            + (Aborted ? $", afgebroken — {Remaining} niet geprobeerd" : "")
        : $"{Embedded} geembed, {Skipped} al actueel";
}

/// <summary>S1-fundament F2: embed kaarten in batches. Idempotent — alleen
/// kaarten zonder embedding of met een verouderd model (provenance-guard);
/// `force` her-embed alles.
///
/// UITVAL IS DATA (#282): valt Ollama halverwege om (de cgroup-OOM-killer schoot
/// <c>llama-server</c> af op ~2,5 GB), dan slaat deze pijplijn de gefaalde batch over,
/// gaat door met de rest, en meldt achteraf hoeveel kaarten om welke reden zijn
/// blijven liggen — mét een run_log-regel, zodat het in beheer zichtbaar is en niet
/// alleen in <c>dmesg</c>. De gefaalde kaarten houden <c>Embedding == null</c> en
/// worden dus vanzelf opnieuw opgepakt; er wordt nooit een half resultaat
/// weggeschreven.
///
/// MAAR NIET EINDELOOS DOORPROBEREN (#282-review): vóór #282 brak de pijplijn bij de
/// eerste fout af, dus een dode Ollama kostte één verzoek. Doorlopen-per-batch mag
/// dat niet in een urenlange hangpartij veranderen — bij uitkomst
/// <see cref="EmbedCallOutcome.Timeout"/> (5 minuten, geen retry) zou 179 batches
/// ≈ 15 uur zijn, en met de één-job-gate van <c>JobRunner</c> plus de synchrone
/// aanroep in <c>ScanScheduler</c> ligt dan de hele beheer- én schedulerlus stil.
/// Daarom stopt de run na <see cref="EmbeddingSettings.MaxConsecutiveFailures"/>
/// opeenvolgende gefaalde batches; een geslaagde batch zet die teller terug, zodat
/// één hik een lange run niet afkapt.</summary>
public class CardEmbeddingPipeline(
    RbRulesDbContext db, EmbeddingService embeddings, EmbeddingSettings? settings = null)
{
    private readonly EmbeddingSettings _settings = settings ?? EmbeddingSettings.Default;

    public async Task<EmbedRunResult> RunAsync(
        bool force = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        // Varianten (alt-art/promo) slaan we over: identieke tekst, en zo
        // blijven 'vergelijkbare kaarten' vrij van duplicaten. Alleen de
        // te-embedden kaarten laden (review-fix #43: niet elk uur alle
        // 1024-dim vectoren naar de client trekken om te filteren).
        var model = EmbeddingConfig.Model;
        var baseQuery = db.Cards.Where(c => c.VariantOf == null);
        var total = await baseQuery.CountAsync(ct);
        var todo = await (force
                ? baseQuery
                : baseQuery.Where(c => c.Embedding == null || c.EmbeddingModel != model))
            .OrderBy(c => c.RiftboundId)
            .ToListAsync(ct);
        var skipped = total - todo.Count;

        var texts = todo.Select(CardText.Compose).ToList();
        var batches = EmbedBatching.Split(texts, _settings.BatchSize, _settings.BatchChars);
        var tally = new EmbedOutcomeTally();
        var embedded = 0;
        var attempted = 0;
        var consecutiveFailures = 0;
        var aborted = false;

        try
        {
            foreach (var range in batches)
            {
                var (offset, count) = range.GetOffsetAndLength(todo.Count);
                progress?.Invoke(
                    $"embeddings berekenen: kaart {offset + 1}–{offset + count} van {todo.Count}");

                var result = await embeddings.TryEmbedAsync([.. texts.GetRange(offset, count)], ct);
                tally.Add(result.Outcome, count);
                attempted += count;
                if (!result.Ok)
                {
                    // Overslaan, niet meteen afbreken: één omgevallen batch mag de rest
                    // van de kaartenset niet meenemen. Deze kaarten blijven null.
                    if (++consecutiveFailures >= _settings.MaxConsecutiveFailures)
                    {
                        // Dit is niet één batch die hapert, dit is Ollama die eruit
                        // ligt. Doorgaan kost alleen tijd en houdt de job-gate bezet.
                        aborted = true;
                        break;
                    }
                    continue;
                }

                consecutiveFailures = 0;
                for (var k = 0; k < count; k++)
                {
                    todo[offset + k].Embedding = result.Vectors![k];
                    todo[offset + k].EmbeddingModel = EmbeddingConfig.Model;
                }
                await db.SaveChangesAsync(ct);
                embedded += count;
            }
        }
        catch (OperationCanceledException)
        {
            // Afbreken mag de meting niet weggooien (#282-review): batches die vóór de
            // annulering faalden verdienen hun regel. Zelfde les als JobRunner's
            // "bewust zonder token"-afronding — daarom CancellationToken.None. Daarna
            // gewoon doorgooien; JobRunner zet de run op 'cancelled'.
            var partial = Result(embedded, skipped, tally, aborted: false, todo.Count - attempted);
            if (partial.HasFailures) await LogRunAsync(partial, CancellationToken.None);
            throw;
        }

        var run = Result(embedded, skipped, tally, aborted, todo.Count - attempted);
        // Niets te doen én niets misgegaan = geen nieuws; anders zou de scheduler-tick
        // elk uur een lege regel schrijven.
        if (todo.Count > 0 || run.HasFailures) await LogRunAsync(run, ct);
        return run;
    }

    private static EmbedRunResult Result(
        int embedded, int skipped, EmbedOutcomeTally tally, bool aborted, int remaining) =>
        new(embedded, skipped, tally.TextsLost, tally.Summary, aborted, remaining);

    /// <summary>Elke run mét werk landt in run_log — welke aanroeper de pijplijn ook
    /// startte (beheer-knop, job, scheduler-tick).
    ///
    /// Dat de GESLAAGDE run óók een regel schrijft is geen ruis maar de
    /// herstel-melding (#282-review): géén enkel vanuit de UI bereikbaar pad schreef
    /// een embed-ok-regel (rb-web post alleen <c>/api/admin/jobs/{name}</c>, en
    /// <c>JobRunner</c> logt <c>Kind = "job"</c>; de scheduler logde bij succes
    /// niets). Daardoor bleef een oude foutregel eeuwig de nieuwste embed-regel en
    /// doofde het alarm in beheer nooit door herstel — alleen door veroudering.</summary>
    private async Task LogRunAsync(EmbedRunResult run, CancellationToken ct)
    {
        try
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "embed",
                Ref = "cards",
                Status = run.HasFailures ? "error" : "ok",
                Detail = run.HasFailures
                    ? run.Summary + " — blijven staan voor de volgende run"
                    : run.Summary,
            });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Loggen mag een run-afronding nooit blokkeren (conventie): de uitslag
            // gaat hoe dan ook terug naar de aanroeper, die hem óók rapporteert.
        }
    }
}
