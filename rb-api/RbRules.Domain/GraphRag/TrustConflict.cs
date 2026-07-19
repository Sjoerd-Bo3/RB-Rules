namespace RbRules.Domain.GraphRag;

/// <summary>Fase 5 (#229, §6) — de idee-niveau onafhankelijkheids-sleutel die de
/// noisy-OR-corroboratie COMPLEET maakt t.o.v. fase 4. <see cref="Corroboration.NoisyOr"/>
/// dedupliceert al op een <see cref="CorroborationSource.ClusterKey"/>, maar liet in
/// het midden hoe die sleutel uit ruwe bron-metadata ontstaat; dat is precies waar de
/// echo-kamer schuilt. De "shared-origin-correlatie-discount op idee-niveau"
/// (beslissing #229): bronnen die dezelfde thread, auteur of site delen zijn NIET
/// onafhankelijk — tien reposts van één judge-tweet zijn één stem. De sleutel kiest de
/// grofste gedeelde herkomst: thread ≻ auteur ≻ site. Alleen als geen van drieën bekend
/// is, krijgt de bron een eigen (unieke) sleutel en telt hij als onafhankelijk.</summary>
public static class ProvenanceCluster
{
    /// <summary>Bereken de cluster-sleutel uit (thread, auteur, site/host). Grofste
    /// gedeelde herkomst wint: zit er een thread-id, dan clusteren alle posts in die
    /// thread samen; anders op auteur; anders op site-host. Alles genormaliseerd
    /// (lowercase, getrimd). Geen enkele bekend → <c>null</c> (de aanroeper geeft dan
    /// een unieke sleutel, bv. de bron-id, zodat de bron als onafhankelijk telt i.p.v.
    /// per ongeluk met alle metadata-loze bronnen in één cluster te vallen).</summary>
    public static string? KeyFor(string? thread = null, string? author = null, string? host = null)
    {
        var t = Norm(thread);
        if (t is not null) return "thread:" + t;
        var a = Norm(author);
        if (a is not null) return "author:" + a;
        var h = Norm(host);
        if (h is not null) return "host:" + h;
        return null;
    }

    private static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();
}

/// <summary>Eén RUWE corroboratie-bron met haar herkomst-metadata (fase 5, #229, §6),
/// vóór de cluster-afleiding. <see cref="ToSource"/> leidt de idee-niveau-sleutel af en
/// levert de <see cref="CorroborationSource"/> die <see cref="Corroboration.NoisyOr"/>
/// consumeert — zo loopt de pijplijn nu compleet van ruwe bron → cluster → noisy-OR.</summary>
/// <param name="SourceId">Stabiele bron-id — de fallback-sleutel als geen thread/auteur/
/// site bekend is (zodat metadata-loze bronnen elk als één onafhankelijke stem tellen,
/// niet samen als één).</param>
public readonly record struct RawCorroborationSource(
    string SourceId,
    string? Thread,
    string? Author,
    string? Host,
    double SourceStrength,
    double AuthorityWeight)
{
    public CorroborationSource ToSource() => new(
        ProvenanceCluster.KeyFor(Thread, Author, Host) ?? ("source:" + (SourceId ?? Guid.NewGuid().ToString())),
        SourceStrength, AuthorityWeight);

    /// <summary>Map een reeks ruwe bronnen naar cluster-gesleutelde corroboratie-bronnen
    /// (voor <see cref="Corroboration.NoisyOr"/>/<see cref="Corroboration.IndependentCount"/>).</summary>
    public static IReadOnlyList<CorroborationSource> ToSources(IEnumerable<RawCorroborationSource> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return [.. raw.Select(r => r.ToSource())];
    }
}

/// <summary>De context waarin een trust-conflict wordt beslecht (fase 5, #229, §6). De
/// resolutie is CONTEXT-AFHANKELIJK — niet één vaste tie-break-richting.</summary>
public enum TrustConflictContext
{
    /// <summary>Twee lezingen uit verschillende tiers spreken elkaar tegen → authority
    /// wint MET veto (de zwakkere wordt <c>contradicted_by_official</c>).</summary>
    CrossTier,

    /// <summary>Twee normatieve passages in dezelfde tier, temporeel → de recentste-
    /// gezaghebbende wint via expliciete SUPERSEDES; de oude blijft <c>superseded</c>.</summary>
    WithinTierTemporal,

    /// <summary>Twee kandidaat-ID's voor hetzelfde feit (detectie-botsing) → de vroegst-
    /// gedetecteerde canonieke ID wint; de latere variant wordt <c>ALIAS_OF</c>.</summary>
    DetectionConflict,
}

