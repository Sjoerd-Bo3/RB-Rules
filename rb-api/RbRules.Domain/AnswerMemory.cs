using Pgvector;

namespace RbRules.Domain;

/// <summary>Antwoordgeheugen (#384): één beantwoorde vraag met alles wat nodig is
/// om haar later te HERGEBRUIKEN — de vraag-embedding voor de nearest-neighbour-
/// zoektocht, het antwoord en de citaties zoals de vrager ze kreeg, en een
/// momentopname van de bron-hashes waarop het antwoord leunde. Anders dan
/// <see cref="AskTrace"/> (een venster van 200 rijen, nooit teruggelezen) is dit
/// een blijvende, gelabelde kennislaag: elke rij doorloopt de promotielus in
/// <see cref="AnswerMemoryPolicy"/> en alleen een bevestigde rij mag als
/// antwoord dienen.</summary>
public class AnswerMemory : IEmbeddable
{
    public long Id { get; set; }
    /// <summary>De vraag zoals gesteld (gecapt op 500 tekens, zoals AskTrace).</summary>
    public required string Question { get; set; }
    /// <summary>De genormaliseerde zoekzin uit de query-rewrite (#66) — of de
    /// ruwe vraag als de rewrite uitviel. Alleen ter weergave/diagnose; de
    /// matching loopt over <see cref="Embedding"/>.</summary>
    public required string QuestionNormalized { get; set; }
    public string? QuestionType { get; set; }
    /// <summary>Embedding van de RUWE vraagtekst (niet van de rewrite): bij een
    /// latere vraag is dat de eerste vector die beschikbaar is, vóór de rewrite —
    /// zo kost een geheugen-hit geen enkele LLM-call.</summary>
    public Vector? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public string? EmbeddingContentHash { get; set; }
    public required string Answer { get; set; }
    /// <summary>JSON van de citaties (het <c>Citation</c>-record van AskService)
    /// zoals ze bij het antwoord hoorden — bij hergebruik worden ze 1-op-1
    /// teruggegeven, want de [n]-verwijzingen in het antwoord slaan erop.</summary>
    public required string CitationsJson { get; set; }
    /// <summary>Bron-momentopname: <c>,bronId=hash,bronId=hash,</c> — de
    /// <c>Source.LastHash</c> van elke geciteerde bron op het moment van
    /// beantwoorden. Leidende en sluitende komma zodat <c>Contains(",id=")</c>
    /// EF-vertaalbaar op één bron kan filteren (invalidatie vanuit de
    /// wijzigingen-feed).</summary>
    public required string SourceSnapshot { get; set; }
    /// <summary>De rule_chunk-ids die de RRF-fusie voor deze vraag koos, komma-
    /// gescheiden — later het zesde RRF-kanaal (relevantiefeedback), nu alleen
    /// bewaard.</summary>
    public string? RetrievalSet { get; set; }
    /// <summary>Het pad dat het antwoord leverde (cheap|hard|agentic).</summary>
    public string? Model { get; set; }
    /// <summary>Korte hash van de systeemprompt op het moment van beantwoorden —
    /// zodat na een promptwijziging zichtbaar is welke rijen nog onder de oude
    /// prompt zijn ontstaan.</summary>
    public string? PromptVersion { get; set; }
    /// <summary>candidate | confirmed | verified | retracted — zie
    /// <see cref="AnswerMemoryPolicy"/>.</summary>
    public string Trust { get; set; } = AnswerMemoryPolicy.Candidate;
    /// <summary>Hoe vaak een latere vraag boven de cache-drempel op deze rij
    /// landde (gediend of niet). Drie hits zonder tegenspraak promoveren een
    /// kandidaat.</summary>
    public int HitCount { get; set; }
    public DateTimeOffset? LastHitAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long? UserId { get; set; }
    /// <summary>Waarom de rij is ingetrokken (duim omlaag, bron gewijzigd,
    /// beheerder) — de rij blijft staan als geschiedenis.</summary>
    public string? RetractedReason { get; set; }
    public DateTimeOffset? RetractedAt { get; set; }
}

/// <summary>De zuivere regels van het antwoordgeheugen (#384): drempels,
/// promotielus en de bron-momentopname. Geen EF, geen klok — alles wat hier
/// staat is met een unit-test vast te zetten, en de service erboven doet alleen
/// nog de I/O.</summary>
public static class AnswerMemoryPolicy
{
    public const string Candidate = "candidate";
    public const string Confirmed = "confirmed";
    public const string Verified = "verified";
    public const string Retracted = "retracted";

    /// <summary>Cosinus-gelijkenis waarboven een eerdere vraag als DEZELFDE
    /// vraag geldt en het bewaarde antwoord gediend mag worden (mits bevestigd
    /// en de bronnen ongewijzigd). Scherp gekozen: een gemiste hit kost alleen
    /// een verse LLM-call, een valse hit een fout antwoord.</summary>
    public const double ServeSimilarity = 0.92;

