using System.Globalization;

namespace RbRules.Domain.GraphRag;

/// <summary>De kennispiramide-laag van een opgehaald feit (fase 4, #228 — §6/§9
/// van de brein-spec, docs/KNOWLEDGE.md). De volgorde is de gezags-orde:
/// <see cref="Official"/> wint altijd, <see cref="Meta"/> vervaagt het snelst.
/// Bewust één enum zodat retrieval, trust-gating, bundeling én de AnswerTrace
/// dezelfde tier-taal spreken.</summary>
public enum KnowledgeTier
{
    /// <summary>Officiële regels, kaartgegevens, Tournament Rules — authority 1.00.</summary>
    Official,
    /// <summary>Geverifieerde ruling/erratum — authority 0.85.</summary>
    VerifiedRuling,
    /// <summary>Gesynthetiseerde primer / sectie-dossier — authority 0.65.</summary>
    Primer,
    /// <summary>Community-lezing (claim) — authority 0.45; NOOIT een officiële bron.</summary>
    Community,
    /// <summary>Meta/tactiek — authority 0.25; vervalt agressief.</summary>
    Meta,
}

/// <summary>Statische authority-as (§0/§6, beslissing #229): de gezags-multiplier
/// per tier. Authority is een <em>labeler/tie-breaker</em>, geen multiplicatieve
/// annihilator (de gating in <see cref="TrustGate"/> zorgt daarvoor) — maar als
/// as van de trust-vector draagt hij wel het intrinsieke gezag van de laag.</summary>
public static class Authority
{
    public const double Official = 1.00;
    public const double VerifiedRuling = 0.85;
    public const double Primer = 0.65;
    public const double Community = 0.45;
    public const double Meta = 0.25;

    public static double Of(KnowledgeTier tier) => tier switch
    {
        KnowledgeTier.Official => Official,
        KnowledgeTier.VerifiedRuling => VerifiedRuling,
        KnowledgeTier.Primer => Primer,
        KnowledgeTier.Community => Community,
        KnowledgeTier.Meta => Meta,
        _ => Community,
    };

    /// <summary>Draagt deze tier officieel gezag (Official of geverifieerde ruling)?
    /// De poort van <see cref="TrustGate"/>: "is er officiële dekking?".</summary>
    public static bool IsOfficial(KnowledgeTier tier) =>
        tier is KnowledgeTier.Official or KnowledgeTier.VerifiedRuling;
}

/// <summary>Verificatie-as (§6): hoe hard is dit feit los van gezag gecheckt.</summary>
public static class Verification
{
    public const double Unverified = 0.50;
    public const double LexicallySupported = 0.80;
    public const double ConsensusVerified = 0.95;
    public const double HumanApproved = 1.00;
}

/// <summary>Eén corroboratie-bron vóór de noisy-OR (§6). <see cref="ClusterKey"/> is
/// de onafhankelijkheids-sleutel (auteur/site/thread) waarop wordt gededupliceerd —
/// dit is de "shared-origin-correlatie-discount op idee-niveau" tegen de echo-kamer
/// (beslissing #229): drie posts uit dezelfde Discord-thread tellen als één stem.
/// <see cref="SourceStrength"/> = hoe stellig de bron de claim steunt (0..1),
/// <see cref="AuthorityWeight"/> = het gezag van die bron (0..1).</summary>
public readonly record struct CorroborationSource(
    string ClusterKey, double SourceStrength, double AuthorityWeight)
{
    public double Product => Math.Clamp(SourceStrength, 0, 1) * Math.Clamp(AuthorityWeight, 0, 1);
}

/// <summary>De corroboratie-as (§6): noisy-OR ná onafhankelijkheids-dedup — NOOIT
/// tellen. Bronnen worden op <see cref="CorroborationSource.ClusterKey"/> gededupt
/// (per cluster telt de sterkste stem), daarna <c>1 − Πᵢ(1 − sᵢ·tᵢ)</c>. Zo laat
/// tien keer dezelfde herkauwde mening de corroboratie niet oplopen, terwijl drie
/// onafhankelijke bronnen hem wél laten stijgen.</summary>
public static class Corroboration
{
    /// <summary>Geen bronnen → een neutrale, niet-verhogende corroboratie van 1.0
    /// (de as mag een feit dat verder geen corroboratie-signaal heeft niet
    /// wegdrukken; verificatie/authority dragen dan de weging).</summary>
    public const double None = 1.0;

    public static double NoisyOr(IEnumerable<CorroborationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        // Onafhankelijkheids-dedup: per cluster de sterkste stem (idee-niveau).
        var perCluster = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
        {
            var key = string.IsNullOrWhiteSpace(s.ClusterKey) ? Guid.NewGuid().ToString() : s.ClusterKey.Trim();
            var p = s.Product;
            if (!perCluster.TryGetValue(key, out var existing) || p > existing)
                perCluster[key] = p;
        }

        if (perCluster.Count == 0) return None;

        var complement = 1.0;
        foreach (var p in perCluster.Values)
            complement *= (1.0 - Math.Clamp(p, 0, 1));
        return 1.0 - complement;
    }

    /// <summary>Aantal onafhankelijke clusters (de "N bronnen" in het badge-label,
    /// ná dedup) — nooit het rauwe bron-aantal.</summary>
    public static int IndependentCount(IEnumerable<CorroborationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
            set.Add(string.IsNullOrWhiteSpace(s.ClusterKey) ? Guid.NewGuid().ToString() : s.ClusterKey.Trim());
        return set.Count;
    }
}

