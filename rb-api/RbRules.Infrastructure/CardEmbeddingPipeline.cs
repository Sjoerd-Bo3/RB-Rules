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
/// <param name="Capped">Kaartteksten die vóór het embedden zijn ingekort omdat ze op
/// zichzelf boven het tekenbudget uitkwamen (#293). Hoort in de melding: afkappen is
/// verlies van invoer, en stil verlies is precies wat #282/#284 wegnamen.</param>
/// <param name="CappedAt">Op hoeveel tekens er gekapt is — het budget van dat moment,
/// zodat de melding klopt ook als <c>EMBED_BATCH_CHARS</c> afwijkt van de default.</param>
/// <param name="CappedLongest">De langste ORIGINELE kaarttekst van deze run (#302).
/// Alleen zinvol naast <paramref name="CappedAt"/>: "afgekapt op 6000 tekens (langste
/// invoer 20000)" zegt hoe ver we eroverheen zaten en dus of het budget knelt of ruim
/// zit; alleen de kaplengte zegt dat niet.</param>
public record EmbedRunResult(
    int Embedded, int Skipped, int Failed = 0, string FailureSummary = "",
    bool Aborted = false, int Remaining = 0, int Capped = 0, int CappedAt = 0,
    int CappedLongest = 0)
{
    public bool HasFailures => Failed > 0 || FailureSummary.Length > 0;

    /// <summary>Regel voor run_log/beheer. Noemt uitval expliciet — zwijgen over een
    /// mislukte stap is precies wat #282 opheft. ÉLKE aanroeper hoort deze string te
    /// gebruiken (#282-review): een eigen samenvatting bouwen liet de uitval weer
    /// wegvallen, waardoor jobs een omgevallen stap als geslaagd meldden.</summary>
    public string Summary =>
        (HasFailures
            ? $"{Embedded} geembed, {Skipped} al actueel, {Failed} mislukt ({FailureSummary})"
                + (Aborted ? $", afgebroken — {Remaining} niet geprobeerd" : "")
            : $"{Embedded} geembed, {Skipped} al actueel")
        + (Capped > 0
            ? $" · {Capped} kaarttekst(en) afgekapt op {CappedAt} tekens "
                + $"(langste invoer {CappedLongest})"
            : "");
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

        // Kap eerst de uitschieters (#293): een kaarttekst die op zichzelf boven het
        // tekenbudget uitkomt zou als solo-verzoek alsnog llama-server omver duwen, en
        // omdat een kaart zonder embedding elke run opnieuw aan de beurt komt is dat
        // een OOM-kill per run. Het budget is óók de itemgrens, dus élk verzoek blijft
        // binnen het gemeten veilige bereik.
        //
        // De kap zélf is sinds #301 een no-op op deze plek — EmbeddingService kapt
        // hoe dan ook, ongeacht de aanroeper. Hij blijft staan omdat de pijplijn moet
        // weten WELKE kaart gekapt is: dat legt hij hieronder per rij vast
        // (Card.EmbeddingTruncatedAt, #299). Een aantal terug uit de service zou dat
        // niet kunnen — dan weet je dát er gekapt is, niet bij wie.
        var originals = todo.Select(CardText.Compose).ToList();
        var capped = EmbedBatching.CapItems(originals, _settings.BatchChars);
        var texts = capped.Texts;
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

                var result = await embeddings.TryEmbedAsync(
                    [.. texts.Skip(offset).Take(count)], ct);
                tally.Add(result.Outcome, count, result.Error);
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
                    // Provenance op de RIJ (#299): deze vector kent alleen de eerste N
                    // tekens, en dat moet over een half jaar nog te zien zijn. Altijd
                    // schrijven, óók null — een kaart die na een budgetverhoging wél
                    // past zou anders voor eeuwig als partieel gemarkeerd blijven.
                    todo[offset + k].EmbeddingTruncatedAt =
                        texts[offset + k].Length < originals[offset + k].Length
                            ? texts[offset + k].Length
                            : null;
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
            var partial = Result(
                embedded, skipped, tally, aborted: false, todo.Count - attempted, capped);
            if (partial.HasFailures) await LogRunAsync(partial, CancellationToken.None);
            throw;
        }

        var run = Result(
            embedded, skipped, tally, aborted, todo.Count - attempted, capped);
        // Niets te doen én niets misgegaan = geen nieuws; anders zou de scheduler-tick
        // elk uur een lege regel schrijven.
        if (todo.Count > 0 || run.HasFailures) await LogRunAsync(run, ct);
        return run;
    }

    private EmbedRunResult Result(
        int embedded, int skipped, EmbedOutcomeTally tally, bool aborted, int remaining,
        EmbedBatching.CappedItems capped) =>
        new(embedded, skipped, tally.TextsLost, tally.Summary, aborted, remaining,
            capped.CappedCount,
            capped.CappedCount > 0 ? _settings.BatchChars : 0,
            capped.CappedCount > 0 ? capped.LongestOriginal : 0);

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
                // "warn" (#299): een run die kapte maar verder slaagde is géén fout —
                // de kaart is geembed — maar hij is ook geen "ok". Onder "ok" was de
                // kapping in beheer ONZICHTBAAR: het paneel hangt aan status 'error',
                // dus de melding stond alleen in de logtabel en zakte daar na 15
                // rijen uit het venster. Dat is de "alarm dat alleen door veroudering
                // dooft"-klasse uit de #282-review, nu aan de andere kant: niet een
                // fout die wegzakt, maar een waarschuwing die nooit opkwam.
                Status = run.HasFailures ? "error" : run.Capped > 0 ? "warn" : "ok",
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
