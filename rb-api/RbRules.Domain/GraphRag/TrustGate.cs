namespace RbRules.Domain.GraphRag;

/// <summary>Uitkomst van de trust-gating (§4/§6, beslissing #229): welke laag het
/// antwoord PRIMAIR draagt, en of niet-officiële steun een badge nodig heeft.</summary>
public enum PrimaryChannel
{
    /// <summary>Er is officiële dekking; officiële feiten dragen het antwoord,
    /// community mag alleen kleuren/labelen (tie-breaker), altijd gebadged.</summary>
    Official,
    /// <summary>Geen officiële dekking; een goed-onderbouwde community-claim mag
    /// primair zijn — MET badge (de eerlijke "geen officiële bron"-melding).</summary>
    CommunityBadged,
    /// <summary>Geen officiële dekking én geen voldoende-onderbouwde community-claim:
    /// eerlijk "geen bekende dekking" (→ KnowledgeGaps), nooit hallucineren.</summary>
    None,
}

/// <summary>Eén kandidaat-feit voor de gating: alleen de assen die de poort nodig
/// heeft. Bewust losgekoppeld van de volle bundle-item zodat de gating puur en
/// goedkoop test.</summary>
public readonly record struct TrustCandidate(KnowledgeTier Tier, double TrustWeight)
{
    public bool IsOfficial => Authority.IsOfficial(Tier);
}

/// <summary>De beslissing van <see cref="TrustGate"/>: het primaire kanaal, of
/// community-items een badge moeten dragen, en een korte memo (inzicht #236 — een
/// beslissing levert nooit onzichtbare state).</summary>
public sealed record TrustGateDecision(
    PrimaryChannel Primary, bool BadgeCommunity, string Memo);

/// <summary>GEGATE trust-weging (beslissing #229, de rode draad van fase 4). NIET
/// multiplicatief-annihilerend: authority nult een community-claim niet weg, maar
/// bepaalt de ROUTE. De vraag is "is er officiële dekking van déze vraag?":
/// <list type="bullet">
/// <item>Ja → officieel is primair; community blijft zichtbaar maar gebadged en
///   alleen als tie-breaker/labeler (het mag het oordeel kleuren, nooit dragen).</item>
/// <item>Nee, maar een voldoende-onderbouwde community-claim → die mág primair zijn,
///   MET badge (de eerlijke "geen officiële bron"-melding).</item>
/// <item>Nee en niets onderbouwd genoeg → None: eerlijk geen dekking.</item>
/// </list>
/// De echo-kamer-discount zit al in <see cref="Corroboration.NoisyOr"/> (dedup op
/// idee-niveau); deze poort werkt op de reeds-gewogen trust-scalars.</summary>
public static class TrustGate
{
    /// <summary>Drempel waaronder een community-claim niet stevig genoeg is om — bij
    /// gebrek aan officiële dekking — het antwoord primair te dragen. Ligt bewust
    /// niet te laag: een losse, zwak-onderbouwde mening (w≈0.20) mag geen ruling
    /// dragen, een breed-gecorroboreerde consensus (w≈0.30+) wel, mét badge.</summary>
    public const double CommunityPrimaryFloor = 0.28;

    public static TrustGateDecision Decide(
        IEnumerable<TrustCandidate> candidates,
        double communityFloor = CommunityPrimaryFloor)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var list = candidates as IReadOnlyList<TrustCandidate> ?? [.. candidates];

        var hasOfficial = false;
        var hasCommunity = false;
        var bestCommunity = 0.0;
        foreach (var c in list)
        {
            if (c.IsOfficial) hasOfficial = true;
            else { hasCommunity = true; bestCommunity = Math.Max(bestCommunity, c.TrustWeight); }
        }

        if (hasOfficial)
            return new(PrimaryChannel.Official, BadgeCommunity: hasCommunity,
                "Officiële dekking aanwezig — officieel primair; community gebadged als tie-breaker.");

        if (hasCommunity && bestCommunity >= communityFloor)
            return new(PrimaryChannel.CommunityBadged, BadgeCommunity: true,
                $"Geen officiële dekking; best-onderbouwde community-claim (trust {bestCommunity:0.00}) " +
                "primair MET badge.");

        if (hasCommunity)
            return new(PrimaryChannel.None, BadgeCommunity: true,
                $"Geen officiële dekking; sterkste community-claim (trust {bestCommunity:0.00}) " +
                $"onder de drempel {communityFloor:0.00} — eerlijk geen dekking.");

        return new(PrimaryChannel.None, BadgeCommunity: false,
            "Geen dekking gevonden — eerlijk 'geen bekende interactie' i.p.v. hallucineren.");
    }

    /// <summary>Tie-break-tik (§4: "trust_weight breekt elke tie richting officieel"):
    /// bij nagenoeg gelijke relevantie wint de hoogste tier, daarna de hoogste
    /// trust-scalar. Authority is hier labeler/tie-breaker — precies waarvoor #229
    /// hem reserveert, niet als annihilator. Positief als A voorrang heeft op B.</summary>
    public static int CompareForTieBreak(
        KnowledgeTier tierA, double trustA, KnowledgeTier tierB, double trustB)
    {
        // Lagere enum-waarde = hoger gezag (Official=0).
        if (tierA != tierB) return ((int)tierB).CompareTo((int)tierA);
        return trustA.CompareTo(trustB);
    }
}
