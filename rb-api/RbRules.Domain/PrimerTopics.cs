namespace RbRules.Domain;

public record PrimerTopic(string Key, string Title, string Query);

/// <summary>De vaste conceptenlijst voor de game-primer (kennislaag 1,
/// docs/KNOWLEDGE.md): samenhangend spelbegrip dat losse §-secties niet
/// geven. Groeit mee met nieuwe mechanieken (evolutie-raamwerk, #52).</summary>
public static class PrimerTopics
{
    public static IReadOnlyList<PrimerTopic> All =>
    [
        new("turn-structure", "De beurtstructuur",
            "turn structure phases beginning main phase ending turn order of play"),
        new("resources", "Runes en energy: het resource-systeem",
            "runes energy channel recycle rune deck paying costs power"),
        new("battlefields-scoring", "Battlefields veroveren en punten scoren",
            "battlefield control conquer score point victory 8 points hold"),
        new("combat", "Combat en showdowns",
            "combat showdown attack defend might damage kill assign"),
        new("priority-reactions", "Prioriteit, reacties en de stapel",
            "priority reaction action speed respond window resolve order"),
        new("zones", "Zones en kaartstromen",
            "zones hand deck trash banishment base board champion zone move place"),
        new("units-legends", "Units, champions en legends",
            "unit champion legend chosen champion play summon exhaust ready"),
        new("spells-gear", "Spells en gear",
            "spell gear cast play equip effect resolve one-time permanent"),
        new("keywords-core", "Kern-keywords in de praktijk",
            "keyword accelerate tank deflect hidden shield legion deathknell temporary"),
        new("deckbuilding", "Deckbouw en legaliteit",
            "deck construction 40 cards domain identity legend copies limit legal"),
        new("winning", "Winnen en verliezen",
            "win condition victory points lose game end tiebreaker"),
        new("golden-rules", "Golden en Silver Rules: wat gaat vóór",
            "golden rule silver rule card text supersedes rules precedence can't beats can"),
    ];
}
