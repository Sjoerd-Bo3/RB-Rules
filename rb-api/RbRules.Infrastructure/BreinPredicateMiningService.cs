using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van één brein-predicaat-mining-run: verwerkte subjecten,
/// nieuw aangedragen predicaten, overgeslagen (dedupe/geen-tekst) en rb-ai-uitval,
/// plus of de per-run-cap geraakt is.</summary>
public sealed record BreinPredicateMiningResult(
    int Subjects, int Mined, int Skipped, int Failed, bool CapHit, string? FailureDetail = null)
{
    public string Summary =>
        $"{Subjects} mechanics/keywords, {Mined} predicaten aangedragen (ter review), " +
        $"{Skipped} overgeslagen, {Failed} rb-ai-uitval" +
        (string.IsNullOrEmpty(FailureDetail) ? "" : $" ({FailureDetail})");
}

/// <summary>Brein-mining-orkestratie voor getypeerde mechanic-predicaten (#226/#229,
/// §5). Haalt tool-forced kandidaten bij rb-ai (<c>/extract/predicates</c>, spiegelt
/// <see cref="MechanicPredicateExtraction"/>) per canonieke mechanic/keyword-entiteit
/// (fase 1: het subject IS al geresolveerd), en legt ze als
/// <see cref="MechanicPredicateAssertion"/> in status <c>candidate</c> vast — met
/// <see cref="MechanicPredicateAssertion.CreatedByRunId"/> als 0a-provenance en de
/// unieke dedupe-sleutel als hard slot. Een LLM-verdict promoveert hier NIETS: elk
/// predicaat wacht op menselijke review (voedt de <see cref="HypothesisEngine"/> pas
/// als <c>reviewed</c>) — de rode draad (#236) dat een LLM-oordeel nooit alléén een
/// promotie draagt.
///
/// Degradatie is het verwachte pad: rb-ai null → subject overgeslagen (Failed++),
/// geen half feit. Bounded per run en idempotent via de dedupe-sleutel.</summary>
public class BreinPredicateMiningService(RbRulesDbContext db, RbAiClient ai)
{
    private const int DefaultMaxSubjects = 40;
    private const int MaxEvidenceCards = 3;

    public const string PromptVersion = "breinmine-predicates-v1";

    public async Task<BreinPredicateMiningResult> RunAsync(
        int maxSubjects = DefaultMaxSubjects, DateTimeOffset? deadline = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        // Subjecten: levende mechanic/keyword-entiteiten die nog géén predicaat dragen
        // (bounded, idempotent). Een reeds-gepredikeerd subject wordt overgeslagen tot
        // een expliciete her-mine (geen stille her-run-kosten).
        var predicatedIds = await db.MechanicPredicates.AsNoTracking()
            .Select(p => p.SubjectEntityId).Distinct().ToListAsync(ct);
        var predicatedSet = predicatedIds.ToHashSet();

        var subjects = await db.CanonicalEntities.AsNoTracking()
            .Where(e => e.Status != CanonicalEntityStatus.Merged
                        && (e.Kind == CanonicalEntityKinds.Mechanic
                            || e.Kind == CanonicalEntityKinds.Keyword))
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        subjects = subjects.Where(e => !predicatedSet.Contains(e.Id)).ToList();

        var capHit = subjects.Count > maxSubjects;
        if (capHit) subjects = subjects.Take(maxSubjects).ToList();

        if (subjects.Count == 0)
            return new(0, 0, 0, 0, false);

        // Bewijstekst-bron: canonieke kaarten met mechanieken + tekst (één keer geladen,
        // per subject in-memory gefilterd — geen EF-array-Contains).
        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null && c.Mechanics != null
                        && c.TextPlain != null && c.TextPlain != "")
            .Select(c => new CardEvidence(c.Name, c.Mechanics, c.TextPlain))
            .ToListAsync(ct);

