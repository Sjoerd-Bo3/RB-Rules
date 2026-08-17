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
/// geen half feit. Bounded per run en idempotent via de dedupe-sleutel.
///
/// PARALLEL sinds #279, langs dezelfde lijn als
/// <see cref="BreinInteractionMiningService"/>: de kosten zitten in de rb-ai-call per
/// subject, dus subjecten gaan met meerdere workers tegelijk door de extractie, elk met
/// een eigen <see cref="RbRulesDbContext"/> uit de
/// <see cref="IDbContextFactory{TContext}"/> (DbContext is niet thread-safe). Zonder
/// factory (unit-tests op EF InMemory) valt de lus terug op één worker en het oude
/// sequentiële pad.
///
/// Eén verschil met de interactie-mining, en het is de reden dat hier GEEN schrijf-slot
/// nodig is: de dedupe-sleutel van een predicaat begint bij het subject
/// (<see cref="MechanicPredicateDedupe.Key"/>) en elk subject wordt door precies één
/// worker opgepakt. Twee workers kunnen dus per constructie niet om dezelfde sleutel
/// vechten — waar de interactie-poort juist wél twee kaarten op hetzelfde paar kan
/// zien uitkomen.</summary>
public class BreinPredicateMiningService(
    RbRulesDbContext db, RbAiClient ai,
    IDbContextFactory<RbRulesDbContext>? dbFactory = null,
    BreinMiningSettings? settings = null)
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

        var tally = new RunTally();
        var cursor = -1;

        // Eén worker = één subject tegelijk, met een eigen context per subject. Werk
        // wordt uit een gedeelde teller getrokken, dus een traag subject houdt de rest
        // niet op.
        async Task WorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref cursor);
                if (index >= subjects.Count) return;

                // Nachtrun-deadline (#245): stop netjes op venster-einde; het reeds-
                // gepredikeerd-watermark bewaart de voortgang voor de volgende nacht.
                if (deadline is { } dl && DateTimeOffset.UtcNow >= dl)
                {
                    tally.HitDeadline();
                    return;
                }

                var subject = subjects[index];
                tally.Report(progress, subjects.Count);

                await using var owned = dbFactory is null
                    ? null
                    : await dbFactory.CreateDbContextAsync(ct);
                await MineSubjectAsync(
                    subject, owned ?? db, run.Id, cards, objectHints, tally, ct);
            }
        }

        // Zonder factory hard terug naar één worker: parallel draaien op één gedeelde
        // DbContext is geen optimalisatie maar corruptie.
        var workers = dbFactory is null
            ? 1
            : Math.Clamp((settings ?? BreinMiningSettings.Default).Concurrency, 1, subjects.Count);
        if (workers == 1)
            await WorkerAsync();
        else
            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => WorkerAsync()));

        run.Candidates = tally.Mined + tally.Skipped;
        run.Verified = tally.Mined;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Subjects = daadwerkelijk verwerkt (bij deadline-stop < subjects.Count);
        // CapHit ⇔ er blijft vers werk liggen: cap geraakt óf deadline afgekapt.
        return new(tally.Processed, tally.Mined, tally.Skipped, tally.Failed,
            capHit || tally.DeadlineHit, tally.FailureDetail);
    }

    /// <summary>Eén subject: bewijstekst → rb-ai → parse → kandidaat-predicaten
    /// wegschrijven. Draait volledig op <paramref name="ctx"/> (eigen context per
    /// subject in de parallelle stand) en raakt de gedeelde scoped context nooit
    /// aan.</summary>
    private async Task MineSubjectAsync(
        CanonicalEntity subject, RbRulesDbContext ctx, string runId,
        IReadOnlyList<CardEvidence> cards, IReadOnlyList<string> objectHints,
        RunTally tally, CancellationToken ct)
    {
        var text = BuildEvidenceText(subject, cards);
        if (string.IsNullOrWhiteSpace(text))
        {
            tally.Skip();
            return;
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
            // Degradatie: geen half feit — maar de oorzaak wordt geteld (#251).
            tally.Fail(call.Outcome, call.Reason);
            return;
        }

        // Bestaande dedupe-sleutels van dit subject (elke status telt: een eerder
        // verworpen predicaat mag niet stil heropenen).
        var existing = await ctx.MechanicPredicates.AsNoTracking()
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
            tally.Fail(AiCallOutcome.Unparseable);
            return;
        }
        // Geldig antwoord zonder predicaten is geslaagd werk, geen uitval.
        tally.Ai(parsed.Items.Count == 0 ? AiCallOutcome.Empty : AiCallOutcome.Ok);
        foreach (var p in parsed.Items)
        {
            var key = MechanicPredicateDedupe.Key(subject.Id, p.Predicate, p.ObjectToken);
            if (existingKeys.Contains(key) || !seenThisSubject.Add(key))
            {
                tally.Skip();
                continue;
            }
            toAdd.Add(new MechanicPredicateAssertion
            {
                SubjectEntityId = subject.Id,
                Predicate = p.Predicate,
                ObjectToken = p.ObjectToken,
                Status = MechanicPredicateStatus.Candidate,
                CreatedByRunId = runId,
            });
        }

        if (toAdd.Count > 0)
        {
            ctx.MechanicPredicates.AddRange(toAdd);
            await ctx.SaveChangesAsync(ct);
            tally.Mine(toAdd.Count);
        }
    }

    /// <summary>De run-tellers, gedeeld door alle subject-workers (#279). Eén slot om
    /// alle mutaties — optellen is microseconden náást een rb-ai-call van tientallen
    /// seconden. De voortgangs-callback loopt door hetzelfde slot: die gaat naar de
    /// job-status en is niet als thread-safe gedocumenteerd.</summary>
    private sealed class RunTally
    {
        private readonly object _gate = new();
        private readonly AiOutcomeTally _ai = new();   // uitval per oorzaak (#251)
        private int _processed, _mined, _skipped, _failed;
        private bool _deadlineHit;

        public void Report(Action<string>? progress, int total)
        {
            lock (_gate)
            {
                _processed++;
                progress?.Invoke($"predicaten extraheren via rb-ai: {_processed}/{total}");
            }
        }

        public void Ai(AiCallOutcome outcome) { lock (_gate) _ai.Add(outcome); }

        /// <summary>Telt één uitval, met de fijnmazige reden die rb-ai meestuurde
        /// (#281) — zo staat er "5xx×22 (max_turns×14, spawn×8)" in het run-detail
        /// in plaats van alleen "5xx×22". Null blijft het gedrag van vóór #281.</summary>
        public void Fail(AiCallOutcome outcome, string? reason = null)
        {
            lock (_gate) { _failed++; _ai.Add(outcome, reason); }
        }

        public void Mine(int count) { lock (_gate) _mined += count; }
        public void Skip() { lock (_gate) _skipped++; }
        public void HitDeadline() { lock (_gate) _deadlineHit = true; }

        public int Processed { get { lock (_gate) return _processed; } }
        public int Mined { get { lock (_gate) return _mined; } }
        public int Skipped { get { lock (_gate) return _skipped; } }
        public int Failed { get { lock (_gate) return _failed; } }
        public bool DeadlineHit { get { lock (_gate) return _deadlineHit; } }

        public string? FailureDetail
        {
            get
            {
                lock (_gate) return _ai.Summary is { Length: > 0 } detail ? detail : null;
            }
        }
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
