using System.Text.RegularExpressions;

namespace RbRules.Domain.GraphRag;

/// <summary>De vier GraphRAG-retrieval-modi (§4) plus de directe niet-graaf-tak.
/// De orchestrator kan er meer dan één combineren (DRIFT + Path); dit is de
/// PRIMAIRE modus die de subgraaf zeeden bepaalt.</summary>
public enum RetrievalMode
{
    /// <summary>k-hop getypeerde subgraaf + gekoppelde chunks. Default voor
    /// profiel-/definitie-vragen.</summary>
    Local,
    /// <summary>Community-summaries (primer/sectie-dossiers als L0/L1) + map-reduce.
    /// Voor brede/abstracte/overzichts-vragen.</summary>
    Global,
    /// <summary>k-shortest trust-gewogen paden als onderbouwing. Voor causale
    /// "waarom"-vragen.</summary>
    Path,
    /// <summary>Vector-seed → typed-edge-expansie → trust re-rank → begrensde
    /// follow-up-hop. Default voor scherpe interactie-vragen.</summary>
    Drift,
    /// <summary>Directe niet-graaf-lookup (BanLookup) — ban/legaliteit.</summary>
    Direct,
}

/// <summary>De volledige modus-keuze: primaire modus, k-hop-diepte en de
/// aanvullende kanalen (§4-tabel). Puur data zodat de orchestrator hem kan
/// tracen en de tests hem exact kunnen asserten.</summary>
public sealed record ModeSelection(
    RetrievalMode Primary,
    int KHops,
    bool UseDrift,
    bool UsePath,
    bool UseMisconceptionChannel,
    string Reason)
{
    /// <summary>Local-only degradatie (budget-fallback, beslissing #232): behoud de
    /// k-hop, val terug op de goedkope getypeerde subgraaf, zet elk duur kanaal
    /// (Path/Drift-follow-up) uit.</summary>
    public ModeSelection ToLocalOnly(string reason) =>
        new(RetrievalMode.Local, Math.Max(1, KHops), UseDrift: false, UsePath: false,
            UseMisconceptionChannel, $"{Reason} → local-only: {reason}");
}

/// <summary>Lexicons + cue-detectie voor de β-router en de modus-selector (§4).
/// Bewust regex-heuristiek: géén extra LLM-call, dus geen extra wachttijd — de
/// router is een goedkope voorbeslissing, precies zoals <see cref="QuestionRouter"/>.</summary>
public static partial class RetrievalCues
{
    // Abstractie/overzichts-cues (NL + EN): breedte-vragen die het community-/
    // Global-kanaal willen ("alle timing-windows", "overzicht van …", "in het
    // algemeen").
    [GeneratedRegex(@"\b(overzicht|overview|alle|all|elke|every|in het algemeen|in general|" +
        @"welke .* zijn er|soorten|types?|categorie|list|lijst van|summariz|samenvat)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Abstraction();

    // Causale "waarom/hoe-verhoudt"-cues → Path-first.
    [GeneratedRegex(@"\b(waarom|why|hoezo|hoe komt|hoe kan|wat maakt|wat gebeurt er als|" +
        @"verliest|wint|beats|loses?|verslaat|voorrang|precede|because)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Causal();

    // Interactie/samenwerkings-cues → DRIFT + Path.
    [GeneratedRegex(@"\b(werkt .* (samen|met)|combineer|combo|interact|samen met|" +
        @"tegen|counter|stapel|stack|reageer|response op|triggert)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Interaction();

    // Misvattings-cues → misvattingen-kanaal ("klopt het dat", "mag je altijd").
    [GeneratedRegex(@"\b(klopt het dat|is het waar|mag je altijd|kun je altijd|" +
        @"moet je altijd|is it true|can you always)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Misconception();

    public static int AbstractionCueCount(string question) => Abstraction().Matches(question).Count;
    public static bool IsAbstract(string question) => Abstraction().IsMatch(question);
    public static bool IsCausal(string question) => Causal().IsMatch(question);
    public static bool IsInteraction(string question) => Interaction().IsMatch(question);
    public static bool HasMisconceptionCue(string question) => Misconception().IsMatch(question);

    /// <summary>Grof inhoudswoord-aantal (voor de entity-dichtheid): tokens van ≥3
    /// letters. Deterministisch, cultuur-onafhankelijk.</summary>
    public static int ContentWordCount(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return 0;
        var count = 0;
        foreach (var token in question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (token.Count(char.IsLetter) >= 3) count++;
        return count;
    }
}

/// <summary>De modus-selector achter de vraag-router (§4-tabel). Combineert het
/// bestaande <see cref="QuestionType"/> (dat de antwoordSTRUCTUUR al bepaalt) met
/// het aantal gelinkte entiteiten en de causale/interactie/abstractie-cues tot de
/// RETRIEVAL-modus. Puur en volledig getest — de I/O (entity-linking, retrievers)
/// blijft in de orchestrator.</summary>
public static class ModeSelector
{
    public static ModeSelection Select(QuestionType type, string question, int linkedEntities)
    {
        var causal = RetrievalCues.IsCausal(question);
        var interaction = RetrievalCues.IsInteraction(question);
        var abstractQ = RetrievalCues.IsAbstract(question);
        var misconception = RetrievalCues.HasMisconceptionCue(question);

        // 1) Ban/legaliteit → directe niet-graaf-lookup (BanLookup), geen graaf.
        if (type == QuestionType.Legaliteit)
            return new(RetrievalMode.Direct, 0, false, false, false,
                "Legaliteit/ban → directe BanLookup, geen graaf.");

        // 2) Overzichts-/lijstvragen → Global (map-reduce over community-summaries).
        if (type == QuestionType.Lijst || abstractQ)
            return new(RetrievalMode.Global, 1, false, false, misconception,
                "Breed/overzicht → Global (community-summaries, map-reduce).");

        // 3) Causale "waarom verliest A de showdown van B" → Path-first (k=2).
        if (causal && linkedEntities >= 2)
            return new(RetrievalMode.Path, 2, UseDrift: true, UsePath: true, misconception,
                "Causaal + ≥2 entiteiten → Path-first met DRIFT-onderbouwing.");

        // 4) Interactie "werkt A samen met B" → DRIFT + Path (k=2).
        if (interaction && linkedEntities >= 2)
            return new(RetrievalMode.Drift, 2, UseDrift: true, UsePath: true, misconception,
                "Interactie tussen ≥2 entiteiten → DRIFT + path.");

        // 5) Misvatting-check → DRIFT + misvattingen-kanaal.
        if (misconception)
            return new(RetrievalMode.Drift, 2, UseDrift: true, UsePath: true, true,
                "Misvatting-check → DRIFT + misvattingen-kanaal.");

        // 6) Definitie/concept → Local k=1, geen DRIFT (goedkoop profiel).
        if (type == QuestionType.Definitie)
            return new(RetrievalMode.Local, 1, false, false, false,
                "Definitie/concept → Local k=1, geen DRIFT.");

        // 7) Kaartvraag → Local k=1 (profiel), lichte drift bij een tweede entiteit.
        if (type == QuestionType.Kaart)
            return new(RetrievalMode.Local, 1, UseDrift: linkedEntities >= 2, false, false,
                "Kaartprofiel → Local k=1.");

        // 8) Rest (ruling/toernooi): scherpe vraag → DRIFT default (§4).
        return new(RetrievalMode.Drift, linkedEntities >= 2 ? 2 : 1,
            UseDrift: true, UsePath: linkedEntities >= 2, misconception,
            "Ruling/interactie → DRIFT (default voor scherpe vragen).");
    }
}