        var run = await StartRunAsync(ct);
        var objectHints = MechanicPredicateKinds.All
            .SelectMany(MechanicPredicateLexicon.SeedFor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        int mined = 0, skipped = 0, failed = 0;
        var aiTally = new AiOutcomeTally();   // uitval per oorzaak (#251)
        var processed = 0;
        var deadlineHit = false;
        foreach (var subject in subjects)
        {
            // Nachtrun-deadline (#245): stop netjes op venster-einde; het reeds-
            // gepredikeerd-watermark bewaart de voortgang voor de volgende nacht.
            if (deadline is { } dl && DateTimeOffset.UtcNow >= dl) { deadlineHit = true; break; }
            processed++;
            progress?.Invoke($"predicaten extraheren via rb-ai: {processed}/{subjects.Count}");

            var text = BuildEvidenceText(subject, cards);
            if (string.IsNullOrWhiteSpace(text))
            {
                skipped++;
                continue;
            }

            var subjectRef = subject.Ref.Format();
            var call = await ai.ExtractStructuredDetailedAsync(
                "/extract/predicates",
                new
                {
                    system = MechanicPredicateExtraction.SystemPrompt,
                    text,
                    subjectRef,
                    subjectLabel = subject.CanonicalLabel,
                    predicates = MechanicPredicateKinds.All,
                    objectHints,
                }, ct);

            if (call.Raw is null)
            {
                failed++;                  // degradatie: geen half feit
                aiTally.Add(call.Outcome); // maar de oorzaak wordt geteld (#251)
                continue;
            }

            // Bestaande dedupe-sleutels van dit subject (elke status telt: een eerder
            // verworpen predicaat mag niet stil heropenen).
            var existing = await db.MechanicPredicates.AsNoTracking()
                .Where(p => p.SubjectEntityId == subject.Id)
                .Select(p => new { p.Predicate, p.ObjectToken })
                .ToListAsync(ct);
            var existingKeys = existing
                .Select(p => MechanicPredicateDedupe.Key(subject.Id, p.Predicate, p.ObjectToken))
                .ToHashSet(StringComparer.Ordinal);

            var toAdd = new List<MechanicPredicateAssertion>();
            var seenThisSubject = new HashSet<string>(StringComparer.Ordinal);
            var parsed = MechanicPredicateExtraction.ParseDetailed(call.Raw);
            if (parsed.Malformed)
            {
                // HTTP 200 met een afgekapte/schema-vreemde body is UITVAL, geen leeg
                // resultaat (#251-review): stil tot [] reduceren verstopte parse-fouten
                // in de "geslaagd"-bak en maakte de uitvalmeting onbetrouwbaar.
                failed++;
                aiTally.Add(AiCallOutcome.Unparseable);
                continue;
            }
            // Geldig antwoord zonder predicaten is geslaagd werk, geen uitval.
            aiTally.Add(parsed.Items.Count == 0 ? AiCallOutcome.Empty : AiCallOutcome.Ok);
            foreach (var p in parsed.Items)
            {
                var key = MechanicPredicateDedupe.Key(subject.Id, p.Predicate, p.ObjectToken);
                if (existingKeys.Contains(key) || !seenThisSubject.Add(key))
                {
                    skipped++;
                    continue;
                }
                toAdd.Add(new MechanicPredicateAssertion
                {
                    SubjectEntityId = subject.Id,
                    Predicate = p.Predicate,
                    ObjectToken = p.ObjectToken,
                    Status = MechanicPredicateStatus.Candidate,
                    CreatedByRunId = run.Id,
                });
            }

            if (toAdd.Count > 0)
            {
                db.MechanicPredicates.AddRange(toAdd);
                await db.SaveChangesAsync(ct);
                mined += toAdd.Count;
            }
        }

        run.Candidates = mined + skipped;
        run.Verified = mined;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Subjects = daadwerkelijk verwerkt (bij deadline-stop < subjects.Count);
        // CapHit ⇔ er blijft vers werk liggen: cap geraakt óf deadline afgekapt.
        return new(processed, mined, skipped, failed, capHit || deadlineHit,
            aiTally.Summary is { Length: > 0 } detail ? detail : null);
    }

    /// <summary>Bewijstekst voor één subject: de <see cref="CanonicalEntity.Definition"/>
    /// als die er is, aangevuld met de tekst van maximaal
    /// <see cref="MaxEvidenceCards"/> kaarten die de mechaniek (canoniek label of
    /// alias) dragen. Leeg wanneer er geen bewijstekst is — dan wordt het subject
    /// overgeslagen (nooit een lege prompt).</summary>
    private static string BuildEvidenceText(
        CanonicalEntity subject, IReadOnlyList<CardEvidence> cards)
    {
        var labels = new HashSet<string>(
            new[] { subject.CanonicalLabel }.Concat(subject.AltLabels),
            StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(subject.Definition))
            parts.Add(subject.Definition!);

        var picked = 0;
        foreach (var c in cards)
        {
            if (picked >= MaxEvidenceCards) break;
            if (!(c.Mechanics ?? []).Any(m => labels.Contains((m ?? "").Trim()))) continue;
            parts.Add($"[{c.Name}] {c.TextPlain}");
            picked++;
        }

        return string.Join("\n", parts);
    }

    private sealed record CardEvidence(string Name, string[]? Mechanics, string? TextPlain);

    private async Task<MiningRun> StartRunAsync(CancellationToken ct)
    {
        var labels = await db.CanonicalEntities.AsNoTracking()
            .OrderBy(e => e.Id).Select(e => e.CanonicalLabel).ToListAsync(ct);
        var run = new MiningRun
        {
            Id = Ulid.NewUlid(),
            Kind = FactKinds.Mechanic,
            LlmModel = "claude-sonnet-4-6",
            PromptVersion = PromptVersion,
            VocabSnapshot = TextUtils.Sha256(string.Join('\n', labels)),
        };
        db.MiningRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }
}
