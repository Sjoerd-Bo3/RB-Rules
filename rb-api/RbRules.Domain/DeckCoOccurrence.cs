namespace RbRules.Domain;

/// <summary>De co-occurrence-statistiek van één structureel voorspeld kaartpaar
/// tegen de echte Piltover-deck-meta (#231/#15, spec §7 — deck-integratie).
/// <see cref="CoDecks"/> = aantal decks dat BEIDE kaarten speelt; <see
/// cref="Support"/> = dat gedeeld door alle decks; <see cref="Lift"/> = hoeveel vaker
/// ze samen voorkomen dan bij onafhankelijk spelen verwacht (>1 = echte synergie,
/// het meetbare signaal dat een structureel pad bevestigt). <see
/// cref="Corroborated"/> = het paar haalt de co-deck-drempel — het structurele
/// combo-pad is dan door de meta gekruisvalideerd.</summary>
public sealed record CoOccurrenceStat(
    string PairKey,
    string A,
    string B,
    int CoDecks,
    int DecksWithA,
    int DecksWithB,
    int TotalDecks,
    double Support,
    double Lift,
    bool Corroborated);

/// <summary>Het CO_OCCURS-kruisvalidatie-rapport: hoeveel van de structureel
/// voorspelde combo-paden (fase 5) daadwerkelijk samen gespeeld worden.
/// <see cref="CorroborationRate"/> is de precisie van de structuurvoorspelling tegen
/// de echte meta — een echt getal voor de admin-tegel, geen folklore.</summary>
public sealed record DeckCoOccurrenceReport(
    int TotalDecks,
    int PredictedPairs,
    int CorroboratedPairs,
    double CorroborationRate,
    IReadOnlyList<CoOccurrenceStat> Stats);

/// <summary>Kruisvalideert structureel voorspelde combo-paren (fase 5,
/// <see cref="HypothesisEngine"/>/interactie-paden) met de echte meta uit Piltover-
/// decks (#15) — als MEETBAAR signaal: structureel voorspeld ∩ echt samen gespeeld
/// (#231, spec §7). Puur/IO-loos: de aanroeper levert de decks als canonieke
/// kaart-id-verzamelingen (via <see cref="DeckCardLinker"/>) en de voorspelde paren
/// (als canonieke kaart-id-paren, uit de hypothese-paar-sleutels door de ontologie
/// gemapt). Live-graaf-pathfinding blijft een integratie-follow-up; deze meting is
/// puur set-rekenkunde.</summary>
public static class DeckCoOccurrence
{
    /// <summary>Standaard co-deck-drempel: een paar heet gecorroboreerd zodra het in
    /// minstens zoveel decks SAMEN voorkomt. 1 = "ooit samen gezien"; hoger filtert
    /// toevalstreffers.</summary>
    public const int DefaultMinCoDecks = 1;

    /// <summary>Meet de co-occurrence van de voorspelde paren. <paramref name="decks"/>
    /// zijn de canonieke kaart-id-verzamelingen per deck; <paramref name="predictedPairs"/>
    /// de structureel voorspelde kaartparen (richting doet er niet toe — geordend op
    /// de ongeordende sleutel). Gesorteerd op lift (sterkste synergie eerst).</summary>
    public static DeckCoOccurrenceReport Measure(
        IReadOnlyList<IReadOnlyCollection<string>> decks,
        IEnumerable<(string A, string B)> predictedPairs,
        int minCoDecks = DefaultMinCoDecks)
    {
        ArgumentNullException.ThrowIfNull(decks);
        ArgumentNullException.ThrowIfNull(predictedPairs);

        var total = decks.Count;

        // Kaart → verzameling deck-indices (voor snelle co-deck-doorsnede). Elke deck
        // als set zodat dubbele kaartregels (varianten die naar dezelfde canonical
        // resolven) niet dubbeltellen.
        var cardToDecks = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var i = 0; i < total; i++)
            foreach (var card in decks[i].Distinct(StringComparer.Ordinal))
                (cardToDecks.TryGetValue(card, out var set)
                    ? set
                    : cardToDecks[card] = new HashSet<int>()).Add(i);

        // Dedup de voorspelde paren op de ongeordende sleutel (een interactie is als
        // kandidaat symmetrisch); zelf-paren (A==B) overslaan.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stats = new List<CoOccurrenceStat>();
        foreach (var (a, b) in predictedPairs)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) continue;
            var (lo, hi) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
            var key = $"{lo}|{hi}";
            if (!seen.Add(key)) continue;

            var decksA = cardToDecks.GetValueOrDefault(lo);
            var decksB = cardToDecks.GetValueOrDefault(hi);
            var withA = decksA?.Count ?? 0;
            var withB = decksB?.Count ?? 0;
            var coDecks = decksA is null || decksB is null
                ? 0
                : decksA.Count(decksB.Contains);

            stats.Add(new CoOccurrenceStat(
                key, lo, hi, coDecks, withA, withB, total,
                Support: total == 0 ? 0.0 : (double)coDecks / total,
                Lift: Lift(coDecks, withA, withB, total),
                Corroborated: coDecks >= minCoDecks));
        }

        var corroborated = stats.Count(s => s.Corroborated);
        var ordered = stats
            .OrderByDescending(s => s.Lift)
            .ThenByDescending(s => s.CoDecks)
            .ThenBy(s => s.PairKey, StringComparer.Ordinal)
            .ToList();

        return new DeckCoOccurrenceReport(
            total,
            stats.Count,
            corroborated,
            stats.Count == 0 ? 0.0 : (double)corroborated / stats.Count,
            ordered);
    }

    /// <summary>Lift = P(A∧B) ÷ (P(A)·P(B)). Ontbrekende kaart of nul decks → 0
    /// (geen signaal, geen deling door 0). 1.0 = precies toeval; &gt;1 = synergie.</summary>
    private static double Lift(int coDecks, int withA, int withB, int total)
    {
        if (total == 0 || withA == 0 || withB == 0) return 0.0;
        var pAB = (double)coDecks / total;
        var pA = (double)withA / total;
        var pB = (double)withB / total;
        return pAB / (pA * pB);
    }
}
