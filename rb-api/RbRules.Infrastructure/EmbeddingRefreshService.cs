using Microsoft.EntityFrameworkCore;
using Pgvector;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van één laag in een verversingsrun (#382).</summary>
public sealed record EmbeddingRefreshLayer(string Layer, EmbedRunResult Result);

/// <summary>Uitkomst van een hele verversingsrun over alle vectorlagen.</summary>
public sealed record EmbeddingRefreshResult(IReadOnlyList<EmbeddingRefreshLayer> Layers)
{
    public bool HasFailures => Layers.Any(l => l.Result.HasFailures);
    public int Embedded => Layers.Sum(l => l.Result.Embedded);

    /// <summary>Eén regel voor run_log/beheer: per laag wat er gebeurde, en niets
    /// over lagen waar niets te doen was — anders is de regel elke nacht een
    /// opsomming van nullen waar de ene echte melding in verdwijnt.</summary>
    public string Summary
    {
        get
        {
            var parts = Layers
                .Where(l => l.Result.Embedded > 0 || l.Result.HasFailures)
                .Select(l => $"{l.Layer}: {l.Result.Summary}")
                .ToList();
            return parts.Count == 0 ? "alle lagen actueel" : string.Join(" · ", parts);
        }
    }
}

/// <summary>Her-embed over álle vectorlagen na een modelwissel (#382).
///
/// AANLEIDING: alleen <see cref="CardEmbeddingPipeline"/> las <c>EmbeddingModel</c>
/// terug als selectiecriterium. De andere vier lagen — regelchunks, primer,
/// rulings, claims — schreven de modelnaam wél, maar vergeleken hem nooit: na een
/// modelwissel bleven hun oude vectoren staan, terwijl de vraagvector met het
/// nieuwe model gemaakt werd. Twee ruimten door elkaar, en niets werd rood
/// (vastgesteld bij de feitencheck van de uitlegpagina, PR #378). Bij rulings
/// werd de modelnaam op drie van de vier schrijfpaden zelfs helemaal niet gezet.
///
/// WAT DEZE SERVICE DOET: per laag de rijen die een vector HEBBEN maar een
/// afwijkende of ontbrekende modelstempel, opnieuw embedden met de tekstvorm die
/// het schrijfpad van die laag ook gebruikt, en de stempel zetten. Rijen zónder
/// vector blijven bewust liggen — die horen bij hun eigen aanmaakpad (een primer
/// zonder vector is een draft die nog niet is goedgekeurd, een ruling zonder
/// vector is nog niet geverifieerd). De kaartlaag zit in haar eigen pijplijn.
///
/// DEZELFDE DISCIPLINE ALS DE KAARTPIJPLIJN: batches binnen het gemeten budget
/// (<see cref="EmbedBatching"/>, #293), kaplengte op de rij waar de laag die
/// kolom heeft (#299), doorlopen per batch maar stoppen na een handvol
/// opeenvolgende fouten (#282-review), en een eigen run_log-regel per laag —
/// ook bij succes, want anders dooft een oud alarm alleen door veroudering.</summary>
public class EmbeddingRefreshService(
    RbRulesDbContext db, EmbeddingService embeddings, EmbeddingSettings? settings = null)
{
    private readonly EmbeddingSettings _settings = settings ?? EmbeddingSettings.Default;

    public async Task<EmbeddingRefreshResult> RunAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var model = EmbeddingConfig.Model;
        var layers = new List<EmbeddingRefreshLayer>();

        // Regelchunks: de tekst zelf. Zelfde invoer als RuleChunkPipeline.
        var chunks = await db.RuleChunks
            .Where(c => c.Embedding != null && c.EmbeddingModel != model)
            .OrderBy(c => c.Id).ToListAsync(ct);
        layers.Add(await RefreshLayerAsync("regels", chunks,
            c => c.Text,
            (c, v, cut) => { c.Embedding = v; c.EmbeddingModel = model; c.EmbeddingTruncatedAt = cut; },
            progress, ct));

        // Primer: titel + body, zoals PrimerService bij (her)generatie.
        var docs = await db.KnowledgeDocs
            .Where(d => d.Embedding != null && d.EmbeddingModel != model)
            .OrderBy(d => d.Id).ToListAsync(ct);
        layers.Add(await RefreshLayerAsync("primer", docs,
            d => $"{d.Title}\n{d.Body}",
            (d, v, cut) => { d.Embedding = v; d.EmbeddingModel = model; d.EmbeddingTruncatedAt = cut; },
            progress, ct));

        // Rulings: vraag + tekst, de dominante vorm op de schrijfpaden (verify in
        // beheer, chat-ruling, reviewnotitie). Geen kaplengte-kolom op deze laag.
        var rulings = await db.Corrections
            .Where(c => c.Embedding != null && c.EmbeddingModel != model)
            .OrderBy(c => c.Id).ToListAsync(ct);
        layers.Add(await RefreshLayerAsync("rulings", rulings,
            c => $"{c.Question}\n{c.Text}",
            (c, v, _) => { c.Embedding = v; c.EmbeddingModel = model; },
            progress, ct));

        // Claims: onderwerp-ref + bewering, zoals ClaimMiningService.
        var claims = await db.Claims
            .Where(c => c.Embedding != null && c.EmbeddingModel != model)
            .OrderBy(c => c.Id).ToListAsync(ct);
        layers.Add(await RefreshLayerAsync("claims", claims,
            c => $"{c.TopicRef}\n{c.Statement}",
            (c, v, _) => { c.Embedding = v; c.EmbeddingModel = model; },
            progress, ct));

        return new EmbeddingRefreshResult(layers);
    }

    private async Task<EmbeddingRefreshLayer> RefreshLayerAsync<T>(
        string layer, List<T> todo, Func<T, string> text, Action<T, Vector, int?> apply,
        Action<string>? progress, CancellationToken ct)
    {
        if (todo.Count == 0)
            return new(layer, new EmbedRunResult(0, 0));

        var originals = todo.Select(text).ToList();
        var capped = EmbedBatching.CapItems(originals, _settings.BatchChars);
        var texts = capped.Texts;
        var batches = EmbedBatching.Split(texts, _settings.BatchSize, _settings.BatchChars);
        var tally = new EmbedOutcomeTally();
        var embedded = 0;
        var attempted = 0;
        var consecutiveFailures = 0;
        var aborted = false;

        foreach (var range in batches)
        {
            var (offset, count) = range.GetOffsetAndLength(todo.Count);
            progress?.Invoke($"her-embed {layer}: {offset + 1}–{offset + count} van {todo.Count}");

            var result = await embeddings.TryEmbedAsync([.. texts.Skip(offset).Take(count)], ct);
            tally.Add(result.Outcome, count, result.Error);
            attempted += count;
            if (!result.Ok)
            {
                if (++consecutiveFailures >= _settings.MaxConsecutiveFailures) { aborted = true; break; }
                continue;
            }
            consecutiveFailures = 0;
            for (var k = 0; k < count; k++)
            {
                var cut = texts[offset + k].Length < originals[offset + k].Length
                    ? texts[offset + k].Length
                    : (int?)null;
                apply(todo[offset + k], result.Vectors![k], cut);
            }
            await db.SaveChangesAsync(ct);
            embedded += count;
        }

        var run = new EmbedRunResult(
            embedded, 0, tally.TextsLost, tally.Summary, aborted, todo.Count - attempted,
            capped.CappedCount,
            capped.CappedCount > 0 ? _settings.BatchChars : 0,
            capped.CappedCount > 0 ? capped.LongestOriginal : 0);
        await LogRunAsync(layer, run, ct);
        return new(layer, run);
    }

    private async Task LogRunAsync(string layer, EmbedRunResult run, CancellationToken ct)
    {
        try
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "embed",
                Ref = layer,
                Status = run.HasFailures ? "error" : run.Capped > 0 ? "warn" : "ok",
                Detail = run.HasFailures ? run.Summary + " — blijven staan voor de volgende run" : run.Summary,
            });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Loggen mag een run-afronding nooit blokkeren (conventie).
        }
    }
}
