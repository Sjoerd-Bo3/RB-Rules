using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record ClaimMineResult(
    int Documents, int NewClaims, int Corroborated, int Rejected,
    int Conflicts, int Rechecked, int Failed, string Message);

/// <summary>Kennislaag 2 (#50): destilleert claims uit community-documenten in
/// het bestaande bronnenregister (trust ≥ 3), dedupet ze via genormaliseerde
/// tekst + embedding-clustering + LLM-toets, telt corroboratie over
/// onafhankelijke bronnen en toetst nieuwe claims aan de officiële regels
/// (officieel wint altijd: tegenspraak ⇒ automatisch rejected met verwijzing).
/// Alles best-effort per stap; cheap-model, gecapt per run (nachtelijke job).
/// Elke faalstap is herleidbaar in run_log (#93) en claims_mined_at wordt pas
/// gezet nadat een document volledig en zonder mislukte claims is verwerkt
/// (#92) — een afgebroken of falende run probeert het vanzelf opnieuw.</summary>
public class ClaimMiningService(RbRulesDbContext db, RbAiClient ai, EmbeddingService embeddings)
{
    /// <summary>Lange gidsen gaan in stukken naar de extractor; de cap houdt
    /// de kosten per document begrensd (rest volgt in een latere run zodra
    /// het document wijzigt, of via een geforceerde run).</summary>
    private const int SegmentChars = 12000;
    private const int MaxSegmentsPerDocument = 4;

    /// <summary>Embedding-afstand waarbinnen een bestaande claim kandidaat is
    /// voor de "zelfde bewering?"-toets. Ruim genoeg om parafrases te vangen;
    /// de LLM velt het eindoordeel, dus een te ruime poort kost hooguit een
    /// cheap-call.</summary>
    private const double JudgeGateDistance = 0.35;
    private const int MaxJudgeCandidates = 3;
    private const int OfficialChunks = 3;
    /// <summary>Her-toets per run voor claims die eerder "unchecked" bleven
    /// (rb-ai/Ollama-uitval of nog geen regelindex) — klein gehouden, het is
    /// bijvangst naast de verse oogst.</summary>
    private const int MaxRechecksPerRun = 15;
    /// <summary>Afkaplengte voor de rauwe LLM-respons in run_log-diagnose
    /// (patroon van de scout-fix, PR #87).</summary>
    private const int ResponseSnippetLength = 400;