/// <summary>Wat er met de VERLIEZER gebeurt (fase 5, #229, §6). Nooit een hard-delete —
/// elke uitkomst laat de verliezer bestaan met een status + herstelpad (rode draad
/// #236): een misvatting blijft toonbaar in het misvattingen-kanaal, een gesuperseerde
/// passage blijft in de historie, een alias blijft omkeerbaar (unconsolidate).</summary>
public enum TrustDisposition
{
    /// <summary>Weggezet als door-officieel-weersproken (w→ε), omgeleid naar het
    /// misvattingen-kanaal — niet verwijderd.</summary>
    ContradictedByOfficial,

    /// <summary>Gesuperseerd door de recentere passage; blijft als historie bestaan.</summary>
    Superseded,

    /// <summary>Als alias aan de canonieke (vroegst-gedetecteerde) ID gehangen;
    /// omkeerbaar.</summary>
    AliasOf,
}

/// <summary>De expliciete trust-BESLISSING als first-class knoop (fase 5, #229, §6 /
/// rode draad #236: geen trust-beslissing zonder zichtbare memo). Legt de context, de
/// winnaar/verliezer en het verliezer-lot vast — de memo tegen flip-flop, en het spoor
/// waarmee een beheerder van beoordelaar naar arbiter schuift. Puur (Domain); de
/// persistentie als graaf-/tabel-knoop is een integratie-follow-up, net als bij de
/// fase-2 <see cref="Interaction"/>-projectie.</summary>
public sealed record TrustDecision(
    TrustConflictContext Context,
    string WinnerRef,
    string LoserRef,
    TrustDisposition LoserDisposition,
    string Memo);

/// <summary>Eén partij in een trust-conflict (fase 5, #229, §6).</summary>
/// <param name="Ref">BrainRef van de assertie/passage/kandidaat.</param>
/// <param name="Tier">De kennispiramide-laag (draagt de authority-as).</param>
/// <param name="EffectiveDate">Publicatie-/ingangsdatum (valid-time) — de temporele
/// as voor WithinTierTemporal; null = onbekend (sorteert als oudste).</param>
/// <param name="DetectedAt">Wanneer WÍJ de partij voor het eerst zagen (transaction-
/// time) — de as voor DetectionConflict (vroegste wint).</param>
public readonly record struct TrustParty(
    string Ref, KnowledgeTier Tier, DateTimeOffset? EffectiveDate, DateTimeOffset DetectedAt);

/// <summary>De context-afhankelijke conflict-resolutie (fase 5, #229, §6) — puur en
/// getest. Herbruikt BEWUST de #168/#206-precedentie (<see cref="Precedence"/>) en let
/// op de tie-break-RICHTING per context: WithinTierTemporal wil de RECENTSTE (de
/// #168-richting), DetectionConflict de VROEGSTE (omgekeerd — de #150/#175-les: de
/// stabiele, eerst-gedetecteerde canonieke ID wint). Cross-tier laat authority winnen
/// met een veto. Elke uitslag levert een <see cref="TrustDecision"/> — nooit onzichtbare
/// state, nooit een hard-delete.</summary>
public static class TrustConflictResolver
{
    public static TrustDecision Resolve(TrustParty a, TrustParty b, TrustConflictContext context) =>
        context switch
        {
            TrustConflictContext.CrossTier => ResolveCrossTier(a, b),
            TrustConflictContext.WithinTierTemporal => ResolveTemporal(a, b),
            TrustConflictContext.DetectionConflict => ResolveDetection(a, b),
            _ => ResolveCrossTier(a, b),
        };

