namespace RbRules.Domain;

/// <summary>De uitkomst van één deterministische extractie-parse: de items PLUS of
/// de body überhaupt een bruikbare envelop wás (#251-review).
///
/// Zonder dat tweede veld betekent een lege lijst twee onverenigbare dingen: "het
/// model vond niets" (geslaagd werk, <see cref="AiCallOutcome.Empty"/>) én "de body
/// was afgekapt of schema-vreemd" (uitval, <see cref="AiCallOutcome.Unparseable"/>).
/// De mining-lussen telden beide als geslaagd, waardoor een run waarin rb-ai bij
/// élke kaart een kapotte envelop teruggaf 0% uitval rapporteerde — precies de
/// samenval die #251 moest opheffen. <see cref="AiCallOutcome.Unparseable"/> bestond
/// al maar was vanuit het parse-pad onbereikbaar.
///
/// <paramref name="Malformed"/> zegt iets over de VORM, niet over de inhoud: wat de
/// enum-/lexicon-poorten van de parser wegfilteren blijft geslaagd werk (het model
/// antwoordde geldig, het antwoord haalde de tweede muur niet).</summary>
public sealed record ExtractionParse<T>(IReadOnlyList<T> Items, bool Malformed)
{
    /// <summary>Een geldige envelop zonder items — geslaagd werk, geen uitval.</summary>
    public static ExtractionParse<T> Empty { get; } = new([], false);

    /// <summary>Geen bruikbare envelop: JSON-fout, afgekapte body of een
    /// schema-vreemde vorm (bv. <c>{"interactions":"none"}</c>).</summary>
    public static ExtractionParse<T> Broken { get; } = new([], true);
}
