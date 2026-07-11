using System.Text.RegularExpressions;

namespace RbRules.Domain;

public enum QuestionType
{
    /// <summary>Interactie/mag-dit-vraag — het volle scheidsrechter-format.</summary>
    Ruling,
    /// <summary>"Wat is/betekent X?" — uitleg van een concept of mechaniek.</summary>
    Definitie,
    /// <summary>Vraag over een specifieke kaart ("wat doet X?").</summary>
    Kaart,
    /// <summary>Ban/legaliteit/deckbouw ("mag ik 4x X spelen?", "is X banned?").</summary>
    Legaliteit,
    /// <summary>Toernooiregels/procedures (rondes, judges, penalties).</summary>
    Toernooi,
}

/// <summary>Interne vraag-router: het soort vraag bepaalt de antwoordstructuur
/// en de bronnen-bias (toernooivragen → Tournament Rules, legaliteit →
/// banlijst). Bewust heuristisch — géén extra LLM-call, dus geen extra
/// wachttijd; het LLM krijgt daarna een structuur passend bij het type.</summary>
public static partial class QuestionRouter
{
    [GeneratedRegex(@"\b(toernooi|tournament|ronde|rondes|judge|penalt|mulligan|match|best of|swiss|top ?cut|deck ?check|tijdslimiet|time limit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Tournament();

    [GeneratedRegex(@"\b(ban|banned|banlijst|banlist|verboden|legaal|legal|legaliteit|rotatie|rotation|toegestaan|hoeveel (kopie|exemplar)|deck ?(bouw|construction|limiet)|4x|playset)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Legality();

    [GeneratedRegex(@"^\s*(wat (is|zijn|betekent|betekenen|doet het keyword)|what (is|are|does .* mean)|leg .*uit|explain)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Definition();

    [GeneratedRegex(@"\b(wat doet|what does|hoe werkt de kaart|kaarttekst van)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CardQuestion();

    public static QuestionType Classify(string question, bool mentionsCard = false)
    {
        if (Tournament().IsMatch(question)) return QuestionType.Toernooi;
        if (Legality().IsMatch(question)) return QuestionType.Legaliteit;
        if (CardQuestion().IsMatch(question) && mentionsCard) return QuestionType.Kaart;
        if (Definition().IsMatch(question) && !mentionsCard) return QuestionType.Definitie;
        return QuestionType.Ruling;
    }

    /// <summary>Type-specifiek structuurblok voor de system-prompt.</summary>
    public static string StructureFor(QuestionType type) => type switch
    {
        QuestionType.Definitie => """
            VRAAGTYPE: definitie/concept. Gebruik deze structuur:
            **Definitie:** één of twee zinnen die het concept precies definiëren.
            ### Hoe het werkt
            Korte genummerde stappen of een concreet spelvoorbeeld, met [n]-citaten.
            ### Regelbasis
            Per bron één regel. Sla 'Oordeel/Zekerheid' over — dit is uitleg, geen ruling.
            """,
        QuestionType.Kaart => """
            VRAAGTYPE: kaartvraag. Gebruik deze structuur:
            **Antwoord:** wat de kaart doet, in gewone taal, één alinea.
            ### Details
            Alleen de niet-triviale punten: timing, targets, veelgemaakte fouten — met [n]-citaten.
            ### Let op
            Errata of banlijst-status als die er is; anders weglaten.
            De kaartgegevens in de context zijn leidend.
            """,
        QuestionType.Legaliteit => """
            VRAAGTYPE: legaliteit/deckbouw. Gebruik deze structuur:
            **Oordeel:** toegestaan of niet, in één zin.
            **Zekerheid:** Bevestigd | Afgeleid | Onzeker.
            ### Regelbasis
            De dragende deckbouw-/banlijstregels met [n]-citaten.
            ### Let op
            Relevante banlijst-items of aangekondigde wijzigingen; anders weglaten.
            De meegegeven BANLIJST is gezaghebbend en actueel.
            """,
        QuestionType.Toernooi => """
            VRAAGTYPE: toernooiprocedure. Gebruik het scheidsrechter-format
            (Oordeel → Zekerheid → Uitleg → Regelbasis → Let op) en baseer je
            primair op de Tournament Rules-fragmenten in de context.
            """,
        _ => """
            VRAAGTYPE: ruling/interactie. Gebruik het volledige format:
            Oordeel → Zekerheid → Uitleg (stappen in spelvolgorde) → Regelbasis → Let op.
            """,
    };
}
