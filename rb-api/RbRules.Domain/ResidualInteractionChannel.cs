namespace RbRules.Domain;

/// <summary>Eén kaart met zijn VOORAF-BEGRENSDE dichtstbijzijnde buren (fase 5, #229,
/// §5): de top-K semantisch dichtste andere kaarten met hun cosine-gelijkenis. In
/// productie komt deze lijst uit de HNSW-index (pgvector, top-K per kaart) — nooit een
/// volle N×N-matrix; dat is exact wat het residuele kanaal begrensd houdt.</summary>
public sealed record CardNeighborhood(
    string CardRef, IReadOnlyList<CardNeighbor> Neighbors);

/// <summary>Eén buur + cosine-gelijkenis (0..1).</summary>
public readonly record struct CardNeighbor(string OtherRef, double Cosine);

/// <summary>Eén residuele kandidaat-interactie zonder structurele signatuur (fase 5,
/// #229, §5): opgepikt puur op embedding-nabijheid. Expliciet GEMARKEERD als laag-
/// prioriteit en afkomstig uit het residuele kanaal, zodat retrieval/review het niet
/// verwart met een structureel-onderbouwde hypothese.</summary>
public sealed record ResidualCandidate(string ARef, string BRef, double Cosine)
{
    /// <summary>Het kanaal-merk — voor de review-UI en de provenance.</summary>
    public const string Channel = "residual-embedding";

    public string UnorderedPairKey =>
        string.CompareOrdinal(ARef, BRef) <= 0 ? $"{ARef}|{BRef}" : $"{BRef}|{ARef}";
}

/// <summary>De begroting van het residuele kanaal (fase 5, #229, §5 / beslissing #232):
/// het is LAAG-prioriteit en BEGRENSD — nooit de volle N²-scan (kritiek B7). Drie
/// harde grenzen: een <see cref="CosineFloor"/> (alleen echt-nabije paren),
/// <see cref="PerCardNeighbors"/> (hoeveel buren per kaart maximaal meetellen — de
/// top-K uit de ANN-index) en een absoluut <see cref="MaxCandidates"/>-budgetplafond
/// op het hele kanaal. Zo blijft het werk lineair in N·K en het aantal LLM-calls onder
/// een vast plafond, ongeacht de kaartaantallen.</summary>
public sealed record ResidualChannelBudget(
    double CosineFloor = 0.82,
    int PerCardNeighbors = 5,
    int MaxCandidates = 50);

/// <summary>Het dunne residuele embedding-cosine-kanaal (fase 5, #229, §5) — puur en
/// getest. Vangt interacties ZONDER structurele signatuur op (geen gedeeld predicaat,
/// dus onzichtbaar voor de <see cref="HypothesisEngine"/>), maar BEGRENSD: het werkt op
/// vooraf-begrensde top-K-burenlijsten (uit de HNSW-index, geen N²-matrix), knipt op
/// een cosine-vloer, sluit paren uit die al structureel gedekt zijn (geen dubbel werk),
/// en kapt hard af op een budgetplafond. De uitvoer is expliciet laag-prioriteit
/// gemarkeerd (<see cref="ResidualCandidate.Channel"/>). De live pgvector-ANN-query is
/// een integratie-follow-up; deze logica is de begrenzing zelf.</summary>
public static class ResidualInteractionChannel
{
    /// <summary>Selecteer de residuele kandidaten. <paramref name="neighborhoods"/> zijn
    /// de top-K-burenlijsten per kaart; <paramref name="structurallyCovered"/> zijn de
    /// ongeordende paar-sleutels die de hypothese-motor al opleverde (worden
    /// overgeslagen — het residuele kanaal is er JUIST voor de rest). Deterministisch
    /// geordend op cosine (aflopend), dan paar-sleutel; ongeordend gededupliceerd en
    /// hard afgekapt op <see cref="ResidualChannelBudget.MaxCandidates"/>.</summary>
    public static IReadOnlyList<ResidualCandidate> Select(
        IReadOnlyList<CardNeighborhood> neighborhoods,
        IReadOnlySet<string>? structurallyCovered = null,
        ResidualChannelBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(neighborhoods);
        budget ??= new ResidualChannelBudget();
        var covered = structurallyCovered ?? new HashSet<string>(StringComparer.Ordinal);

        // Per paar de sterkste cosine (een paar kan uit beide burenlijsten opduiken;
        // begrensd door PerCardNeighbors per kaart → werk is Σ min(K, |buren|)).
        var best = new Dictionary<string, ResidualCandidate>(StringComparer.Ordinal);
        foreach (var nh in neighborhoods)
        {
            if (string.IsNullOrWhiteSpace(nh.CardRef)) continue;
            var taken = 0;
            foreach (var nb in nh.Neighbors)
            {
                if (taken >= Math.Max(0, budget.PerCardNeighbors)) break;  // top-K-begrenzing
                taken++;
                if (string.IsNullOrWhiteSpace(nb.OtherRef) || nb.OtherRef == nh.CardRef) continue;
                if (nb.Cosine < budget.CosineFloor) continue;              // cosine-vloer
                var cand = new ResidualCandidate(nh.CardRef, nb.OtherRef, nb.Cosine);
                var key = cand.UnorderedPairKey;
                if (covered.Contains(key)) continue;                       // al structureel gedekt
                if (!best.TryGetValue(key, out var existing) || cand.Cosine > existing.Cosine)
                    best[key] = cand with { ARef = Min(cand), BRef = Max(cand) };
            }
        }

        return best.Values
            .OrderByDescending(c => c.Cosine)
            .ThenBy(c => c.UnorderedPairKey, StringComparer.Ordinal)
            .Take(Math.Max(0, budget.MaxCandidates))                       // hard budgetplafond
            .ToList();
    }

    private static string Min(ResidualCandidate c) =>
        string.CompareOrdinal(c.ARef, c.BRef) <= 0 ? c.ARef : c.BRef;

    private static string Max(ResidualCandidate c) =>
        string.CompareOrdinal(c.ARef, c.BRef) <= 0 ? c.BRef : c.ARef;
}