    /// <summary>Ondergrens waarboven een eerdere vraag als "vergelijkbaar" naast
    /// het verse antwoord getoond wordt.</summary>
    public const double SimilarSimilarity = 0.80;

    /// <summary>Zoveel hits zonder tegenspraak maken van een kandidaat een
    /// bevestigde rij — de "3× hergebruikt"-regel uit het issue.</summary>
    public const int ConfirmAfterHits = 3;

    /// <summary>Alleen een bevestigde of geverifieerde rij mag als antwoord
    /// dienen; een kandidaat is nog niemands oordeel, een ingetrokken rij is
    /// geschiedenis.</summary>
    public static bool MayServe(string trust) => trust is Confirmed or Verified;

    /// <summary>Is deze gelijkenis (cosinus, 1 = identiek) hoog genoeg om als
    /// dezelfde vraag te tellen?</summary>
    public static bool IsSameQuestion(double similarity) => similarity >= ServeSimilarity;

    public static bool IsSimilarQuestion(double similarity) =>
        similarity >= SimilarSimilarity && similarity < ServeSimilarity;

    /// <summary>Trust na een hit: een kandidaat die <see cref="ConfirmAfterHits"/>
    /// keer opnieuw gevraagd werd zonder tegenspraak wordt bevestigd; alle
    /// andere toestanden blijven wat ze zijn (een ingetrokken rij komt nooit via
    /// hits terug — daar is een mens of een verse rij voor).</summary>
    public static string TrustAfterHit(string trust, int hitCountAfter) =>
        trust == Candidate && hitCountAfter >= ConfirmAfterHits ? Confirmed : trust;

    /// <summary>Trust na gebruikersfeedback: duim omhoog bevestigt een kandidaat
    /// (een geverifieerde rij blijft geverifieerd), duim omlaag trekt élke niet-
    /// ingetrokken rij in — óók een geverifieerde: het oordeel van de beheerder
    /// is dan aan een nieuwe review toe, en intussen mag de rij niet dienen.</summary>
    public static string TrustAfterFeedback(string trust, bool thumbsUp)
    {
        if (trust == Retracted) return Retracted;
        if (!thumbsUp) return Retracted;
        return trust == Candidate ? Confirmed : trust;
    }

    /// <summary>Bron-momentopname (zie <see cref="AnswerMemory.SourceSnapshot"/>):
    /// gesorteerd op bron-id zodat dezelfde set altijd dezelfde string geeft.</summary>
    public static string EncodeSnapshot(IEnumerable<(string SourceId, string? Hash)> sources)
    {
        var parts = sources
            .Where(s => !string.IsNullOrEmpty(s.SourceId))
            .DistinctBy(s => s.SourceId, StringComparer.Ordinal)
            .OrderBy(s => s.SourceId, StringComparer.Ordinal)
            .Select(s => $"{s.SourceId}={s.Hash ?? ""}");
        return "," + string.Join(",", parts) + ",";
    }

    public static IReadOnlyList<(string SourceId, string Hash)> DecodeSnapshot(string snapshot) =>
        [.. snapshot.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(kv => (kv[0], kv.Length > 1 ? kv[1] : ""))];

    /// <summary>De EF-vertaalbare zoeksleutel voor "citeert bron X":
    /// <c>Contains(",X=")</c> op de momentopname.</summary>
    public static string SnapshotKey(string sourceId) => $",{sourceId}=";

    /// <summary>Zijn alle bronnen uit de momentopname nog ongewijzigd? Een bron
    /// die niet meer bestaat of waarvan de hash verschilt maakt het antwoord
    /// verdacht — dan liever vers dan verouderd. Een bron zonder hash op beide
    /// momenten (nooit gescand) telt als ongewijzigd: er is niets veranderd.</summary>
    public static bool SourcesUnchanged(
        string snapshot, IReadOnlyDictionary<string, string?> currentHashes)
    {
        foreach (var (sourceId, hash) in DecodeSnapshot(snapshot))
        {
            if (!currentHashes.TryGetValue(sourceId, out var now)) return false;
            if (!string.Equals(hash, now ?? "", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Komt een vraag in aanmerking voor het geheugen (lezen én
    /// schrijven)? Alleen een eerste beurt zonder foto en buiten benchmark/
    /// model-sweep: een doorvraag hangt aan zijn gesprek, een foto-antwoord aan
    /// het beeld, en een benchmark mag niets leren of hergebruiken.</summary>
    public static bool Eligible(bool firstTurn, bool hasImage, bool benchmark, bool modelOverride) =>
        firstTurn && !hasImage && !benchmark && !modelOverride;
}
