using System.Text;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Eval-gevallen uit echt verkeer (#387): een beantwoorde vraag (een
/// <c>AskTrace</c> of een <c>answer_memory</c>-rij) wordt een
/// <see cref="EvalCase"/> waarvan de VERWACHTE citaties de citaties van dat
/// antwoord zijn. Zo groeit de set uit vragen die spelers echt stellen, in
/// plaats van uit verzonnen voorbeelden. Puur: id-vocabulaire, vraagklasse-
/// mapping en het parsen van de §-lijst uit een trace zijn hier met unit-tests
/// vast te zetten; de I/O zit in <c>EvalCaseService</c>.
///
/// ID-VOCABULAIRE: <c>section:{code}</c> (en <c>card:{riftboundId}</c> voor
/// later, als een geval ook kaartverwachtingen krijgt). Bewust zonder bron-id
/// in de sectie-ref (anders dan <c>BrainRef</c>): een citatie draagt in het
/// antwoord geen bron-id, en de harness vergelijkt alleen verzamelingen van
/// strings — de vocabulaire hoeft maar consistent te zijn tussen
/// <see cref="Draft"/> en <see cref="EvalRunMapping.ToRunResult"/>.</summary>
public static partial class EvalCasePromotion
{
    public const string OriginTrace = "trace";
    public const string OriginMemory = "memory";

    public static string SectionId(string code) => $"section:{code}";
    public static string CardId(string riftboundId) => $"card:{riftboundId}";

    [GeneratedRegex(@"§\s*([0-9A-Za-z][0-9A-Za-z.]*)")]
    private static partial Regex SectionMarker();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketMarker();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();

    /// <summary>De §-codes uit <c>AskTrace.Sections</c> ("[kanaal-uitval: …]
    /// §101, §466.2.c") als sectie-ids; de blokhaak-markers van #100/#152/#364
    /// worden overgeslagen. Volgorde behouden, dubbelen weg.</summary>
    public static IReadOnlyList<string> SectionIdsFromTrace(string? sections)
    {
        if (string.IsNullOrWhiteSpace(sections)) return [];
        var clean = BracketMarker().Replace(sections, " ");
        return [.. SectionMarker().Matches(clean)
            .Select(m => m.Groups[1].Value.TrimEnd('.'))
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(SectionId)];
    }

    /// <summary>Vraagklasse (MultiHop-RAG-as, <see cref="EvalQueryType"/>) uit
    /// het router-vraagtype plus de vraagtekst: een vergelijkingsvraag herken
    /// je aan "verschil"/"versus", een tijdgebonden aan errata-woorden; Ruling
    /// en Interactie vragen redeneren (Inference); de rest is één feit.</summary>
    public static EvalQueryType QueryTypeFor(string? questionType, string question)
    {
        var q = question.ToLowerInvariant();
        if (q.Contains("verschil") || q.Contains(" versus ") || q.Contains(" vs ") || q.Contains(" of ") && q.Contains("beter"))
            return EvalQueryType.Comparison;
        if (q.Contains("errat") || q.Contains("sinds ") || q.Contains("vroeger") || q.Contains("voorheen") || q.Contains("nog steeds"))
            return EvalQueryType.Temporal;
        return questionType is "Ruling" or "Interactie" ? EvalQueryType.Inference : EvalQueryType.Factoid;
    }

    /// <summary>Stabiel, leesbaar id: "eval-{slug van de vraag}-{6 hex van de
    /// SHA-256}". Dezelfde vraag geeft hetzelfde id — een tweede promotie van
    /// dezelfde vraag is dan een dubbel dat de service kan weigeren.</summary>
    public static string CaseId(string question)
    {
        var slug = NonSlug().Replace(question.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40)
        {
            var cut = slug.LastIndexOf('-', 40);
            slug = cut > 10 ? slug[..cut] : slug[..40];
        }
        var hash = TextUtils.Sha256(question.Trim().ToLowerInvariant())[..6];
        return $"eval-{slug}-{hash}";
    }

    /// <summary>Een nieuw geval als <see cref="EvalStatus.Shadow"/>: het scoort
    /// en rapporteert, maar gate't pas nadat een beheerder het activeert
    /// (cold-start-regel van #231). Verwachte citaties = gold-support: bij
    /// promotie uit verkeer is er geen aparte subgraaf-verwachting.</summary>
    public static EvalCase Draft(
        string question, string? questionType,
        IEnumerable<string> citationIds, DateOnly today)
    {
        var ids = citationIds.Distinct(StringComparer.Ordinal).ToList();
        return new EvalCase
        {
            Id = CaseId(question),
            Question = question.Trim(),
            QueryType = QueryTypeFor(questionType, question),
            Status = EvalStatus.Shadow,
            ValidFrom = today,
            GoldSupport = ids,
            ExpectedCitations = ids,
        };
    }
}

/// <summary>Van een echt /ask-antwoord naar de geabstraheerde
/// <see cref="EvalRunResult"/> van de harness (#387). Retrieval en citaties
/// vallen hier samen: /ask heeft geen aparte subgraaf-notie, de citaties zíjn
/// wat de retrieval opleverde. Alleen §-secties tellen: de "betrokken kaarten"
/// van een antwoord zijn herkend in vraag+antwoord, niet geciteerd — ze
/// meetellen als citatie maakte elk geval een "verzonnen citatie" (gevonden in
/// de eerste test). Verboden claims worden lexicaal gedetecteerd — de
/// claimtekst komt letterlijk (hoofdletterongevoelig) in het antwoord voor.</summary>
public static class EvalRunMapping
{
    public static EvalRunResult ToRunResult(
        IEnumerable<string?> citationSections, string answer,
        IReadOnlyList<ForbiddenClaim> forbidden)
    {
        var ids = citationSections
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => EvalCasePromotion.SectionId(s!))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var produced = forbidden
            .Where(f => !string.IsNullOrWhiteSpace(f.Text)
                        && answer.Contains(f.Text, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Id)
            .ToList();
        return new EvalRunResult
        {
            RetrievedSupport = ids,
            Citations = ids,
            ProducedClaims = produced,
        };
    }

    /// <summary>Compacte, leesbare samenvatting per vraagklasse voor het
    /// run-memo: gemiddelde recall en citatieprecisie over de meetellende én
    /// shadow-samples.</summary>
    public static string ClassSummary(IReadOnlyList<ClassifiedSample> samples)
    {
        var sb = new StringBuilder();
        foreach (var g in samples.GroupBy(s => s.QueryType).OrderBy(g => g.Key))
        {
            double Mean(string metric)
            {
                var vals = g.Where(s => s.Metric == metric).Select(s => s.Value).ToList();
                return vals.Count == 0 ? 1.0 : vals.Average();
            }
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append($"{g.Key}: recall {Mean(EvalMetricNames.Recall):0.00}, " +
                      $"citaties {Mean(EvalMetricNames.CitationPrecision):0.00} " +
                      $"(n={g.Select(s => s.Metric).Count(m => m == EvalMetricNames.Recall)})");
        }
        return sb.Length == 0 ? "geen samples" : sb.ToString();
    }
}
