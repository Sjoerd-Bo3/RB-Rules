using System.Text.Json;

namespace RbRules.Domain;

/// <summary>Eén steekproef-audit-oordeel over een gepromoveerde <see cref="Interaction"/>
/// (#255). De "precisie" in de observability was tot dusver de accept-ratio van onze
/// eigen promotie-poort (verified ÷ judged) — zelfreferentieel: een pijplijn die
/// tautologieën promoveert scoort er uitstekend op. Deze rij is het oordeel van een
/// STERKER model (rb-ai task "hard") over de vraag die de poort niet kan beantwoorden:
/// klopt het feit, en wordt het gedragen door het bewijs?
///
/// HARDE REGEL (issue #255, rode draad #236): een audit-oordeel draagt NOOIT
/// zelfstandig een promotie of degradatie. De rij is pure meting + provenance; een
/// negatief oordeel wordt daarnaast zichtbaar gemaakt via het bestaande
/// reviewqueue-kanaal (<c>ReasoningConflict</c>), waar een beheerder beslist.
/// De interactie-rij zelf blijft per constructie onaangeraakt.</summary>
public class InteractionAudit
{
    public long Id { get; set; }

    /// <summary>De beoordeelde <see cref="Interaction"/>. Bewust géén FK-navigatie:
    /// het oordeel is een meting óver het feit, geen onderdeel ervan.</summary>
    public required long InteractionId { get; set; }

    /// <summary>0a-provenance (#233): de audit-<see cref="MiningRun"/>
    /// (Kind = interaction_audit) die dit oordeel velde.</summary>
    public required string RunId { get; set; }

    /// <summary>Eigen provenance op de RIJ (issue-eis): welk model oordeelde.</summary>
    public required string Model { get; set; }

    /// <summary>Eigen provenance op de RIJ: welke promptversie. Tevens het
    /// watermark-criterium: een prompt-bump maakt bestaande oordelen "niet recent"
    /// en opent de pool opnieuw (zelfde stale-conditie als her-mining, §3.5).</summary>
    public required string PromptVersion { get; set; }

    /// <summary>Het oordeel: klopt de bewering inhoudelijk?</summary>
    public bool Correct { get; set; }

    /// <summary>Het oordeel: wordt de bewering gedragen door het meegegeven bewijs?
    /// Apart van <see cref="Correct"/> — een feit kan toevallig kloppen zonder dat
    /// het aangeboden bewijs het draagt, en dát onderscheid is de meting.</summary>
    public bool SupportedByEvidence { get; set; }

    /// <summary>Motivering van het model (Engels — #187: afgeleide kennis in de
    /// brontaal). Voor mensenogen in beheer; nooit voor logica.</summary>
    public string? Motivation { get; set; }

    /// <summary>De tier van de interactie op het moment van de audit (nu altijd
    /// "promoted" — de pool selecteert alleen gepromoveerde). Vastgelegd zodat de
    /// meting leesbaar blijft als er later ook andere tiers geauditeerd worden.</summary>
    public string? InteractionStatusAtAudit { get; set; }

    public DateTimeOffset AuditedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gezond oordeel: correct én gedragen door het bewijs — de teller van
    /// de gemeten precisie.</summary>
    public bool Sound => Correct && SupportedByEvidence;
}

/// <summary>Het geparste tool-oordeel (spiegelt rb-ai's <c>emit_audit_verdict</c>).</summary>
public sealed record InteractionAuditVerdict(
    bool Correct, bool SupportedByEvidence, string? Motivation);

/// <summary>De tool-forced audit-vorm (#255) — puur, zonder IO; de rb-ai-call woont
/// in de service. De parser is de tweede muur (defense-in-depth, zelfde rol als
/// <see cref="InteractionExtraction.ParseDetailed"/>): het oordeel is alleen geldig
/// als het EXACT het gesloten schema volgt — precies één verdict, echte JSON-booleans.
/// Alles daarbuiten is <see cref="ExtractionParse{T}.Malformed"/>, nooit een coulante
/// lezing: een audit die "yes" als true leest, meet zijn eigen soepelheid.</summary>
public static class InteractionAuditExtraction
{
    /// <summary>Prompt-versie-stempel op run én rij — bump bij elke wijziging aan
    /// prompt/vorm; bestaande oordelen tellen dan niet meer als "recent" en de pool
    /// gaat opnieuw open.</summary>
    public const string PromptVersion = "interaction-audit-v1";

    public const string SystemPrompt = """
        Je bent een onafhankelijke auditor van een Riftbound TCG-kennisbank. Je krijgt
        één bewering over een kaart/keyword-interactie voorgelegd, plus het bewijs
        waarop die bewering is gebaseerd. Beoordeel streng en roep UITSLUITEND de tool
        `emit_audit_verdict` aan, met precies één verdict:
        - correct: klopt de bewering inhoudelijk voor Riftbound TCG?
        - supported_by_evidence: wordt de bewering gedragen door het meegeleverde
          bewijs? Alleen het aangeboden bewijs telt — niet wat je verder weet.
        - motivation: 1-3 zinnen, in het Engels, die je oordeel onderbouwen.
        Een vage of te sterke bewering ("X counters Y" terwijl het bewijs alleen een
        gedeeltelijke wisselwerking beschrijft) is NIET correct.
        """;

