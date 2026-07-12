namespace RbRules.Domain;

/// <summary>Stand van de <c>ASK_AGENTIC</c>-feature-flag (#107,
/// docs/BRAIN.md §2.4). Default is <see cref="Off"/>: de deploy verandert
/// het live gedrag niet totdat de beheerder de flag omzet.</summary>
public enum AgenticMode
{
    /// <summary>Nooit escaleren — het standaardpad (single-pass).</summary>
    Off,
    /// <summary>Escaleren wanneer de vraag kwalificeert (§2.4-triggers).</summary>
    Auto,
    /// <summary>Altijd escaleren — bestaat alleen voor verificatie.</summary>
    Force,
}

/// <summary>Gate voor agentic ask (#107): beslist ná de normale retrieval of
/// een vraag mag door-redeneren over het brein. Puur en unit-getest — de
/// I/O-kant (env lezen, retrieval-signalen verzamelen) blijft in AskService.</summary>
public static class AgenticGate
{
    /// <summary>Parse de <c>ASK_AGENTIC</c>-env-waarde. Onbekend, leeg of
    /// afwezig valt op <see cref="AgenticMode.Off"/> terug: een tikfout mag
    /// nooit stilzwijgend het dure agent-pad aanzetten (zelfde principe als
    /// de task-fallback in rb-ai's validate.ts).</summary>
    public static AgenticMode ParseMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "auto" => AgenticMode.Auto,
            "force" => AgenticMode.Force,
            _ => AgenticMode.Off,
        };

    /// <summary>§2.4: in <c>auto</c> kwalificeert een vraag alléén als
    /// (a) vraagtype Ruling met ≥2 herkende kaartnamen in de huidige vraag —
    /// interactievragen, precies waar één pass aantoonbaar context mist — óf
    /// (b) het lege-retrieval-signaal (zie de berekening in AskService).
    /// <c>force</c> escaleert altijd (verificatie), <c>off</c> nooit.
    /// Foto-vragen escaleren nooit, óók niet onder force (review #107):
    /// board-state-analyse kreeg bewust het Opus-visionpad (task "hard") en
    /// de brein-tools zijn tekst-only — escaleren zou die keuze stil
    /// downgraden naar het Sonnet-agentpad.</summary>
    public static bool ShouldEscalate(
        QuestionType type, int cardMentions, bool emptyRetrieval, AgenticMode mode,
        bool hasImage = false)
    {
        if (hasImage) return false;
        return mode switch
        {
            AgenticMode.Force => true,
            AgenticMode.Auto =>
                (type == QuestionType.Ruling && cardMentions >= 2) || emptyRetrieval,
            _ => false,
        };
    }

    /// <summary>Telt hoeveel écht verschillende kaarten er genoemd zijn
    /// (review #107). De naam-match in AskService is een substring-match:
    /// "Jinx" matcht óók binnen "Jinx, Loose Cannon", waardoor één genoemde
    /// kaart als twee mentions zou tellen en een enkelkaart-Ruling onterecht
    /// zou escaleren. Namen die deel zijn van een langere gematchte naam
    /// tellen daarom niet mee.</summary>
    public static int CountDistinctMentions(IReadOnlyCollection<string> matchedNames)
    {
        var distinct = matchedNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count(name => !distinct.Any(other =>
            other.Length > name.Length &&
            other.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }
}
