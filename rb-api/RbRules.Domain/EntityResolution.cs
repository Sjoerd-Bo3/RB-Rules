using RbRules.Domain.Ontology;

namespace RbRules.Domain;

/// <summary>Fase 1 (#225) — canonieke entiteiten &amp; entity-resolution. Alle
/// PURE, IO-loze bouwstenen die faalmodus #1 (duplicatie) en #2
/// (synoniem-proliferatie) verslaan: alias-normalisatie, magnitude-behoud, de
/// trigram-gelijkenis, de drietraps-signaal-classifier, de precisie-gate en de
/// gouden-set-evaluatie. De service-laag (<c>EntityResolutionService</c>) hangt
/// hier de IO (Postgres/Neo4j) omheen; deze klassen zijn zelf deterministisch en
/// volledig unit-testbaar zonder database.</summary>
public static class CanonicalEntityKinds
{
    /// <summary>De ENIGE toegestane <see cref="CanonicalEntity.Kind"/>-waarden —
    /// een strikte deelverzameling van de Concept-tak van de ontologie
    /// (<see cref="EntityType.Mechanic"/>/<see cref="EntityType.Keyword"/>/
    /// <see cref="EntityType.Concept"/>). De canonieke laag registreert alleen
    /// spel-abstracties; kaarten dedupen al via <c>Card.VariantOf</c>.</summary>
    public static readonly IReadOnlyList<EntityType> Allowed =
        [EntityType.Mechanic, EntityType.Keyword, EntityType.Concept];

    public const string Mechanic = "mechanic";
    public const string Keyword = "keyword";
    public const string Concept = "concept";

    /// <summary>Canonieke, lowercase kind-string (matcht de <see cref="BrainRef"/>-
    /// prefixen) voor een toegestaan <see cref="EntityType"/>; <c>null</c> als het
    /// type buiten de canonieke laag valt.</summary>
    public static string? ToKind(EntityType type) => type switch
    {
        EntityType.Mechanic => Mechanic,
        EntityType.Keyword => Keyword,
        EntityType.Concept => Concept,
        _ => null,
    };

    /// <summary>Parse een kind-string naar het ontologie-<see cref="EntityType"/>;
    /// <c>null</c> bij een onbekende/niet-toegestane waarde (de service weigert dan
    /// i.p.v. te gokken).</summary>
    public static EntityType? Parse(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        Mechanic => EntityType.Mechanic,
        Keyword => EntityType.Keyword,
        Concept => EntityType.Concept,
        _ => null,
    };

    public static bool IsValid(string? kind) => Parse(kind) is not null;
}