    public async Task<ClaimMineResult> RunAsync(
        bool force = false, int maxClaims = 60,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        maxClaims = Math.Clamp(maxClaims, 1, 300);
        var sources = await db.Sources
            .Where(s => s.Enabled && s.TrustTier >= 3)
            .OrderByDescending(s => s.Rank)
            .ToListAsync(ct);
        var officialSourceIds = await db.Sources
            .Where(s => s.TrustTier == 1)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var docs = 0;
        var newClaims = 0;
        var corroborated = 0;
        var rejected = 0;
        var conflicts = 0;
        var failed = 0;
        var processed = 0;
        var budgetHit = false;

        foreach (var src in sources)
        {
            if (budgetHit) break;
            var doc = await db.Documents
                .Where(d => d.SourceId == src.Id)
                .OrderByDescending(d => d.RetrievedAt)
                .FirstOrDefaultAsync(ct);
            if (doc is null || (doc.ClaimsMinedAt is not null && !force)) continue;

            docs++;
            var extractionComplete = true;
            var srcNew = 0;
            var srcCorroborated = 0;
            // Reden per mislukte claim: herleidbaar in run_log in plaats van
            // één anonieme "mislukt"-teller (#93 — op productie faalden 60/60
            // claims zonder één zichtbare foutregel).
            var claimFailures = new List<string>();

            var segments = Segment(doc.Content);
            for (var si = 0; si < segments.Count; si++)
            {
                if (budgetHit) { extractionComplete = false; break; }
                progress?.Invoke(
                    $"{src.Id}: deel {si + 1}/{segments.Count} extraheren ({newClaims} nieuw, {corroborated} gecorroboreerd)");

                var raw = await AskSafeAsync(
                    ClaimMiner.BuildExtractionPrompt(src.Name, segments[si]),
                    ClaimMiner.ExtractionSystemPrompt, ct);
                if (raw is null)
                {
                    // rb-ai weg: dit deel blijft staan voor een volgende run
                    // (document blijft ongemarkeerd), de rest gaat door.
                    extractionComplete = false;
                    failed++;
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = "claims", Ref = src.Id, Status = "error",
                        Detail = $"deel {si + 1}/{segments.Count}: rb-ai niet beschikbaar — extractie overgeslagen",
                    });
                    continue;
                }
                var extracted = ClaimMiner.ParseClaims(raw);
                if (extracted is null)
                {
                    // Onzin-output: reden + afgekapte respons in run_log
                    // (scout-patroon, PR #87), zodat de beheerder ziet wát het
                    // model werkelijk antwoordde.
                    extractionComplete = false;
                    failed++;
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = "claims", Ref = src.Id, Status = "error",
                        Detail = $"deel {si + 1}/{segments.Count}: LLM-antwoord onbruikbaar — geen parseerbare claims. "
                                 + $"Respons (afgekapt): {LlmJson.Snippet(raw, ResponseSnippetLength)}",
                    });
                    continue;
                }

                foreach (var ec in extracted)
                {
                    if (processed >= maxClaims)
                    {
                        // Kostencap (issue: cap per nacht). Dedupe maakt een
                        // herhaalde run idempotent, dus de rest volgt vanzelf.
                        budgetHit = true;
                        extractionComplete = false;
                        break;
                    }
                    processed++;
                    var (outcome, failure) = await ProcessClaimAsync(src, ec, officialSourceIds, ct);
                    switch (outcome)
                    {
                        case ClaimOutcome.New: newClaims++; srcNew++; break;
                        case ClaimOutcome.Corroborated: corroborated++; srcCorroborated++; break;
                        case ClaimOutcome.Rejected: rejected++; srcNew++; break;
                        case ClaimOutcome.Conflict: conflicts++; srcNew++; break;
                        case ClaimOutcome.Failed:
                            failed++;
                            claimFailures.Add(failure ?? "onbekende fout");
                            break;
                        case ClaimOutcome.Seen: break; // zelfde bron, al bekend
                    }
                }
            }

            // Gelijke redenen gegroepeerd tot één regel: Ollama-uitval raakt
            // doorgaans álle claims van een document met dezelfde fout.
            foreach (var g in claimFailures.GroupBy(r => r))
            {
                db.RunLogs.Add(new RunLog
                {
                    Kind = "claims", Ref = src.Id, Status = "error",
                    Detail = $"{g.Count()} claim(s) niet verwerkt: {g.Key}",
                });
            }

            // #92: pas markeren wanneer extractie én verwerking voor dit
            // document volledig geslaagd zijn (0 claims vinden is ook een
            // geldig resultaat). Een mislukte, afgekapte of op de cap
            // gestrande run komt zo vanzelf opnieuw aan de beurt.
            var documentDone = extractionComplete && claimFailures.Count == 0;
            if (documentDone)
            {
                doc.ClaimsMinedAt = DateTimeOffset.UtcNow;
            }
            db.RunLogs.Add(new RunLog
            {
                Kind = "claims", Ref = src.Id,
                Status = documentDone ? "ok" : "info",
                Detail = $"{srcNew} nieuwe claims, {srcCorroborated} gecorroboreerd"
                         + (documentDone ? "" : " (deels — document blijft staan voor een volgende run)"),
            });
            await db.SaveChangesAsync(ct);
        }

        // Her-toets tegen officieel voor claims die eerder unchecked bleven.
        var (rechecked, weerlegd) = budgetHit
            ? (0, 0)
            : await RecheckOfficialAsync(officialSourceIds, progress, ct);
        rejected += weerlegd;

        var message =
            $"{docs} documenten verwerkt: {newClaims} nieuwe claims, {corroborated} gecorroboreerd, "
            + $"{rejected} verworpen (officieel tegengesproken), {conflicts} conflicten, "
            + $"{rechecked} hergetoetst, {failed} mislukt"
            + (failed > 0 ? " (redenen in run_log)" : "")
            + (budgetHit ? $" — cap van {maxClaims} claims bereikt, rest volgt bij de volgende run" : "");
        return new(docs, newClaims, corroborated, rejected, conflicts, rechecked, failed, message);
    }

    private enum ClaimOutcome { New, Corroborated, Seen, Rejected, Conflict, Failed }

    /// <summary>AskAsync met het scout-timeoutpatroon: een HttpClient-timeout
    /// (niet de aanroeper die annuleert) telt als uitval van één stap, niet
    /// als crash van de hele nachtelijke oogst.</summary>
    private async Task<string?> AskSafeAsync(string prompt, string system, CancellationToken ct)
    {
        try
        {
            return await ai.AskAsync(prompt, system, ct: ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<(ClaimOutcome Outcome, string? Failure)> ProcessClaimAsync(
        Source src, ExtractedClaim ec, IReadOnlyList<string> officialSourceIds,
        CancellationToken ct)
    {
        // 1. Idempotente sneltoets: zelfde genormaliseerde bewering binnen
        // hetzelfde topic — geen LLM/Ollama nodig (her-runs en her-extracties
        // van hetzelfde document dupliceren zo nooit).
        var topicRefLower = ec.TopicRef.ToLowerInvariant();
        var topicClaims = await db.Claims
            .Where(c => c.TopicType == ec.TopicType && c.TopicRef.ToLower() == topicRefLower)
            .ToListAsync(ct);
        var norm = ClaimMiner.NormalizeStatement(ec.Statement);
        var exact = topicClaims.FirstOrDefault(
            c => ClaimMiner.NormalizeStatement(c.Statement) == norm);
        if (exact is not null)
            return (await CorroborateAsync(exact, src, ec.Quote, ct), null);

        // 2. Embedding voor clustering (en straks retrieval, #51). Zonder
        // Ollama geen betrouwbare dedupe — deze claim wacht op een latere run.
        // De reden gaat mee naar run_log: op productie faalden 60/60 claims
        // precies hier, zonder één zichtbare foutregel (#93).
        Vector vec;
        try
        {
            vec = await embeddings.EmbedOneAsync($"{ec.TopicRef}\n{ec.Statement}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (ClaimOutcome.Failed, $"embedding mislukt (Ollama): {ex.Message}");
        }

        // 3. Dichtstbijzijnde bestaande claims → LLM-oordeel "zelfde bewering?".
        var near = await db.Claims.AsNoTracking()
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(vec))
            .Select(c => new
            {
                c.Id, c.Statement,
                Distance = c.Embedding!.CosineDistance(vec),
            })
            .Take(MaxJudgeCandidates)
            .ToListAsync(ct);
        var candidates = near.Where(c => c.Distance <= JudgeGateDistance).ToList();

        long? contradictsClaimId = null;
        if (candidates.Count > 0)
        {
            var raw = await AskSafeAsync(
                ClaimJudge.BuildPrompt(ec.Statement, [.. candidates.Select(c => c.Statement)]),
                ClaimJudge.SystemPrompt, ct);
            var judgement = raw is null ? null : ClaimJudge.Parse(raw, candidates.Count);
            if (judgement is null)
            {
                // Onbruikbare dedupe-toets: als nieuw behandelen (veilige
                // kant), maar wel herleidbaar — bij parse-uitval mét de
                // afgekapte respons (#93).
                db.RunLogs.Add(new RunLog
                {
                    Kind = "claims", Ref = src.Id,
                    Status = raw is null ? "info" : "error",
                    Detail = "dedupe-toets gaf geen bruikbaar oordeel — claim als nieuw behandeld"
                             + (raw is null
                                ? " (rb-ai niet beschikbaar)"
                                : $". Respons (afgekapt): {LlmJson.Snippet(raw, ResponseSnippetLength)}"),
                });
            }
            if (judgement is { Verdict: "same", Match: not null })
            {
                var match = await db.Claims.FindAsync([candidates[judgement.Match.Value - 1].Id], ct);
                if (match is not null)
                    return (await CorroborateAsync(match, src, ec.Quote, ct), null);
            }
            if (judgement is { Verdict: "contradicts", Match: not null })
                contradictsClaimId = candidates[judgement.Match.Value - 1].Id;
            // "different": als nieuwe claim behandelen.
        }

        // 4. Toets tegen de officiële regels — officieel wint altijd.
        var (officialStatus, statusReason, officialDegraded) = await CheckOfficialAsync(
            ec.Statement, vec, officialSourceIds, ct);
        if (officialDegraded is not null)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "claims", Ref = src.Id, Status = "info",
                Detail = $"toets tegen officiële regels bleef uit — claim blijft 'unchecked': {officialDegraded}",
            });
        }
        var status = officialStatus == "contradicted" ? "rejected" : "unreviewed";

        var claim = new Claim
        {
            TopicType = ec.TopicType,
            TopicRef = ec.TopicRef,
            Statement = ec.Statement,
            Corroboration = 1,
            TrustScore = ClaimScoring.TrustScore([src.TrustTier]),
            Status = status,
            StatusReason = statusReason,
            OfficialStatus = officialStatus,
            Embedding = vec,
            EmbeddingModel = EmbeddingConfig.Model,
        };
        db.Claims.Add(claim);
        await db.SaveChangesAsync(ct);
        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = claim.Id, SourceId = src.Id, Url = src.Url, QuoteExcerpt = ec.Quote,
        });

        // 5. Claim↔claim-tegenspraak → bestaand Conflict-model + reviewqueue
        // (beide claims blijven zichtbaar; de beheerder beslist).
        if (contradictsClaimId is not null)
        {
            var other = await db.Claims.AsNoTracking()
                .Where(c => c.Id == contradictsClaimId.Value)
                .Select(c => new { c.Statement })
                .FirstOrDefaultAsync(ct);
            var otherSource = await db.ClaimSources.AsNoTracking()
                .Where(cs => cs.ClaimId == contradictsClaimId.Value)
                .OrderBy(cs => cs.SeenAt)
                .Select(cs => cs.SourceId)
                .FirstOrDefaultAsync(ct);
            db.Conflicts.Add(new Conflict
            {
                Topic = $"claim:{ec.TopicType}:{ec.TopicRef}",
                Kind = "contradiction",
                SourceAId = otherSource,
                SourceBId = src.Id,
                Explanation =
                    $"Bestaande claim: \"{other?.Statement}\" ↔ nieuwe claim: \"{ec.Statement}\"",
            });
        }
        await db.SaveChangesAsync(ct);

        var outcome = contradictsClaimId is not null ? ClaimOutcome.Conflict
            : status == "rejected" ? ClaimOutcome.Rejected
            : ClaimOutcome.New;
        return (outcome, null);
    }

    /// <summary>Corroboratie: een nieuwe onafhankelijke bron versterkt de
    /// claim (telling + trust-score omhoog); dezelfde bron nogmaals telt niet
    /// ("één bron = ongecorroboreerd") maar ververst wel last_seen.</summary>
    private async Task<ClaimOutcome> CorroborateAsync(
        Claim claim, Source src, string? quote, CancellationToken ct)
    {
        claim.LastSeen = DateTimeOffset.UtcNow;
        var alreadyAttached = await db.ClaimSources
            .AnyAsync(cs => cs.ClaimId == claim.Id && cs.SourceId == src.Id, ct);
        if (alreadyAttached)
        {
            await db.SaveChangesAsync(ct);
            return ClaimOutcome.Seen;
        }

        db.ClaimSources.Add(new ClaimSource
        {
            ClaimId = claim.Id, SourceId = src.Id, Url = src.Url, QuoteExcerpt = quote,
        });
        await db.SaveChangesAsync(ct);

        var tiers = await db.ClaimSources
            .Where(cs => cs.ClaimId == claim.Id)
            .Join(db.Sources, cs => cs.SourceId, s => s.Id,
                (cs, s) => new { cs.SourceId, s.TrustTier })
            .ToListAsync(ct);
        claim.Corroboration = ClaimScoring.Corroboration(tiers.Select(t => t.SourceId));
        claim.TrustScore = ClaimScoring.TrustScore(
            tiers.DistinctBy(t => t.SourceId, StringComparer.OrdinalIgnoreCase)
                .Select(t => t.TrustTier));
        await db.SaveChangesAsync(ct);
        return ClaimOutcome.Corroborated;
    }

    /// <summary>Toets één claim aan de dichtstbijzijnde officiële §'s. Geen
    /// regelindex of geen bruikbaar oordeel ⇒ "unchecked" (volgende run);
    /// Degraded draagt in dat laatste geval de reden voor run_log.</summary>
    private async Task<(string OfficialStatus, string? Reason, string? Degraded)> CheckOfficialAsync(
        string statement, Vector vec, IReadOnlyList<string> officialSourceIds,
        CancellationToken ct)
    {
        var chunks = await db.RuleChunks.AsNoTracking()
            .Where(c => c.Embedding != null && c.SectionCode != null
                        && officialSourceIds.Contains(c.SourceId))
            .OrderBy(c => c.Embedding!.CosineDistance(vec))
            .Take(OfficialChunks)
            .Select(c => new { c.SectionCode, c.Text })
            .ToListAsync(ct);
        if (chunks.Count == 0) return ("unchecked", null, "geen officiële regelindex met embeddings");

        var raw = await AskSafeAsync(
            OfficialCheck.BuildPrompt(statement, chunks.Select(c => (c.SectionCode!, c.Text))),
            OfficialCheck.SystemPrompt, ct);
        if (raw is null) return ("unchecked", null, "rb-ai niet beschikbaar");
        var verdict = OfficialCheck.Parse(raw);
        if (verdict is null)
        {
            return ("unchecked", null,
                $"oordeel onparseerbaar. Respons (afgekapt): {LlmJson.Snippet(raw, ResponseSnippetLength)}");
        }

        return verdict.Verdict == "contradicted"
            ? ("contradicted", verdict.Reason ?? "de officiële regels spreken deze claim tegen", null)
            : (verdict.Verdict, null, null);
    }

    /// <summary>Her-toets claims die eerder "unchecked" bleven. Tegenspraak
    /// weerlegt: een al geaccepteerde claim wordt superseded (terug de queue
    /// in), een onbeoordeelde wordt rejected.</summary>
    private async Task<(int Rechecked, int Weerlegd)> RecheckOfficialAsync(
        IReadOnlyList<string> officialSourceIds,
        Action<string>? progress, CancellationToken ct)
    {
        var pending = await db.Claims
            .Where(c => c.OfficialStatus == "unchecked" && c.Embedding != null
                        && (c.Status == "unreviewed" || c.Status == "accepted"))
            .OrderBy(c => c.FirstSeen)
            .Take(MaxRechecksPerRun)
            .ToListAsync(ct);

        var rechecked = 0;
        var weerlegd = 0;
        var skipped = 0;
        string? firstDegradation = null;
        foreach (var claim in pending)
        {
            progress?.Invoke($"hertoets tegen officiële regels: {rechecked + 1}/{pending.Count}");
            var (officialStatus, reason, degraded) = await CheckOfficialAsync(
                claim.Statement, claim.Embedding!, officialSourceIds, ct);
            if (officialStatus == "unchecked")
            {
                // Nog steeds geen oordeel — herleidbaar houden, maar
                // geaggregeerd (één regel per run, geen 15 losse regels).
                skipped++;
                firstDegradation ??= degraded;
                continue;
            }

            rechecked++;
            claim.OfficialStatus = officialStatus;
            if (officialStatus == "contradicted")
            {
                claim.Status = claim.Status == "accepted" ? "superseded" : "rejected";
                claim.StatusReason = reason ?? "de officiële regels spreken deze claim tegen";
                weerlegd++;
            }
            await db.SaveChangesAsync(ct);
        }
        if (skipped > 0)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "claims", Ref = null, Status = "info",
                Detail = $"hertoets: {skipped} claim(s) blijven 'unchecked'"
                         + (firstDegradation is null ? "" : $" — {firstDegradation}"),
            });
            await db.SaveChangesAsync(ct);
        }
        return (rechecked, weerlegd);
    }

    /// <summary>Knipt documenttekst in extractie-delen op een woordgrens.</summary>
    private static List<string> Segment(string content)
    {
        var segments = new List<string>();
        var rest = content.Trim();
        while (rest.Length > 0 && segments.Count < MaxSegmentsPerDocument)
        {
            if (rest.Length <= SegmentChars)
            {
                segments.Add(rest);
                break;
            }
            var cut = rest.LastIndexOf(' ', SegmentChars);
            if (cut < SegmentChars / 2) cut = SegmentChars;
            segments.Add(rest[..cut]);
            rest = rest[cut..].TrimStart();
        }
        return segments;
    }
}