    /// <summary>Parse de audit-envelop <c>{"verdicts":[{…}]}</c>. Geldig is
    /// UITSLUITEND: precies één verdict, met echte booleans voor <c>correct</c> en
    /// <c>supported_by_evidence</c>. Al het andere — nul of twee verdicts, een string
    /// waar een boolean hoort, een afgekapte body — is Malformed en telt als uitval
    /// (geen watermark; de interactie komt de volgende run terug).</summary>
    public static ExtractionParse<InteractionAuditVerdict> ParseDetailed(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ExtractionParse<InteractionAuditVerdict>.Broken;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonSpan(raw));
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return ExtractionParse<InteractionAuditVerdict>.Broken; }

        var array = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("verdicts", out var inner) && inner.ValueKind == JsonValueKind.Array
            ? inner
            : root.ValueKind == JsonValueKind.Array ? root : default;
        if (array.ValueKind != JsonValueKind.Array) return ExtractionParse<InteractionAuditVerdict>.Broken;

        // Precies één: een audit zonder oordeel of met twee oordelen is geen audit.
        if (array.GetArrayLength() != 1) return ExtractionParse<InteractionAuditVerdict>.Broken;
        var item = array[0];
        if (item.ValueKind != JsonValueKind.Object) return ExtractionParse<InteractionAuditVerdict>.Broken;

        // Echte booleans of niets — het gesloten schema wordt hier deterministisch
        // nagerekend (mutatie-eis (b) van #255: buiten het schema ⇒ geweigerd).
        if (Bool(item, "correct") is not { } correct) return ExtractionParse<InteractionAuditVerdict>.Broken;
        if (Bool(item, "supported_by_evidence") is not { } supported)
            return ExtractionParse<InteractionAuditVerdict>.Broken;

        var motivation = item.TryGetProperty("motivation", out var m)
            && m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString())
            ? m.GetString()
            : null;

        return new([new InteractionAuditVerdict(correct, supported, motivation)], Malformed: false);
    }

    private static bool? Bool(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True
            : null;

    private static string ExtractJsonSpan(string raw)
    {
        var objStart = raw.IndexOf('{');
        var arrStart = raw.IndexOf('[');
        if (objStart < 0 && arrStart < 0) return raw;
        var useObj = objStart >= 0 && (arrStart < 0 || objStart < arrStart);
        var start = useObj ? objStart : arrStart;
        var end = raw.LastIndexOf(useObj ? '}' : ']');
        return end > start ? raw[start..(end + 1)] : raw;
    }
}

/// <summary>Bouwt de audit-prompt: de bewering in één zin, plus het gelabelde bewijs
/// (kaartteksten, keyword-definities, regelsecties). Puur en getest — de service
/// levert alleen de opgezochte teksten. Elke bewijs-eenheid wordt begrensd zodat de
/// prompt niet meegroeit met de langste kaarttekst in de set (de #281-les: omvang
/// drijft latency, en latency tegen een vaste timeout bepaalt uitval).</summary>
public static class InteractionAuditPrompt
{
    /// <summary>Kap per bewijs-eenheid. Ruim genoeg voor elke kaarttekst (~300
    /// tekens) en de meeste regelsecties; een uitzonderlijk lange sectie wordt
    /// afgekapt in de PROMPT — de bron blijft uiteraard volledig.</summary>
    public const int MaxEvidenceChars = 800;

    /// <summary>Maximaal aantal bewijs-eenheden per audit.</summary>
    public const int MaxEvidenceUnits = 6;

    public sealed record EvidenceUnit(string Label, string Text);

    /// <summary>De voorgelegde tekst: bewering + condities + bewijsblok. De labels
    /// ("kaarttekst", "regels …") laten het model zien wélk bewijs wat is, zodat
    /// <c>supported_by_evidence</c> over het aangeboden bewijs gaat en niet over
    /// vrije associatie.</summary>
    public static string Compose(
        string claim, IReadOnlyList<string> conditions, IReadOnlyList<EvidenceUnit> evidence)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var parts = new List<string> { $"Bewering: {claim}" };
        if (conditions is { Count: > 0 })
            parts.Add("Condities: " + string.Join("; ", conditions));
        parts.Add(evidence is { Count: > 0 }
            ? "Bewijs:\n" + string.Join("\n", evidence
                .Take(MaxEvidenceUnits)
                .Select(e => $"[{e.Label}] {Truncate(e.Text)}"))
            : "Bewijs: (geen bewijstekst gevonden bij dit feit)");
        return string.Join("\n\n", parts);
    }

    private static string Truncate(string text) =>
        text.Length <= MaxEvidenceChars ? text : text[..MaxEvidenceChars] + "…";
}