    /// <summary>Cross-tier: authority wint. De hoogste tier (laagste enum-waarde) draagt
    /// het meeste gezag. Maar het VETO (verliezer → <c>contradicted_by_official</c>, w→ε,
    /// misvattingen-kanaal) is gereserveerd voor een écht OFFICIËLE winnaar — de routing
    /// keyt op "is er officiële dekking?" (beslissing #229), niet op kale tier-ordening.
    /// Wint een hoger-gezaghebbende maar NIET-officiële lezing (bv. een Primer van een
    /// Community-claim), dan is er geen officieel veto: de verliezer wordt <c>superseded</c>
    /// i.p.v. vals als door-officieel-weersproken gelabeld. Bij gelijk gezag valt de
    /// beslissing terug op de recentste (dan is het feitelijk geen cross-tier-conflict);
    /// een volledige gelijkstand breekt stabiel op de ref, nooit op invoervolgorde.</summary>
    private static TrustDecision ResolveCrossTier(TrustParty a, TrustParty b)
    {
        // Precedence: gelijk-tier → recentste; ongelijk-tier → authority. Positief = A wint.
        var cmp = Precedence.Compare((short)a.Tier, a.EffectiveDate, (short)b.Tier, b.EffectiveDate);
        var (winner, loser) = AWins(cmp, a, b) ? (a, b) : (b, a);

        // Beslissing #229: alleen officiële dekking legt een veto op. Een niet-officiële
        // winnaar (Primer/Community/Meta) mag de verliezer niet contradicted_by_official
        // noemen — anders labelt hij een lezing vals als officieel-weersproken.
        if (Authority.IsOfficial(winner.Tier))
            return new TrustDecision(
                TrustConflictContext.CrossTier, winner.Ref, loser.Ref,
                TrustDisposition.ContradictedByOfficial,
                $"Cross-tier: {TrustLabels.TierTag(winner.Tier)} ({winner.Ref}) wint met veto van " +
                $"{TrustLabels.TierTag(loser.Tier)} ({loser.Ref}); verliezer → contradicted_by_official " +
                "(w→ε, misvattingen-kanaal), blijft toonbaar.");

        return new TrustDecision(
            TrustConflictContext.CrossTier, winner.Ref, loser.Ref,
            TrustDisposition.Superseded,
            $"Cross-tier zonder officiële dekking: {TrustLabels.TierTag(winner.Tier)} ({winner.Ref}) " +
            $"heeft meer gezag dan {TrustLabels.TierTag(loser.Tier)} ({loser.Ref}) maar is niet " +
            "officieel — zonder officieel weerwoord; verliezer → superseded, blijft toonbaar.");
    }

    /// <summary>Within-tier temporeel: de recentste-gezaghebbende wint (#168-richting).
    /// Bij gelijke tier beslist de nieuwste <see cref="TrustParty.EffectiveDate"/>; de
    /// verliezer wordt <c>superseded</c> maar blijft als historie bestaan. Een volledige
    /// gelijkstand (gelijke tier, gelijke of beide-null datum) breekt stabiel op de ref —
    /// nooit op invoervolgorde, want een <see cref="TrustDecision"/> is een first-class
    /// knoop en mag niet per run van winnaar/richting flippen.</summary>
    private static TrustDecision ResolveTemporal(TrustParty a, TrustParty b)
    {
        var cmp = Precedence.Compare((short)a.Tier, a.EffectiveDate, (short)b.Tier, b.EffectiveDate);
        var (winner, loser) = AWins(cmp, a, b) ? (a, b) : (b, a);
        return new TrustDecision(
            TrustConflictContext.WithinTierTemporal, winner.Ref, loser.Ref,
            TrustDisposition.Superseded,
            $"Within-tier temporeel: {winner.Ref} (recentste-gezaghebbende) SUPERSEDES {loser.Ref}; " +
            "de oude passage blijft superseded in de historie.");
    }

    /// <summary>Wint partij A van B gegeven een <see cref="Precedence.Compare"/>-uitkomst?
    /// Positief → A; negatief → B; een echte gelijkstand (0) breekt stabiel op de ref
    /// (ordinaal, laagste wint) zodat de winnaar niet van invoervolgorde afhangt — dezelfde
    /// stabilisatie als <see cref="ResolveDetection"/>.</summary>
    private static bool AWins(int cmp, TrustParty a, TrustParty b) =>
        cmp > 0 || (cmp == 0 && string.CompareOrdinal(a.Ref, b.Ref) <= 0);

    /// <summary>Detectie-botsing: de VROEGST-gedetecteerde canonieke ID wint (omgekeerde
    /// tie-break t.o.v. temporeel — de #150/#175-les: stabiele canonieke ID's, latere
    /// variant wordt ALIAS_OF). Gelijke detectietijd → stabiele val op de ref-ordening
    /// zodat de uitkomst niet van invoervolgorde afhangt.</summary>
    private static TrustDecision ResolveDetection(TrustParty a, TrustParty b)
    {
        var earliestA = a.DetectedAt < b.DetectedAt
            || (a.DetectedAt == b.DetectedAt && string.CompareOrdinal(a.Ref, b.Ref) <= 0);
        var (winner, loser) = earliestA ? (a, b) : (b, a);
        return new TrustDecision(
            TrustConflictContext.DetectionConflict, winner.Ref, loser.Ref,
            TrustDisposition.AliasOf,
            $"Detectie-botsing: {winner.Ref} (vroegst gedetecteerd) blijft canoniek; " +
            $"{loser.Ref} wordt ALIAS_OF (omkeerbaar via unconsolidate).");
    }
}
