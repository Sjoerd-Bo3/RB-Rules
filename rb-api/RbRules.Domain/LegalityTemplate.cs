using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Deterministisch antwoord op een zuivere legaliteitsvraag (#383):
/// "is X banned?", "mag ik X spelen?", "is X legaal?". De banlijst en de
/// set-legaliteit zijn gezaghebbende tabellen; ze naar een taalmodel sturen om
/// er een alinea van te maken kost tientallen seconden en voegt niets toe wat
/// een sjabloon niet kan — met als extra risico dat het model iets bij de tabel
/// verzint. Precies op de vragen waar een fout het meest kost.
///
/// GRENZEN, bewust: alleen als er een kaart herkend is en de vraag NIET over
/// deckbouw gaat (kopie-limieten, playsets) — dat vraagt de deckbouwregels uit
/// de Core Rules, en daar is de gewone retrieval voor. Twijfel ⇒ null ⇒ het
/// bestaande LLM-pad, byte-voor-byte ongewijzigd.
///
/// De uitvoer volgt <see cref="QuestionRouter.StructureFor"/> voor Legaliteit
/// (Oordeel / Zekerheid / Uitleg / Let op), zodat rb-web hem rendert als elk
/// ander antwoord. Puur: geen IO, 'vandaag' is een parameter.</summary>
public static partial class LegalityTemplate
{
    /// <summary>Deckbouwvragen horen niet in het sjabloon: het antwoord staat
    /// in de Core Rules (kopie-limieten per kaartsoort), niet in de banlijst.</summary>
    [GeneratedRegex(
        @"\b(hoeveel (kopie|exemplar)|deck ?(bouw|construction|limiet)|4x|playset|kopie(ën|s)?|copies|exemplaren|rotatie|rotation)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeckConstruction();

    /// <summary>Wat het sjabloon per herkende kaart nodig heeft. Alles hierin
    /// komt uit gezaghebbende tabellen (banlijst, sets); niets uit tekst.</summary>
    public sealed record CardFact(
        string Name,
        bool Banned,
        string? BanFormat,
        DateOnly? BanEffectiveFrom,
        string? SetName,
        DateOnly? LegalFrom);

    /// <summary>Het sjabloonantwoord plus de ene bron die het draagt.</summary>
    public sealed record Result(string Answer, string SourceName, string SourceUrl);

    /// <summary>Mag het sjabloon deze vraag beantwoorden? Vereist minstens één
    /// herkende kaart en géén deckbouw-formulering.</summary>
    public static bool Applies(string question, int recognisedCards) =>
        recognisedCards > 0 && IsPureLegalityQuestion(question);

    /// <summary>Goedkope voorcheck zonder databaseraadpleging: gaat de vraag
    /// niet over deckbouw? Zo niet, dan hoeft de kaartlookup niet te draaien.</summary>
    public static bool IsPureLegalityQuestion(string question) =>
        !DeckConstruction().IsMatch(question);

    /// <summary>Bouwt het antwoord. <paramref name="sourceName"/>/<paramref
    /// name="sourceUrl"/> is de officiële banlijstbron (de Rules Hub) — bij een
    /// gebande kaart de bron van die ban, anders de hub zelf.</summary>
    public static Result Build(
        IReadOnlyList<CardFact> cards, DateOnly today, string sourceName, string sourceUrl)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (cards.Count == 0) throw new ArgumentException("minstens één kaart", nameof(cards));

        var sb = new StringBuilder();
        var nl = CultureInfo.GetCultureInfo("nl-NL");

        // Oordeel: één zin, ook bij meerdere kaarten.
        var verdicts = cards.Select(c => Verdict(c, today)).ToList();
        sb.Append("**Oordeel:** ").Append(string.Join(" ", verdicts)).Append('\n');
        sb.Append("**Zekerheid:** Bevestigd\n");

        sb.Append("### Uitleg\n");
        foreach (var c in cards)
        {
            if (c.Banned)
            {
                sb.Append($"- {c.Name} staat op de actuele banlijst [1]");
                if (!string.IsNullOrWhiteSpace(c.BanFormat))
                    sb.Append($" voor {c.BanFormat}");
                if (c.BanEffectiveFrom is { } from)
                    sb.Append($", sinds {from.ToString("d MMMM yyyy", nl)}");
                sb.Append(".\n");
            }
            else
            {
                sb.Append($"- {c.Name} staat niet op de actuele banlijst [1].\n");
            }

            switch (SetLegality.StatusFor(c.LegalFrom, today))
            {
                case SetLegalityStatus.Legal when c.LegalFrom is { } lf:
                    sb.Append($"- {c.Name} komt uit {c.SetName ?? "een verschenen set"}, legaal sinds {lf.ToString("d MMMM yyyy", nl)}.\n");
                    break;
                case SetLegalityStatus.Upcoming when c.LegalFrom is { } lf:
                    sb.Append($"- {c.Name} komt uit {c.SetName ?? "een aangekondigde set"} en is pas legaal vanaf {lf.ToString("d MMMM yyyy", nl)}.\n");
                    break;
                default:
                    // Announced/onbekende datum: géén harde claim — een onbekende
                    // releasedatum kan net zo goed een allang verschenen set zijn
                    // (SetLegality.Announced-kanttekening).
                    if (c.SetName is not null)
                        sb.Append($"- {c.Name} komt uit {c.SetName}.\n");
                    break;
            }
        }

        // Let op: alleen als er iets te melden is (de structuur zegt "anders weglaten").
        var letOp = new List<string>();
        foreach (var c in cards)
            if (SetLegality.StatusFor(c.LegalFrom, today) == SetLegalityStatus.Upcoming)
                letOp.Add($"- Tot de release van {c.SetName ?? "die set"} mag {c.Name} niet in een Constructed-deck.");
        if (cards.Any(c => c.Banned) && cards.Any(c => !c.Banned))
            letOp.Add("- Niet alle genoemde kaarten hebben dezelfde status; zie de uitleg per kaart.");
        if (letOp.Count > 0)
        {
            sb.Append("### Let op\n");
            foreach (var l in letOp) sb.Append(l).Append('\n');
        }

        return new Result(sb.ToString().TrimEnd(), sourceName, sourceUrl);
    }

    private static string Verdict(CardFact c, DateOnly today)
    {
        if (c.Banned) return $"{c.Name} is niet toegestaan: de kaart staat op de banlijst.";
        return SetLegality.StatusFor(c.LegalFrom, today) == SetLegalityStatus.Upcoming
            ? $"{c.Name} is nog niet toegestaan: de set is nog niet verschenen."
            : $"{c.Name} is toegestaan.";
    }
}