/// <summary>De recency-as (§6): <c>exp(−λ_tier·age)</c> met een λ per tier. Officieel
/// vervalt vrijwel niet (verval alleen via SUPERSEDES/errata), meta agressief
/// (weken). De λ's zijn per DAG uitgedrukt zodat de leeftijd in dagen ingaat.</summary>
public static class Recency
{
    // λ per dag, gekozen op de half-life-intuïtie uit §6:
    //  official  ≈ 0            (nooit spontaan verval)
    //  ruling    half-life ~3j  → λ = ln2/1095
    //  primer    half-life ~1j  → λ = ln2/365
    //  community half-life ~7mnd→ λ = ln2/210
    //  meta      half-life ~3wk → λ = ln2/21
    private const double Ln2 = 0.6931471805599453;

    public static double Lambda(KnowledgeTier tier) => tier switch
    {
        KnowledgeTier.Official => 0.0,
        KnowledgeTier.VerifiedRuling => Ln2 / 1095.0,
        KnowledgeTier.Primer => Ln2 / 365.0,
        KnowledgeTier.Community => Ln2 / 210.0,
        KnowledgeTier.Meta => Ln2 / 21.0,
        _ => Ln2 / 210.0,
    };

    /// <summary>Verval-factor voor een feit van tier <paramref name="tier"/> dat
    /// <paramref name="ageDays"/> dagen oud is. Negatieve leeftijd (klok-scheefte)
    /// klemt op 0 → factor 1.0; officieel geeft altijd 1.0.</summary>
    public static double Decay(KnowledgeTier tier, double ageDays)
    {
        var age = Math.Max(0, ageDays);
        var lambda = Lambda(tier);
        return lambda <= 0 ? 1.0 : Math.Exp(-lambda * age);
    }

    public static double Decay(KnowledgeTier tier, DateTimeOffset assertedAt, DateTimeOffset now) =>
        Decay(tier, (now - assertedAt).TotalDays);
}

/// <summary>De canonieke trust-VECTOR op een <see cref="Assertion"/>/opgehaald feit
/// (§0/§6, beslissing #229). Vier orthogonale assen; elke as kan een veto uitoefenen
/// via het PRODUCT. De scalar <see cref="Weight"/> is de afgeleide retrieval-
/// multiplier — nooit andersom. Bewust een <c>readonly record struct</c>: puur,
/// waardegelijk, en goedkoop in de retrieval-hot-path.</summary>
public readonly record struct TrustVector(
    double AuthorityAxis, double VerificationAxis, double CorroborationAxis, double RecencyAxis)
{
    /// <summary>De afgeleide scalar-multiplier: het product van de vier assen,
    /// geklemd op 0..1.</summary>
    public double Weight => Math.Clamp(AuthorityAxis, 0, 1)
        * Math.Clamp(VerificationAxis, 0, 1)
        * Math.Clamp(CorroborationAxis, 0, 1)
        * Math.Clamp(RecencyAxis, 0, 1);

    /// <summary>Bouw de vector uit tier + verificatie + corroboratie-bronnen +
    /// leeftijd. De authority- en recency-as volgen deterministisch uit de tier;
    /// verificatie en corroboratie komen van het feit zelf.</summary>
    public static TrustVector For(
        KnowledgeTier tier,
        double verification,
        IEnumerable<CorroborationSource>? corroboration = null,
        double ageDays = 0) =>
        new(Authority.Of(tier),
            verification,
            corroboration is null ? Corroboration.None : Corroboration.NoisyOr(corroboration),
            Recency.Decay(tier, ageDays));

    /// <summary>Een officieel-tier feit met de neutrale assen — voor officiële
    /// secties die geen corroboratie/verval-signaal dragen (regels vervallen niet
    /// spontaan).</summary>
    public static TrustVector OfficialDefault =>
        new(Authority.Official, Verification.HumanApproved, Corroboration.None, 1.0);
}

/// <summary>Machine-leesbare trust-labels voor de context-bundel (§4): het model
/// ziet expliciet <c>[OFFICIEEL]</c> of <c>[COMMUNITY trust=0.30 corrob=2/5]</c> —
/// nooit een impliciete rangorde. De labels zijn invariant (punt als
/// decimaalteken) zodat prompt-diffs stabiel zijn.</summary>
public static class TrustLabels
{
    public static string TierTag(KnowledgeTier tier) => tier switch
    {
        KnowledgeTier.Official => "OFFICIEEL",
        KnowledgeTier.VerifiedRuling => "GEVERIFIEERDE RULING",
        KnowledgeTier.Primer => "PRIMER",
        KnowledgeTier.Community => "COMMUNITY",
        KnowledgeTier.Meta => "META",
        _ => "COMMUNITY",
    };

    /// <summary>Het volledige label. Officiële tiers dragen alleen hun tag (gezag
    /// spreekt voor zich); niet-officiële tiers dragen trust + corroboratie zodat
    /// de gebruiker de weging kan wegen.</summary>
    public static string For(KnowledgeTier tier, TrustVector trust, int independentSources, int totalSources)
    {
        var tag = TierTag(tier);
        if (Authority.IsOfficial(tier))
            return $"[{tag}]";
        var w = trust.Weight.ToString("0.00", CultureInfo.InvariantCulture);
        return $"[{tag} trust={w} corrob={independentSources}/{Math.Max(independentSources, totalSources)}]";
    }
}
