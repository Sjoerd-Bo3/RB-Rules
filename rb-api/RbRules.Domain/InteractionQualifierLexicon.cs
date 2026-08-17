namespace RbRules.Domain;

/// <summary>Fase-2-extractie (#226, §3.1) — het gesloten qualifier-lexicon
/// (Window/Status) waarmee de tool-forced interactie-extractie haar conditie-enums
/// sluit. Zoals <see cref="MechanicPredicateLexicon"/> is dit een SEED: de
/// <see cref="InteractionExtraction"/>-parser weigert een window/status buiten dit
/// lexicon (tweede muur), maar de set is bewust uitbreidbaar — een nieuwe set mag
/// nieuwe timing-windows/toestanden introduceren (CLAUDE.md: de kennisbank
/// mee-evolueren) via review, niet via een stille code-lek. Bewust NIET het
/// COST-lexicon: cost-condities zijn vrijer gestructureerd (operator + floor) en
/// worden lexicaal niet gesloten (zie <see cref="InteractionExtraction"/>).</summary>
public static class InteractionQualifierLexicon
{
    /// <summary>Timing-windows (WINDOW-conditie-as, §2.3). Showdown is het
    /// kanonieke voorbeeld uit de architectuur; de rest is een redelijke seed die
    /// de review/evolutie uitbreidt.</summary>
    public static readonly IReadOnlyList<string> Windows =
        ["Showdown", "Reaction", "Beginning", "Main", "Combat", "Ending", "Channeling"];

    /// <summary>Object-toestanden (STATUS-conditie-as, §2.3). Dekt de
    /// architectuur-voorbeelden (Exhausted, Empowered, Stunned) plus courante
    /// tegenhangers.</summary>
    public static readonly IReadOnlyList<string> Statuses =
        ["Exhausted", "Ready", "Empowered", "Stunned", "Hidden"];
}
