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
    /// <summary>Lijst-/opsommingsvraag ("welke kaarten…", "alle X", "geef een
    /// overzicht", meta) — krijgt bredere kaart-retrieval (#67).</summary>
    Lijst,
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

    // Lijst-/opsommingsvragen (#67): "welke kaarten…", "welke X zijn er",
    // "alle kaarten", "geef een overzicht/lijst", meta-vragen.
    [GeneratedRegex(
        @"\b(welke|which|what|noem)\b[^.?!]{0,60}\b(kaarten|cards)\b" +
        @"|\bwelke\b[^.?!]{0,60}\bzijn er\b" +
        @"|\b(geef|maak|toon|give|show)\b[^.?!]{0,40}\b(overzicht|lijst|overview|list)\b" +
        @"|\b(overzicht|lijst) van\b|\blist all\b|\balle? (kaarten|cards)\b" +
        @"|\bde meta\b|\bmeta ?-?(decks?|kaarten|cards|overzicht)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ListQuestion();

    public static QuestionType Classify(string question, bool mentionsCard = false)
    {
        if (Tournament().IsMatch(question)) return QuestionType.Toernooi;
        if (Legality().IsMatch(question)) return QuestionType.Legaliteit;
        if (ListQuestion().IsMatch(question)) return QuestionType.Lijst;
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
            Korte genummerde stappen of een concreet spelvoorbeeld, met
            [n]-citaten in de tekst. Sla 'Oordeel/Zekerheid' over — dit is
            uitleg, geen ruling.
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
            ### Uitleg
            De dragende deckbouw-/banlijstregels, kort, met [n]-citaten in de tekst.
            ### Let op
            Relevante banlijst-items of aangekondigde wijzigingen; anders weglaten.
            De meegegeven BANLIJST is gezaghebbend en actueel.
            """,
        QuestionType.Toernooi => """
            VRAAGTYPE: toernooiprocedure. Gebruik het scheidsrechter-format
            (Oordeel → Zekerheid → Uitleg → Let op) en baseer je primair op de
            Tournament Rules-fragmenten in de context; citeer per stap met [n].
            """,
        QuestionType.Lijst => """
            VRAAGTYPE: lijst/overzicht — de vraag vraagt om een verzameling kaarten.
            **Antwoord:** één zin met de kern (hoeveel passende kaarten er zijn gevonden).
            ### Kaarten
            Een opsomming: per kaart de naam (vetgedrukt) plus één zin waarom hij aan
            het criterium voldoet, gebaseerd op de kaarttekst in de kaartgegevens.
            Noem uitsluitend kaarten uit de meegegeven kaartgegevens — nooit uit eigen
            kennis; kaarten die niet echt aan het criterium voldoen sla je over.
            Als de kaartgegevens melden dat de lijst is afgekapt ("eerste N van M"),
            zeg dat dan expliciet in het antwoord — nooit doen alsof de lijst compleet is.
            ### Regelbasis
            Alleen als een regel-§ het criterium verduidelijkt; anders weglaten.
            """,
        _ => """
            VRAAGTYPE: ruling/interactie. Gebruik het volledige format:
            Oordeel → Zekerheid → Uitleg (stappen in spelvolgorde, elk met
            [n]-citaat) → Let op.
            """,
    };
}
