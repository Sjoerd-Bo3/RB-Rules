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
    /// (a) vraagtype Ruling met ≥2 herkende kaartnamen — interactievragen,
    /// precies waar één pass aantoonbaar context mist — óf (b) het
    /// lege-retrieval-signaal uit het gaps-rapport (#52): geen §-secties,
    /// geen kaartcontext, geen primer. <c>force</c> escaleert altijd
    /// (verificatie), <c>off</c> nooit.</summary>
    public static bool ShouldEscalate(
        QuestionType type, int cardMentions, bool emptyRetrieval, AgenticMode mode) =>
        mode switch
        {
            AgenticMode.Force => true,
            AgenticMode.Auto =>
                (type == QuestionType.Ruling && cardMentions >= 2) || emptyRetrieval,
            _ => false,
        };
}
