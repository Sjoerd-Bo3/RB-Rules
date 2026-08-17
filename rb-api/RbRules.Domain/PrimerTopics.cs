namespace RbRules.Domain;

/// <summary>Eén primer-concept. <paramref name="Title"/> is de canonieke
/// (Engelse) titel die opgeslagen wordt en in de retrieval meegaat;
/// <paramref name="TitleNl"/> is de Nederlandse weergavetitel (#266) —
/// handgeschreven, met de speltermen onvertaald, dus zonder LLM en zonder
/// kans op drift.</summary>
public record PrimerTopic(string Key, string Title, string TitleNl, string Query);

/// <summary>De vaste conceptenlijst voor de game-primer (kennislaag 1,
/// docs/KNOWLEDGE.md): samenhangend spelbegrip dat losse §-secties niet
/// geven. Groeit mee met nieuwe mechanieken (evolutie-raamwerk, #52).</summary>
public static class PrimerTopics
{
    public static IReadOnlyList<PrimerTopic> All =>
    [
        new("turn-structure", "The turn structure", "De beurtstructuur",
            "turn structure phases beginning main phase ending turn order of play"),
        new("resources", "Runes and energy: the resource system",
            "Runes en energie: het resourcesysteem",
            "runes energy channel recycle rune deck paying costs power"),
        new("battlefields-scoring", "Conquering battlefields and scoring points",
            "Battlefields veroveren en punten scoren",
            "battlefield control conquer score point victory 8 points hold"),
        new("combat", "Combat and showdowns", "Combat en showdowns",
            "combat showdown attack defend might damage kill assign"),
        new("priority-reactions", "Priority, reactions, and the stack",
            "Priority, reactions en de stack",
            "priority reaction action speed respond window resolve order"),
        new("zones", "Zones and card flow", "Zones en de stroom van kaarten",
            "zones hand deck trash banishment base board champion zone move place"),
        new("units-legends", "Units, champions, and legends",
            "Units, champions en legends",
            "unit champion legend chosen champion play summon exhaust ready"),
        new("spells-gear", "Spells and gear", "Spells en gear",
            "spell gear cast play equip effect resolve one-time permanent"),
        new("keywords-core", "Core keywords in practice",
            "De kernkeywords in de praktijk",
            "keyword accelerate tank deflect hidden shield legion deathknell temporary"),
        new("deckbuilding", "Deckbuilding and legality", "Deckbuilding en legaliteit",
            "deck construction 40 cards domain identity legend copies limit legal"),
        new("winning", "Winning and losing", "Winnen en verliezen",
            "win condition victory points lose game end tiebreaker"),
        new("golden-rules", "Golden and Silver Rules: what takes precedence",
            "Golden en Silver Rules: wat gaat vóór",
            "golden rule silver rule card text supersedes rules precedence can't beats can"),
    ];

    /// <summary>De Nederlandse weergavetitel voor een opgeslagen primer-doc
    /// (#266), of null als die er niet is. Null zodra de beheerder de titel
    /// zelf heeft aangepast (<paramref name="storedTitle"/> wijkt af van de
    /// canonieke titel van het concept): dan wint zijn tekst, want een
    /// handmatige correctie mag nooit stil door een lijstwaarde uit de code
    /// worden overruled. Ook null voor topics buiten de lijst — de aanroeper
    /// valt dan terug op de opgeslagen titel.</summary>
    public static string? DutchTitle(string topic, string storedTitle)
    {
        var t = All.FirstOrDefault(t => t.Key == topic);
        return t is not null && string.Equals(t.Title, storedTitle, StringComparison.Ordinal)
            ? t.TitleNl
            : null;
    }
}