/// <summary>Alias-normalisatie: het canonicalisatie-oppervlak (§3.2). Brengt een
/// surface-form terug tot een case/whitespace-genormaliseerde sleutel zodat
/// <c>'showdown-window'</c> en <c>'showdown window'</c> dezelfde sleutel krijgen.
/// BEWUST wordt hier GEEN magnitude gestript (kritiek Risico 2a: 'Assault 2' mag
/// niet ononderscheidbaar worden van 'Assault 3') — dat doet <see cref="Magnitude"/>
/// apart en geeft de waarde als parameter terug.</summary>
public static class AliasNormalizer
{
    /// <summary>Lowercase, trim, en vouw elke reeks witruimte/underscore/koppelteken
    /// samen tot één spatie. Deterministisch en cultuur-onafhankelijk
    /// (<see cref="System.Globalization.CultureInfo.InvariantCulture"/>).</summary>
    public static string Normalize(string? surfaceForm)
    {
        if (string.IsNullOrWhiteSpace(surfaceForm)) return string.Empty;
        var lowered = surfaceForm.Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lowered.Length);
        var pendingSeparator = false;
        foreach (var ch in lowered)
        {
            if (char.IsWhiteSpace(ch) || ch is '_' or '-')
            {
                pendingSeparator = sb.Length > 0;
                continue;
            }
            if (pendingSeparator) { sb.Append(' '); pendingSeparator = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>Magnitude-behoud (kritiek Risico 2a). Een keyword als <c>Assault 2</c>
/// hoort tot de gedeelde <em>familie</em> <c>Assault</c> met de numerieke waarde 2
/// als parameter (HAS_KEYWORD {value}) — niet tot een aparte entiteit. Deze parser
/// splitst een trailing geheel getal af: <c>("Assault 2")</c> → basis
/// <c>"Assault"</c> + waarde 2; zonder magnitude → de kale basis + <c>null</c>. De
/// basis behoudt zijn oorspronkelijke casing (voor de CanonicalLabel); resolutie
/// normaliseert los via <see cref="AliasNormalizer"/>.</summary>
public static class Magnitude
{
    public readonly record struct Parsed(string BaseLabel, int? Value);

    public static Parsed Parse(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return new(string.Empty, null);
        var trimmed = label.Trim();
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > 0 && lastSpace < trimmed.Length - 1)
        {
            var tail = trimmed[(lastSpace + 1)..];
            if (int.TryParse(tail, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                return new(trimmed[..lastSpace].TrimEnd(), value);
        }
        return new(trimmed, null);
    }
}

/// <summary>Trigram-gelijkenis (Jaccard over 3-gram-verzamelingen) — het lexicale
/// signaal (§3.2 stap 2). Spiegelt de semantiek van Postgres' <c>pg_trgm
/// similarity()</c> (verzameling-Jaccard over trigrammen) zodat de gouden-set-gate
/// exact dezelfde beslissing meet als de productie-classifier. Bij fase-1-
/// cardinaliteit (tientallen mechanics/keywords) is dit de gezaghebbende,
/// gate-consistente scorer; de <c>pg_trgm</c>-extensie + GIN-index in de migratie
/// staan klaar als het gedocumenteerde schaal-pad wanneer de canonieke set groeit.</summary>
public static class Trigrams
{
    /// <summary>Trigram-verzameling van een genormaliseerde string, met de
    /// pg_trgm-conventie van twee leidende + één afsluitende spatie zodat woord-
    /// begin/eind meetellen. Lege input → lege verzameling.</summary>
    public static IReadOnlySet<string> Of(string normalized)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(normalized)) return set;
        var padded = "  " + normalized + " ";
        for (var i = 0; i + 3 <= padded.Length; i++)
            set.Add(padded.Substring(i, 3));
        return set;
    }

    /// <summary>Jaccard-similarity |A∩B| / |A∪B| over de trigram-verzamelingen van
    /// twee reeds-genormaliseerde labels. 0..1; twee lege strings → 0.</summary>
    public static double Similarity(string normalizedA, string normalizedB)
    {
        var a = Of(normalizedA);
        var b = Of(normalizedB);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}

/// <summary>Instelbare drempels voor de drietraps-signalen en de auto-merge-gate.
/// Als record zodat tests ze kunnen variëren; de defaults zijn op de gouden set
/// gekalibreerd (zie <c>EntityResolutionTests</c>).</summary>
public sealed record EntityResolutionThresholds(
    int BlockingPrefixLength = 4,
    double TrigramStrong = 0.45,
    double EmbeddingStrong = 0.85,
    int MinAutoMergeLabelLength = 4)
{
    public static readonly EntityResolutionThresholds Default = new();
}

/// <summary>De drie goedkoop→duur-signalen voor één kandidaatpaar (§3.2):
/// blocking (gedeeld genormaliseerd prefix), lexicaal (trigram) en embedding
/// (cosine over de Definition, <c>null</c> als een van beide entiteiten nog geen
/// embedding heeft).</summary>
public sealed record EntityMatchSignals(
    string NormalizedLabelA,
    string NormalizedLabelB,
    double TrigramSimilarity,
    double? EmbeddingCosine);

/// <summary>Uitkomst van de classifier: één van drie niveaus, plus welke signalen
/// vuurden en een leesbare reden. NIET de eindbeslissing om te schrijven — die
/// ligt bij de service, die daar de precisie-gate en de min-lengte-regel bovenop
/// legt.</summary>
public enum EntityMergeVerdict
{
    /// <summary>Onvoldoende signaal — geen kandidaatpaar.</summary>
    NoMatch,
    /// <summary>Twee van drie signalen — naar de reviewqueue.</summary>
    Review,
    /// <summary>Alle drie de signalen — auto-merge-KANDIDAAT (schrijft pas na de
    /// gate; standaard nog steeds review).</summary>
    AutoMergeCandidate,
}

public sealed record EntityMergeDecision(
    EntityMergeVerdict Verdict,
    bool Blocked,
    bool LexicalStrong,
    bool EmbeddingStrong,
    int SignalCount,
    string Reason);

/// <summary>De drietraps-classifier (§3.2): NOOIT auto-merge op alleen embedding.
/// Drie onafhankelijke signalen; alle drie → auto-merge-kandidaat, precies twee →
/// review, minder → geen match. Puur en deterministisch.</summary>
public static class EntityResolutionClassifier
{
    /// <summary>Deelt een genormaliseerd prefix (blocking)?</summary>
    public static bool Blocks(string normalizedA, string normalizedB, int prefixLength)
    {
        if (normalizedA.Length == 0 || normalizedB.Length == 0) return false;
        var a = Prefix(normalizedA, prefixLength);
        var b = Prefix(normalizedB, prefixLength);
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    /// <summary>Blocking-sleutel: de eerste <paramref name="prefixLength"/> tekens
    /// van het genormaliseerde label (of het hele label als het korter is). Twee
    /// entiteiten met dezelfde sleutel zitten in hetzelfde blok en worden een
    /// kandidaatpaar.</summary>
    public static string BlockingKey(string normalized, int prefixLength) =>
        Prefix(normalized, prefixLength);

    private static string Prefix(string s, int n) => s.Length <= n ? s : s[..n];

    public static EntityMergeDecision Classify(
        EntityMatchSignals signals, EntityResolutionThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(thresholds);

        var blocked = Blocks(signals.NormalizedLabelA, signals.NormalizedLabelB,
            thresholds.BlockingPrefixLength);
        var lexicalStrong = signals.TrigramSimilarity >= thresholds.TrigramStrong;
        var embeddingStrong = signals.EmbeddingCosine is double c && c >= thresholds.EmbeddingStrong;
        var count = (blocked ? 1 : 0) + (lexicalStrong ? 1 : 0) + (embeddingStrong ? 1 : 0);

        var verdict = count switch
        {
            3 => EntityMergeVerdict.AutoMergeCandidate,
            2 => EntityMergeVerdict.Review,
            _ => EntityMergeVerdict.NoMatch,
        };

        var reason =
            $"blocking={(blocked ? "ja" : "nee")}, " +
            $"trigram={signals.TrigramSimilarity:0.00}{(lexicalStrong ? "≥" : "<")}{thresholds.TrigramStrong:0.00}, " +
            $"embedding={(signals.EmbeddingCosine is double e ? e.ToString("0.00") : "n.v.t.")}" +
            $"{(embeddingStrong ? "≥" : "<")}{thresholds.EmbeddingStrong:0.00} → {count}/3 signalen";

        return new(verdict, blocked, lexicalStrong, embeddingStrong, count, reason);
    }
}

/// <summary>De auto-merge-gate (kritiek Risico 2b). Auto-merge staat standaard UIT
/// en mag pas schrijven ná een gemeten ER-gouden-set-precisie boven de drempel,
/// ÉN nooit voor labels korter dan de minimumlengte. Puur: de service levert de
/// gemeten precisie; dit beslist hard of een auto-merge-kandidaat daadwerkelijk
/// zelf geschreven mag worden of alsnog naar review moet.</summary>
public static class EntityResolutionGate
{
    /// <summary>Standaard-precisiedrempel: een auto-merge-fout (twee ongelijke
    /// keywords samengevoegd) is duur en moeilijk zichtbaar, dus streng.</summary>
    public const double DefaultPrecisionThreshold = 0.95;

    /// <summary>Is de auto-merge-gate open? Alleen als de gemeten merge-precisie de
    /// drempel haalt EN de gouden set niet leeg was (een lege meting bewijst
    /// niets).</summary>
    public static bool IsOpen(EntityResolutionEvalResult eval, double threshold = DefaultPrecisionThreshold)
    {
        ArgumentNullException.ThrowIfNull(eval);
        return eval.TotalPairs > 0 && eval.PredictedMerges > 0 && eval.Precision >= threshold;
    }

    /// <summary>Mag dit concrete paar auto-gemerged worden? Vereist een open gate
    /// EN beide labels ≥ minimumlengte (korte labels — 'Fury', 'Calm', 'Tank' —
    /// zijn te ambigu voor een onbewaakte merge en gaan altijd naar review).</summary>
    public static bool MayAutoMerge(
        bool gateOpen, string canonicalLabelA, string canonicalLabelB,
        EntityResolutionThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (!gateOpen) return false;
        var min = thresholds.MinAutoMergeLabelLength;
        return (canonicalLabelA?.Trim().Length ?? 0) >= min
            && (canonicalLabelB?.Trim().Length ?? 0) >= min;
    }
}

/// <summary>Eén gelabeld paar in de ER-gouden set (patroon van eval-scaffold
/// #235): twee surface-forms en of ze dezelfde canonieke entiteit horen te zijn.</summary>
public sealed record EntityResolutionGoldPair(string LabelA, string LabelB, bool ShouldMerge, string Note);

/// <summary>Resultaat van de gouden-set-meting: de verwarrings-tellingen en de
/// afgeleide merge-precisie/recall. Precisie is de gate-maat (§3.2): van alle
/// paren die de classifier zou mergen, welk deel hoorde te mergen.</summary>
public sealed record EntityResolutionEvalResult(
    int TotalPairs, int TruePositives, int FalsePositives,
    int TrueNegatives, int FalseNegatives)
{
    public int PredictedMerges => TruePositives + FalsePositives;
    public double Precision => PredictedMerges == 0 ? 0 : (double)TruePositives / PredictedMerges;
    public double Recall => (TruePositives + FalseNegatives) == 0
        ? 0 : (double)TruePositives / (TruePositives + FalseNegatives);
}

/// <summary>De ER-gouden set + de precisie-meting (§3.2, patroon #235). De set is
/// bewust label-gebaseerd en meet de <em>lexicale</em> merge-precisie (blocking +
/// trigram — de twee goedkope, deterministische signalen). Auto-merge vereist in
/// productie bovendien embedding-corroboratie (het derde signaal), dus de echte
/// auto-merge-precisie ligt ≥ deze meting: gaten op de lexicale precisie is een
/// conservatieve, veilige gate. Zolang de meting onder de drempel zit, blijft
/// auto-merge dicht en gaat elke merge naar review.</summary>
public static class EntityResolutionGoldSet
{
    /// <summary>De gelabelde paren. Merge-paren dekken de klassieke synoniem-
    /// valkuilen (spatie/koppelteken, morfologische varianten); niet-merge-paren
    /// dekken zowel duidelijk-verschillende keywords als het lastige geval van een
    /// gedeeld prefix dat tóch niet mag mergen (Assault/Assail) — dat laatste maakt
    /// de precisie-meting discriminatief i.p.v. triviaal.</summary>
    public static IReadOnlyList<EntityResolutionGoldPair> Pairs =>
    [
        // Moeten mergen (synoniem/variant → dezelfde canonieke entiteit).
        new("Deflect", "Deflecting", true, "morfologische variant"),
        new("Deathknell", "Death Knell", true, "spatie-variant"),
        new("Showdown Window", "showdown-window", true, "koppelteken + casing"),
        new("Recycle", "Recycling", true, "morfologische variant"),

        // Mogen NIET mergen (verschillende concepten).
        new("Deflect", "Assault", false, "ongerelateerde keywords"),
        new("Showdown Window", "Reaction Window", false, "verschillende timing-windows"),
        new("Tank", "Trample", false, "ongerelateerde keywords"),
        new("Accelerate", "Assault", false, "ongerelateerde keywords"),
        new("Assault", "Assail", false, "gedeeld prefix, tóch verschillend"),
    ];

    /// <summary>Meet de lexicale merge-precisie van de classifier op een gelabelde
    /// set. De predictie is "merge" wanneer de twee goedkope signalen samen vuren
    /// (blocking ∧ trigram≥drempel); dat is exact de deelverzameling die de
    /// auto-merge-poort als eerste twee eisen stelt.</summary>
    public static EntityResolutionEvalResult Evaluate(
        IReadOnlyList<EntityResolutionGoldPair> pairs, EntityResolutionThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(thresholds);
        int tp = 0, fp = 0, tn = 0, fn = 0;
        foreach (var pair in pairs)
        {
            var na = AliasNormalizer.Normalize(pair.LabelA);
            var nb = AliasNormalizer.Normalize(pair.LabelB);
            var blocked = EntityResolutionClassifier.Blocks(na, nb, thresholds.BlockingPrefixLength);
            var lexicalStrong = Trigrams.Similarity(na, nb) >= thresholds.TrigramStrong;
            var predictMerge = blocked && lexicalStrong;

            if (predictMerge && pair.ShouldMerge) tp++;
            else if (predictMerge && !pair.ShouldMerge) fp++;
            else if (!predictMerge && !pair.ShouldMerge) tn++;
            else fn++;
        }
        return new(pairs.Count, tp, fp, tn, fn);
    }

    /// <summary>Gemak: meet de ingebakken set met de gegeven (of default) drempels.</summary>
    public static EntityResolutionEvalResult EvaluateDefault(EntityResolutionThresholds? thresholds = null) =>
        Evaluate(Pairs, thresholds ?? EntityResolutionThresholds.Default);
}
