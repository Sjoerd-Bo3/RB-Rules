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
public record EmbedRunResult(
    int Embedded, int Skipped, int Failed = 0, string FailureSummary = "")
{
    public bool HasFailures => Failed > 0 || FailureSummary.Length > 0;

    /// <summary>Regel voor run_log/beheer. Noemt uitval expliciet — zwijgen over een
    /// mislukte stap is precies wat #282 opheft.</summary>
    public string Summary => HasFailures
        ? $"{Embedded} geembed, {Skipped} al actueel, {Failed} mislukt ({FailureSummary})"
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
/// weggeschreven.</summary>
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

        foreach (var range in batches)
        {
            var (offset, count) = range.GetOffsetAndLength(todo.Count);
            progress?.Invoke(
                $"embeddings berekenen: kaart {offset + 1}–{offset + count} van {todo.Count}");

            var result = await embeddings.TryEmbedAsync([.. texts.GetRange(offset, count)], ct);
            tally.Add(result.Outcome, count);
            if (!result.Ok)
            {
                // Overslaan, niet afbreken: één omgevallen batch mag de rest van de
                // kaartenset niet meenemen. Deze kaarten blijven Embedding == null.
                continue;
            }

            for (var k = 0; k < count; k++)
            {
                todo[offset + k].Embedding = result.Vectors![k];
                todo[offset + k].EmbeddingModel = EmbeddingConfig.Model;
            }
            await db.SaveChangesAsync(ct);
            embedded += count;
        }

        var run = new EmbedRunResult(embedded, skipped, tally.TextsLost, tally.Summary);
        await LogFailureAsync(run, ct);
        return run;
    }

    /// <summary>Een gefaalde/overgeslagen embed-stap landt ALTIJD in run_log — welke
    /// aanroeper de pijplijn ook startte (beheer-knop, job, scheduler-tick). Precies
    /// dát ontbrak: de scheduler ving de exception op en logde "Ollama onbereikbaar?"
    /// naar de containerlog, waar niemand kijkt. De geslaagde run laat deze pijplijn
    /// wél aan de aanroeper: die schrijft zijn eigen ok-regel, en twee regels per
    /// geslaagde run helpt niemand.</summary>
    private async Task LogFailureAsync(EmbedRunResult run, CancellationToken ct)
    {
        if (!run.HasFailures) return;
        try
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "embed",
                Ref = "cards",
                Status = "error",
                Detail = run.Summary + " — blijven staan voor de volgende run",
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
