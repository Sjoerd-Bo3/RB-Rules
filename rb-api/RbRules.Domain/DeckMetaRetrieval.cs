using System.Globalization;

namespace RbRules.Domain;

/// <summary>Een mede-gespeelde kaart in het deck-meta-blok (#267): naam plus
/// het aantal recente decks waarin beide kaarten samen voorkomen.</summary>
public record DeckMetaCoPlay(string Name, int DeckCount);

/// <summary>Kandidaat voor het deck-meta-kanaal: een canonieke kaart waarvan
/// de naam letterlijk in de vraag voorkomt (CardsNamedIn-match).</summary>
public record DeckMetaCard(string RiftboundId, string Name);

/// <summary>Deck-meta voor één kaart zoals het /ask-kanaal hem aanlevert
/// (kennislaag 3, #267): dezelfde velden als het "In decks"-dossierblok op de
/// kaartpagina (CardDeckPopularity), maar dan de subset die de prompt nodig
/// heeft. ThinData volgt de dossier-afspraak (#15): onder de drempel is een
/// percentage misleidend, dus dan gaan de absolute aantallen mee.</summary>
public record RetrievedDeckMeta(
    string CardName, int DeckCount, int RecentDeckCount, double Percentage,
    double? AverageCopies, bool ThinData, IReadOnlyList<DeckMetaCoPlay> TopCoPlayed);

/// <summary>Poort en prompt-opbouw voor het deck-meta-kanaal in /ask
/// (kennislaag 3, #267). Puur en getest; de retrieval zelf (deck-tabellen)
/// leeft in AskService/DeckPopularityQuery. Hard principe uit
/// docs/KNOWLEDGE.md: lagen worden expliciet gelabeld, en meta is de zwakste
/// laag — community-metagegevens, geen officiële regel.</summary>
public static class DeckMetaRetrieval
{
    /// <summary>Maximaal aantal kaarten waarvoor deck-meta meegaat: elke kaart
    /// kost eigen deck-query's, en het blok is context, geen hoofdinhoud.</summary>
    public const int MaxCards = 2;

    /// <summary>De hotpath-poort (#267): deck-meta wordt alléén opgehaald bij
    /// kaart-specifieke of deck-/meta-gerelateerde vragen (router-type Kaart
    /// of Lijst — Lijst dekt ook de meta-vragen, zie QuestionRouter) waarin
    /// bovendien een kaartnaam herkend is (de naam-match die de router toch
    /// al nodig had — geen extra query voor de poort zelf). Elke andere vraag,
    /// in het bijzonder een regelvraag zonder kaarten, doet géén enkele
    /// deck-query. Zonder herkende kaart valt er bovendien niets op te halen:
    /// het signaal is per kaart berekend (archetype-detectie is expliciet
    /// buiten scope, #267).</summary>
    public static bool ShouldRetrieve(QuestionType type, bool mentionsCard) =>
        mentionsCard && type is QuestionType.Kaart or QuestionType.Lijst;

    /// <summary>Kaart-selectie voor het kanaal (#318-review B1), dezelfde
    /// dedup-regel als AgenticGate.CountDistinctMentions: een naam die
    /// substring is van een langere match is dezélfde vermelding — "Jinx"
    /// raakt binnen "Jinx, Loose Cannon" — en mag geen eigen slot (en dus
    /// geen eigen deck-query's) verbruiken. Vrijwel elke legend "X, Epithet"
    /// heeft een champion-unit "X" naast zich, dus zonder dedup zou bijna
    /// elke legend-vraag een ongevraagde tweede kaart meenemen. Daarna de
    /// langste (meest specifieke) naam eerst, deterministisch getie-breakt,
    /// gecapt op <see cref="MaxCards"/>.</summary>
    public static IReadOnlyList<DeckMetaCard> SelectCards(
        IReadOnlyCollection<DeckMetaCard> matches) =>
        [.. matches
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Where(m => !matches.Any(other =>
                other.Name.Length > m.Name.Length &&
                other.Name.Contains(m.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.Name.Length)
            .ThenBy(m => m.RiftboundId, StringComparer.Ordinal)
            .Take(MaxCards)];

    /// <summary>Regel per kaart, invariant genoteerd (punt als decimaalteken,
    /// zelfde afspraak als ClaimRetrieval.PromptLabel). Bij ThinData (#15)
    /// absolute aantallen in plaats van een percentage-claim.</summary>
    public static string Line(RetrievedDeckMeta m)
    {
        var share = m.ThinData
            ? $"gespeeld in {m.DeckCount} van de {m.RecentDeckCount} recentste decks " +
              "(dun sample — indicatief)"
            : $"gespeeld in {m.Percentage.ToString("0.#", CultureInfo.InvariantCulture)}% " +
              $"van de {m.RecentDeckCount} recentste decks";
        var copies = m.AverageCopies is { } avg
            ? $", gemiddeld {avg.ToString("0.0", CultureInfo.InvariantCulture)} exemplaren"
            : "";
        var coPlayed = m.TopCoPlayed.Count == 0
            ? ""
            : "; vaak samen met: " + string.Join(", ", m.TopCoPlayed.Select(c =>
                $"{c.Name} ({c.DeckCount} {(c.DeckCount == 1 ? "deck" : "decks")})"));
        return $"- [deck-meta] {m.CardName}: {share}{copies}{coPlayed}";
    }

    /// <summary>Het contextblok voor de prompt: expliciet gelabeld als
    /// kennislaag 3 — de zwakste laag van de piramide (docs/KNOWLEDGE.md:
    /// officieel > geverifieerde rulings > primer > community-claims > meta) —
    /// met omgangsregels die de framing afdwingen: meta is context bij meta-/
    /// deckbouw-/kaartvragen en draagt nooit een oordeel of regel-uitleg.</summary>
    public static string PromptBlock(IReadOnlyList<RetrievedDeckMeta> items)
    {
        if (items.Count == 0) return "";
        var lines = string.Join("\n", items.Select(Line));
        return "\n\nDECK-META (kennislaag 3, de zwakste laag: community-metagegevens uit "
            + "recente decklijsten — géén officiële regel, ruling of kaarttekst):\n"
            + lines
            + "\nOmgang met deck-meta:\n"
            + "- Dit beschrijft hoe de community speelt (populariteit, veelgebruikte "
            + "combinaties), nooit wat mag of hoe iets werkt. Gebruik het uitsluitend als "
            + "context bij meta-, deckbouw- of kaartvragen; past het niet bij de vraag, "
            + "laat het volledig weg.\n"
            + "- Een Oordeel of regel-uitleg mag hier nooit op steunen: alle andere lagen "
            + "(officiële regels, kaartgegevens, geverifieerde rulings, primer, "
            + "community-interpretatie) winnen van deck-meta.\n"
            + "- Benoem deck-meta in het antwoord altijd expliciet als community-gegevens "
            + "(\"in recente decklijsten…\"), nooit als officiële status of aanbeveling "
            + "met gezag.";
    }
}
