namespace RbRules.Domain;

/// <summary>Seed voor de benchmarkvragen (#158) — zelfde idempotente
/// startup-patroon als SourceSeed: ontbrekende ExternalKey's komen erbij,
/// /admin (of een latere sleutel-update) is daarna leidend, bestaande rijen
/// blijven ongemoeid. Part 1: de eerste 12 judge-vragen die Sjoerd aanleverde
/// (2026-07-14), als gestructureerde meerkeuze. De officiële antwoordsleutel
/// is nog grotendeels open — CorrectIndex staat overal op null totdat Sjoerd
/// hem bevestigt; zonder sleutel draait een vraag wel mee maar telt hij niet
/// mee in de score (zie BenchmarkService). Latere delen komen als extra
/// entries hierin, nooit als wijziging van bestaande ExternalKey's.</summary>
public static class BenchmarkSeed
{
    private const string JudgeCategory = "judge";

    public static IReadOnlyList<BenchmarkQuestion> Defaults =>
    [
        new()
        {
            ExternalKey = "judge-1", Category = JudgeCategory,
            Question = "Which of the following options would NOT enable the Legion bonus " +
                "of Vanguard Captain (When you play me, play two 1 [S] Recruit unit tokens " +
                "here. Get the effect if you've played another card this turn.)?",
            Options =
            [
                "Play a Vanguard Sergeant",
                "Conquering with Kai'sa, Evolutionary, and playing a spell from the trash",
                "Hiding a Hidden Blade spell from hand",
                "Playing a Pakaa Cub from face down",
            ],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-2", Category = JudgeCategory,
            Question = "What is the correct sequence of the phases of a turn of Riftbound?",
            Options =
            [
                "Awaken, Beginning, Channel, Draw, Main, End",
                "Beginning, Awaken, Channel, Draw, Main, End",
                "Awaken, Channel, Draw, Beginning, Main, End",
                "Awaken, Beginning, Draw, Channel, Main, End",
            ],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-3", Category = JudgeCategory,
            Question = "Player 1 has a single Fury Unit at a battlefield. Player 2 moves 2 " +
                "of their units, a Chaos Unit and a Body Unit, to that battlefield. The Fury " +
                "unit has an ability that reads \"When I defend, draw 1\". The Chaos unit " +
                "has an ability that reads \"When I move, draw 1\" and the Body Unit has an " +
                "ability that reads \"When I attack, draw 1\". In what sequence will these " +
                "abilities resolve?",
            Options =
            [
                "Chaos, Fury, Body",
                "Chaos or Body, then Fury",
                "Fury, Body, Chaos",
                "Fury, then Body or Chaos",
                "Chaos, Body, Fury",
            ],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-4", Category = JudgeCategory,
            Question = "How many times does your Rune Pool empty across the span of a turn?",
            Options =
            [
                "Once",
                "Twice",
                "Thrice",
                "As many times as chains open",
                "As many times as chains open, plus 2",
                "As many times as priority passes",
                "As many times as priority passes plus 1",
            ],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-5", Category = JudgeCategory,
            Question = "In a Competitive OPL event, how many cards is a player required to bring?",
            Options =
            [
                "Exactly 12 Runes, Exactly 40 Main Deck Cards (Including a Chosen Champion), " +
                    "Up to 8 Sideboard Cards, 1 Legend",
                "At Least 12 Runes, At Least 40 Main Deck Cards (Including a Chosen Champion), " +
                    "Exactly 0 or 8 Sideboard Cards, 1 Legend",
                "Up To 12 Runes, Exactly 40 Main Deck Cards (Including a Chosen Champion), " +
                    "Up to 8 Sideboard Cards, 1 Legend",
                "Exactly 12 Runes, Exactly 40 Main Deck Cards (Including a Chosen Champion), " +
                    "Exactly 0 or 8 Sideboard Cards, 1 Legend",
            ],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-6", Category = JudgeCategory,
            Question = "Which of the following situations involves Priority being passed " +
                "from the player performing the action to another player, I.E. will result " +
                "in an opportunity for Reactions to be played?",
            Options =
            [
                "The Loose Canon triggering during its controller's Beginning Phase",
                "Moving a unit from a battlefield to its base",
                "Drawing a card during the Draw Phase",
                "Recycling Runes to add Power to the Resource Pool",
            ],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-7", Category = JudgeCategory,
            Question = "Unchecked Power is played by Player 1, which deals 12 damage to all " +
                "units at battlefields. Player 2 has Viktor, Leader and two Vanguard " +
                "Sergeants at a battlefield. Viktor, Leader has the ability: \"When another " +
                "non-Recruit unit you control dies, play a 1 [S] Recruit unit token into your " +
                "base.\" How many Recruits will Player 2 play when Unchecked Power resolves?",
            Options = ["2", "3", "1", "0"],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-8", Category = JudgeCategory,
            Question = "Player 1 plays a spell that reads \"Deal 4 to a unit at a battlefield. " +
                "Discard 1. Draw 1. Move a friendly Unit from its base to a battlefield.\" " +
                "Player 2 plays a spell that, before the resolution of that spell, moves the " +
                "unit that was meant to be dealt 4 damage to its base. Given that Player 1 has " +
                "no cards in hand, what will happen when Player 1's spell resolves?",
            Options =
            [
                "The unit will take 4 damage, then player 1 will move a friendly unit to a battlefield.",
                "The unit will take 4 damage, then player 1 will draw a card, then player 1 " +
                    "will move a friendly unit to a battlefield.",
                "Player 1 will draw a card, then player 1 will move a friendly unit to a battlefield.",
                "Player 1 will move a friendly unit to a battlefield",
            ],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-9", Category = JudgeCategory,
            Question = "Player 1 controls a battlefield with a Mountain Drake at that " +
                "battlefield. Player 2 moves their Travelling Merchant and Yasuo, Remorseful " +
                "to that battlefield. A combat begins, and Yasuo, Remorseful's attack trigger " +
                "targets the Mountain Drake. It Resolves. Player 1 plays Facebreaker, which " +
                "stuns the Mountain Drake and the Yasuo. Player 2's hand is empty. How does " +
                "the combat resolve?",
            Options =
            [
                "No Units are Killed, All Units Recall, Player 2 draws 1",
                "Travelling Merchant is Killed, Mountain Drake Recalls",
                "No Units are Killed, Yasuo and Travelling Merchant Recall, Player 2 Draws 1",
                "Mountain Drake is Killed, Player 2 Conquers",
                "No Units Are Killed, Yasuo and Travelling Merchant Recall",
            ],
            // Sjoerd gokte hier eerder optie 2 maar trok dat terug (geen
            // geverifieerd antwoord) — bewust null, zie issue #158.
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-10", Category = JudgeCategory,
            Question = "Given a Mind Unit with the ability \"When I hold, draw 1\" and a Calm " +
                "Unit with the ability \"At the start of your Beginning Phase, draw 1\" at the " +
                "same battlefield, in what sequence will these abilities resolve at the start " +
                "of their controller's turn in relation to gaining a point.",
            Options =
            [
                "Gain 1 Point, Calm or Mind",
                "Calm or Mind, Gain 1 Point",
                "Calm, Gain 1 Point, Mind",
                "Gain 1 Point, Mind, Calm",
            ],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-11", Category = JudgeCategory,
            Question = "In Low OPL, Player 1 performs their beginning of turn sequence, and " +
                "then begins considering their turn. They then exhaust two runes to play a " +
                "Travelling Merchant to their base. They realize that, with one card in their " +
                "hand, they have missed the trigger for their The Loose Cannon from the " +
                "beginning of their turn. They inform Player 2 of this, and move to correct " +
                "the game state by taking back the Travelling Merchant action, and then " +
                "drawing the card for the missed trigger. However, Player 2 does not believe " +
                "they should be able to do so. A Judge is called to settle the opposing view. " +
                "Which of the following should happen:",
            Options =
            [
                "The trigger being missed does not constitute an OPL violation, and Player 1 " +
                    "should be allowed to rewind and enact the missed trigger",
                "The trigger being missed constitutes an OPL violation, and Player 1 should " +
                    "receive a warning – and no rewind",
                "The trigger being missed constitutes an OPL violation, and Player 1 should " +
                    "receive a game loss",
                "The trigger being missed does not constitute an OPL violation, but Player 1 " +
                    "should execute the trigger in-place instead of performing a rewind",
            ],
            CorrectIndex = null,
        },
        new()
        {
            ExternalKey = "judge-12", Category = JudgeCategory,
            Question = "Which of the following actions does not count as playing a card for " +
                "the sake of triggering Legion on a card such as Vanguard Captain?",
            Options =
            [
                "Hiding a Hidden Blade at a battlefield you control",
                "Activating The Herald of the Arcane and playing a Recruit",
                "Machine Evangelist's deathknell triggering and playing three Recruits",
                "Killing your Forge of the Future to recycle four cards from Trashes",
                "All of the Above",
            ],
            CorrectIndex = null,
        },
    ];
}
