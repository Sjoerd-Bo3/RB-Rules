using System.Globalization;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Een geaccepteerde community-claim zoals hij het /ask-kanaal in
/// gaat (kennislaag 2, #51): de subset van de Claim-entiteit die de prompt en
/// het antwoord nodig hebben.</summary>
public record RetrievedClaim(
    string TopicType, string TopicRef, string Statement,
    int Corroboration, double TrustScore, string OfficialStatus);

/// <summary>Een gedocumenteerde misvatting (#125): een verworpen of
/// achterhaalde community-claim mét officiële weerlegging (StatusReason;
/// zodra #124 landt komt daar de verwerp-notitie bij). Negatieve kennis —
/// hoe het NIET zit, en waarom.</summary>
public record RetrievedMisconception(
    string TopicType, string TopicRef, string Statement, string Rebuttal);

/// <summary>Kandidaat-rij voor het misvattingen-kanaal zoals de
/// nearest-neighbour-query hem aanlevert — status, weerlegging en afstand
/// nog ongefilterd; de poort is <see cref="ClaimRetrieval.SelectMisconceptions"/>.
/// Id blijft erbij zodat de service de bronnen (citaat + URL) van de
/// winnaars kan bijladen.</summary>
public record MisconceptionCandidate(
    long Id, string TopicType, string TopicRef, string Statement,
    string Status, string? StatusReason, double Distance);

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

    // ── Misvattingen-kanaal (#125): verworpen claims als negatieve kennis ──

    /// <summary>Cap op het misvattingen-kanaal: maximaal twee per antwoord —
    /// negatieve kennis is kanttekening, geen hoofdinhoud, en elke misvatting
    /// kost promptruimte én aandacht in de "Let op"-sectie.</summary>
    public const int MisconceptionCap = 2;

    /// <summary>De poort van het misvattingen-kanaal, puur en getest: alleen
    /// rejected/superseded claims mét weerlegging doen mee (een kale rejected
    /// zonder reden is geen kennis), binnen hetzelfde afstands-plafond als het
    /// claims-kanaal, gecapt op <see cref="MisconceptionCap"/>. De SQL-query in
    /// AskService is een voorselectie; deze poort is de waarheid en draait ook
    /// over wat de query aanlevert.</summary>
    public static IReadOnlyList<MisconceptionCandidate> SelectMisconceptions(
        IEnumerable<MisconceptionCandidate> candidates) =>
        [.. candidates
            .Where(c => c.Status is "rejected" or "superseded"
                && !string.IsNullOrWhiteSpace(c.StatusReason)
                && c.Distance <= MaxDistance)
            .OrderBy(c => c.Distance)
            .Take(MisconceptionCap)];

    /// <summary>§-code in een weerleggingstekst ("… §466.2 zegt …") — de
    /// OfficialCheck-reason is één NL-zin mét §-verwijzing; de beheerders-
    /// afwijzing ("door de beheerder afgewezen") heeft er geen.</summary>
    private static readonly Regex SectionInRebuttal = new(
        @"§\s*(\d+(?:\.\d+)*(?:\.[a-z])?)", RegexOptions.Compiled);

    /// <summary>De §-code uit de weerlegging, voor het promptlabel en de
    /// sectie-link in de UI; null als de weerlegging geen § noemt.</summary>
    public static string? RebuttalSection(string rebuttal)
    {
        var match = SectionInRebuttal.Match(rebuttal);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Label per misvatting: "[misvatting, weerlegd door §466.2]" —
    /// zonder §-code in de weerlegging het generieke "officieel weerlegd".</summary>
    public static string MisconceptionPromptLabel(RetrievedMisconception m) =>
        RebuttalSection(m.Rebuttal) is { } section
            ? $"[misvatting, weerlegd door §{section}]"
            : "[misvatting, officieel weerlegd]";

    /// <summary>Contextblok voor de prompt (#125): gedocumenteerde misvattingen
    /// als negatieve kennis, met omgangsregels die de framing afdwingen —
    /// alleen benoemen als de vraag er echt op lijkt, altijd in de "Let op"-
    /// sectie, nooit als waarheid. Het citatencontract van #69 blijft gelden:
    /// geen eigen Regelbasis-sectie, verwijzen gaat met [n] in de tekst.</summary>
    public static string MisconceptionBlock(IReadOnlyList<RetrievedMisconception> misconceptions)
    {
        if (misconceptions.Count == 0) return "";
        var lines = string.Join("\n", misconceptions.Select(m =>
            $"- {MisconceptionPromptLabel(m)} {m.TopicRef}: \"{m.Statement}\" — weerlegging: {m.Rebuttal}"));
        return "\n\nGEDOCUMENTEERDE MISVATTINGEN (community-lezingen die officieel weerlegd zijn "
            + "— dit is hoe het NIET zit):\n"
            + lines
            + "\nOmgang met misvattingen:\n"
            + "- Benoem een misvatting uitsluitend als de vraag er inhoudelijk echt op lijkt; "
            + "bij twijfel laat je hem volledig weg.\n"
            + "- Benoem hem alléén in de sectie `### Let op`, met precies deze framing: "
            + "\"een veelgemaakte lezing is X, maar [n] zegt Y\" — [n] is het context-fragment "
            + "dat de weerlegging draagt; staat dat fragment er niet bij, noem dan de §-code "
            + "uit de weerlegging in de lopende tekst.\n"
            + "- Presenteer een misvatting nooit als waarheid, regel of oordeel: hij mag een "
            + "antwoord nuanceren, nooit dragen.";
    }
}
