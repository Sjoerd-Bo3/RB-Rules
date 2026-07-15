namespace RbRules.Domain;

public record PrimerTopic(string Key, string Title, string Query);

/// <summary>De vaste conceptenlijst voor de game-primer (kennislaag 1,
/// docs/KNOWLEDGE.md): samenhangend spelbegrip dat losse §-secties niet
/// geven. Groeit mee met nieuwe mechanieken (evolutie-raamwerk, #52).</summary>
public static class PrimerTopics
{
    public static IReadOnlyList<PrimerTopic> All =>
    [
        new("turn-structure", "The turn structure",
            "turn structure phases beginning main phase ending turn order of play"),
        new("resources", "Runes and energy: the resource system",
            "runes energy channel recycle rune deck paying costs power"),
        new("battlefields-scoring", "Conquering battlefields and scoring points",
            "battlefield control conquer score point victory 8 points hold"),
        new("combat", "Combat and showdowns",
            "combat showdown attack defend might damage kill assign"),
        new("priority-reactions", "Priority, reactions, and the stack",
            "priority reaction action speed respond window resolve order"),
        new("zones", "Zones and card flow",
            "zones hand deck trash banishment base board champion zone move place"),
        new("units-legends", "Units, champions, and legends",
            "unit champion legend chosen champion play summon exhaust ready"),
        new("spells-gear", "Spells and gear",
            "spell gear cast play equip effect resolve one-time permanent"),
        new("keywords-core", "Core keywords in practice",
            "keyword accelerate tank deflect hidden shield legion deathknell temporary"),
        new("deckbuilding", "Deckbuilding and legality",
            "deck construction 40 cards domain identity legend copies limit legal"),
        new("winning", "Winning and losing",
            "win condition victory points lose game end tiebreaker"),
        new("golden-rules", "Golden and Silver Rules: what takes precedence",
            "golden rule silver rule card text supersedes rules precedence can't beats can"),
    ];
}
