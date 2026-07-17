using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Invoer voor de kandidaat-poort (#206): wat een <see
/// cref="Change"/> voor consolidatie-doeleinden draagt. <paramref
/// name="Refs"/> zijn de geraakte kaart-/sectie-BrainRefs, dezelfde
/// resolutie als de AFFECTS-projectie (<see cref="ChangeAffectsMapper"/>) —
/// géén nieuwe extractielaag, één gedeelde waarheid over "wat raakt deze
/// change".</summary>
public readonly record struct ChangeConsolidationCandidate(
    string ChangeType, DateTimeOffset DetectedAt, string SourceId, IReadOnlyList<BrainRef> Refs);

/// <summary>Deterministische kandidaat-poort voor changeconsolidatie (issue
/// #206, #188-lijn: poort deterministisch, oordeel via LLM). Twee changes
/// zijn een kandidaat-paar als ze hetzelfde <see cref="Change.ChangeType"/>
/// hebben, van VERSCHILLENDE bronnen komen, binnen <see cref="Window"/> van
/// elkaar gedetecteerd zijn, en overlappende geraakte referenties dragen.
/// Geen bruikbare refs aan een van beide kanten ⇒ nooit een kandidaat —
/// liever twee kaarten in de feed dan een fout gekoppeld paar (dezelfde
/// voorzichtige toon als de rest van de #188-lijn). Puur en getest; de
/// LLM-toets (<see cref="ChangeEventJudge"/>) oordeelt pas op wat hier al
/// doorkomt.</summary>
public static class ChangeConsolidationGate
{
    /// <summary>Venster (#206-eis): de 16-juli-bans (Rules Hub 06:46,
    /// Mobalytics 06:51) lagen 5 minuten uit elkaar — opeenvolgend binnen
    /// dezelfde scan, bronnen worden op TrustTier-volgorde gescand
    /// (IngestService.ScanAsync). 72 uur geeft daarnaast ruimte aan bronnen
    /// met een lagere scan-cadans (weekly) die een event pas dagen later
    /// oppikken.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(72);

    public static bool IsCandidate(ChangeConsolidationCandidate a, ChangeConsolidationCandidate b)
    {
        if (!string.Equals(a.ChangeType, b.ChangeType, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(a.SourceId, b.SourceId, StringComparison.OrdinalIgnoreCase)) return false;
        if ((a.DetectedAt - b.DetectedAt).Duration() > Window) return false;
        // Geen bruikbare refs → geen kandidaat, ook al kloppen type/venster/bron.
        if (a.Refs.Count == 0 || b.Refs.Count == 0) return false;

        foreach (var x in a.Refs)
            foreach (var y in b.Refs)
                if (x.Kind == y.Kind && string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }
}

/// <summary>Primair-keuze bij consolidatie (#206): hoogste bron-trust wint
/// (laagste TrustTier-getal = meest gezaghebbend); bij gelijke trust de
/// VROEGSTE detectie. Let op: dit is het OMGEKEERDE tie-break-doel van <see
/// cref="Precedence"/> (#168) — daar wint bij gelijk gezag de RECENTSTE
/// datum, want het gaat daar om welke tekst nu geldt (een bijgewerkte
/// officiële pagina superseedt een oudere). Hier gaat het om twee
/// onafhankelijke meldingen van HETZELFDE gebeurtenis: de vroegste melding
/// was het eerst zichtbaar en blijft — bij een echte gelijke stand — het
/// stabielste ankerpunt.</summary>
public static class ChangeConsolidationPrimary
{
    /// <summary>True als (tierA, detectedAtA) voorrang heeft op (tierB,
    /// detectedAtB) — dus A de primaire wordt/blijft. Bij een volledig
    /// gelijke stand (zelfde tier, zelfde tijdstip) wint A: de aanroeper
    /// geeft hier altijd de reeds bestaande root als A door, zodat een
    /// nieuwkomer een gevestigde primaire nooit zonder duidelijke reden
    /// verdringt.</summary>
    public static bool Wins(
        short tierA, DateTimeOffset detectedAtA, short tierB, DateTimeOffset detectedAtB)
    {
        if (tierA != tierB) return tierA < tierB;
        return detectedAtA <= detectedAtB;
    }
}

/// <summary>Uitkomst van de "zelfde gebeurtenis?"-toets (#206).</summary>
public sealed record ChangeEventJudgement(bool SameEvent);

/// <summary>"Zelfde gebeurtenis?"-toets (issue #206, #188-lijn): één cheap
/// LLM-call beslist of een kandidaat-paar (al gepasseerd door <see
/// cref="ChangeConsolidationGate"/>) hetzelfde real-world event beschrijft.
/// Zelfde patroon als <see cref="ClaimJudge"/>: objectvorm-guard vóór
/// TryGetProperty (<see cref="LlmJson.Candidates"/> levert ook
/// array-vormige blokken op uit toevallige bronvermeldingen in het
/// antwoord, en TryGetProperty op een niet-object root gooit een
/// InvalidOperationException — geen JsonException, dus de catch daaronder
/// vangt 'm niet zonder deze guard). Degradatie: AI-uitval of onbruikbaar
/// antwoord ⇒ null; de aanroeper behandelt het paar dan als NIET
/// geconsolideerd — de veilige kant (twee kaarten in de feed is nooit fout,
/// een fout gekoppeld paar wel).</summary>
public static class ChangeEventJudge
{
    /// <summary>Cap per change-omschrijving in de prompt — een volledige
    /// document-diff hoeft niet compleet mee; de eerste alinea's dragen het
    /// oordeel.</summary>
    public const int MaxDescriptionLength = 1500;

    public const string SystemPrompt = """
        You compare two changes detected from different sources for a
        Riftbound TCG rules-tracking feed. Respond ONLY with JSON:
        {"sameEvent": true|false}
        - true: both changes describe the same real-world event (e.g. the
          same ban/errata update or rules change), even if worded
          differently, with different detail, or reported at a slightly
          different time
        - false: they describe different, unrelated events, even if they
          are superficially similar (same topic, different specifics)
        When in doubt, answer false.
        No text outside the JSON.
        """;

    public static string BuildPrompt(
        string sourceNameA, string? summaryA, string? diffA,
        string sourceNameB, string? summaryB, string? diffB) =>
        $"Change A ({sourceNameA}):\n{Describe(summaryA, diffA)}\n\n"
        + $"Change B ({sourceNameB}):\n{Describe(summaryB, diffB)}";

    private static string Describe(string? summary, string? diff)
    {
        var text = string.Join('\n', new[] { summary, diff }.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (text.Length == 0) return "(geen tekst)";
        return text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] + "…" : text;
    }

    /// <summary>null bij onbruikbare output (aanroeper behandelt het paar
    /// dan als niet-hetzelfde-gebeurtenis — de veilige kant).</summary>
    public static ChangeEventJudgement? Parse(string raw)
    {
        foreach (var json in LlmJson.Candidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (Map(doc.RootElement) is { } judgement) return judgement;
            }
            catch (JsonException)
            {
                // geen geldige JSON op deze positie — volgende kandidaat
            }
        }
        return null;
    }

    private static ChangeEventJudgement? Map(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("sameEvent", out var v)
            || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        return new(v.GetBoolean());
    }
}
