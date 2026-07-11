using System.Globalization;

namespace RbRules.Domain;

/// <summary>Een geaccepteerde community-claim zoals hij het /ask-kanaal in
/// gaat (kennislaag 2, #51): de subset van de Claim-entiteit die de prompt en
/// het antwoord nodig hebben.</summary>
public record RetrievedClaim(
    string TopicType, string TopicRef, string Statement,
    int Corroboration, double TrustScore, string OfficialStatus);

/// <summary>Router-gewicht en prompt-opbouw voor het community-claimskanaal in
/// /ask (#51). Puur en getest; de retrieval zelf (pgvector) leeft in
/// AskService. Hard principe uit docs/KNOWLEDGE.md: lagen worden expliciet
/// gelabeld en interpretatie mag een oordeel kleuren, nooit dragen.</summary>
public static class ClaimRetrieval
{
    /// <summary>Afstands-plafond (cosine) voor het claimskanaal: het kanaal is
    /// aanvullend — liever géén claims dan claims over een ander onderwerp.
    /// Bewust ruimer dan de cluster-poort van de miner (0.35): hier matcht een
    /// vraag tegen een bewering, niet een bewering tegen een parafrase.</summary>
    public const double MaxDistance = 0.55;

    /// <summary>Router-gestuurd gewicht (issue #51 / docs/KNOWLEDGE.md:
    /// "ruling: laag; conventie-/tactiekvraag: hoog"): hoeveel claims er
    /// maximaal meegaan per vraagtype.</summary>
    public static int TakeFor(QuestionType type) => type switch
    {
        // Lijst dekt ook meta-vragen — daar is community-kennis de kern.
        QuestionType.Lijst => 4,
        QuestionType.Definitie or QuestionType.Kaart => 3,
        // Ruling/Legaliteit/Toernooi zijn normatief: interpretatie weegt licht.
        _ => 2,
    };

    /// <summary>Label per claim, het format uit docs/KNOWLEDGE.md:
    /// "[community, 4 bronnen, trust 0.94]" — invariant genoteerd (punt als
    /// decimaalteken), met de officiële bevestiging als extra signaal.</summary>
    public static string PromptLabel(RetrievedClaim c) =>
        $"[community, {c.Corroboration} {(c.Corroboration == 1 ? "bron" : "bronnen")}, " +
        $"trust {c.TrustScore.ToString("0.00", CultureInfo.InvariantCulture)}]" +
        (c.OfficialStatus == "confirmed" ? " (door de officiële regels bevestigd)" : "");

    /// <summary>Het contextblok voor de prompt: expliciet gelabelde claims plus
    /// de omgangsregels van issue #51 — community kleurt maar draagt nooit, een
    /// apart "Community-consensus"-blok in het antwoord, en het uitgebreide
    /// zekerheidslabel. Conform het citatencontract van #69 verwijst het model
    /// nooit zelf naar bronnen of URL's: de site toont de claims mét bronnen
    /// als eigen blok onder het antwoord.</summary>
    public static string PromptBlock(IReadOnlyList<RetrievedClaim> claims)
    {
        if (claims.Count == 0) return "";
        var lines = string.Join("\n", claims.Select(c =>
            $"- {PromptLabel(c)} {c.TopicRef}: {c.Statement}"));
        return "\n\nCOMMUNITY-INTERPRETATIE (géén officiële bron — zo leest de community het):\n"
            + lines
            + "\nOmgang met community-interpretatie:\n"
            + "- Officiële regels, kaartgegevens en GEVERIFIEERDE RULINGS winnen altijd; "
            + "interpretatie mag je oordeel kleuren, nooit dragen.\n"
            + "- Gebruik je een interpretatie inhoudelijk, sluit het antwoord dan af met een "
            + "sectie `### Community-consensus`: per gebruikte interpretatie één zin, met de "
            + "corroboratie erbij (\"N bronnen lezen dit zo\"). Noem daar geen URL's of "
            + "bronnamen — de site toont de claims met hun bronnen apart onder het antwoord. "
            + "Geen interpretatie gebruikt: laat de sectie weg.\n"
            + "- Waar het format een Zekerheid-regel heeft: steunt het oordeel zélf uitsluitend "
            + "op community-interpretatie (geen dragende officiële § of geverifieerde ruling), "
            + "schrijf dan `Community-consensus (N bronnen)`; steunt het op officiële bronnen, "
            + "schrijf `Bevestigd (officieel)`.";
    }
}
