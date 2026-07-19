using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using RbRules.Domain;
using RbRules.Domain.Reasoning;

namespace RbRules.Infrastructure;

public record ReasoningResult(
    int RulesRun, int DerivedEdges, int PatternsRun, int Hits, int NewConflicts, bool GraphAvailable)
{
    public string Summary => GraphAvailable
        ? $"{RulesRun} regels · {DerivedEdges} afgeleide edges · {PatternsRun} patronen · " +
          $"{Hits} treffers · {NewConflicts} nieuwe tegenspraken"
        : "graph niet beschikbaar — reasoner overgeslagen (afgeleide edges zijn herberekenbaar)";
}

/// <summary>Fase 3 (#227, §5) — de redeneer-run: één engine, Neo4j-native. Draait de
/// monotone inferentie-regels (<see cref="InferenceRuleRegistry"/>) via batched
/// Cypher-MERGE die afgeleide edges met <see cref="DerivedEdgeProvenance"/> tagt, en
/// de bounded contradictie-patronen (<see cref="ContradictionDetector"/>) die
/// treffers naar <see cref="ReasoningConflict"/>-rijen in Postgres vertalen (→
/// misvattingen-kanaal / reviewqueue via <see cref="ConflictRouter"/>).
///
/// Invarianten. (a) Afgeleide edges zijn NOOIT bron: bij elke run worden ze eerst
/// gewist en opnieuw gematerialiseerd — herberekenbaar, nooit als Postgres-feit
/// gepersisteerd (SoT = de basisfeiten in Postgres). (b) Een LLM-oordeel draagt hier
/// nooit iets: de regels zijn puur deterministisch (<c>model='deterministic'</c>).
/// (c) Elke run krijgt provenance: een <see cref="MiningRun"/> (kind "reasoning")
/// waaraan afgeleide edges en tegenspraken hangen.
///
/// Neo4j zit niet in CI/lokaal — de graaf-executie is best-effort (net als de
/// fase-2-projectie): valt Neo4j weg, dan doet de run niets (de PURE regel-/patroon-
/// generatie en de tegenspraak-vertaling zijn los getest). Live-Cypher-executie is
/// de integratie-follow-up, gedocumenteerd in docs/ARCHITECTURE §6.4.</summary>
public class ReasoningService(RbRulesDbContext db, IDriver driver)
{
    public async Task<ReasoningResult> RunAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        // Provenance-run (deterministisch — geen LLM/embedding).
        var run = new MiningRun
        {
            Id = Ulid.NewUlid(),
            Kind = "reasoning",
            PromptVersion = null,
            LlmModel = null,
        };
        db.MiningRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var prov = DerivedEdgeProvenance.Params(run.Id, DateTimeOffset.UtcNow);
        var rules = InferenceRuleRegistry.All;
        var patterns = ContradictionDetector.All;

        var derivedEdges = 0;
        var rulesRun = 0;
        var hits = new List<ContradictionHit>();
        var graphAvailable = true;

        try
        {
            await using var session = driver.AsyncSession();

            // (a) Afgeleide edges zijn geen bron: wis de vorige stand vóór her-
            // materialisatie. Alleen edges met derived=true — basisfeiten blijven.
            progress?.Invoke("afgeleide edges opruimen (herberekenbaar, nooit bron)");
            await session.RunAsync(
                $"MATCH ()-[r]->() WHERE r.{DerivedEdgeProvenance.DerivedProp} = true DELETE r");

            // Monotone inferentie: elke regel is een idempotente MERGE. Best-effort
            // per regel — een falende regel (bv. ontbrekend bron-edge-type in de
            // huidige projectie) stopt de rest niet.
            foreach (var rule in rules)
            {
                progress?.Invoke($"regel {rulesRun + 1}/{rules.Count}: {rule.Id}");
                var p = new Dictionary<string, object?>(prov);
                if (rule.Rows.Count > 0) p["rows"] = rule.Rows.Cast<object>().ToList();
                var cursor = await session.RunAsync(rule.Cypher, p);
                var summary = await cursor.ConsumeAsync();
                derivedEdges += summary.Counters.RelationshipsCreated;
                rulesRun++;
            }

            // Bounded contradictie-detectie: read-only patronen → rauwe treffers.
            var patternIx = 0;
            foreach (var pattern in patterns)
            {
                progress?.Invoke($"patroon {++patternIx}/{patterns.Count}: {pattern.Id}");
                var cursor = await session.RunAsync(pattern.Cypher);
                foreach (var record in await cursor.ToListAsync(ct))
                {
                    if (record["subjectRef"].As<string?>() is not { Length: > 0 } subjectRef) continue;
                    hits.Add(new ContradictionHit(
                        pattern,
                        subjectRef,
                        record["counterRef"].As<string?>(),
                        record["memo"].As<string?>()));
                }
            }
        }
        catch (Neo4jException)
        {
            graphAvailable = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Neo4j-uitval (driver-/connectie-fout) is een verwacht pad: de reasoner
            // is best-effort. Postgres blijft leidend; afgeleide edges zijn
            // herberekenbaar bij de volgende run.
            graphAvailable = false;
        }

        // Tegenspraken persisteren (Postgres = SoT), idempotent op de dedupe-sleutel:
        // dezelfde tegenspraak opnieuw detecteren maakt geen tweede rij.
        var newConflicts = 0;
        if (hits.Count > 0)
        {
            var known = (await db.ReasoningConflicts.AsNoTracking()
                    .Select(c => c.DedupeKey)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var hit in hits)
            {
                var conflict = ContradictionDetector.ToConflict(hit, run.Id);
                if (!known.Add(conflict.DedupeKey)) continue;   // al bekend of dubbel in deze run
                db.ReasoningConflicts.Add(conflict);
                newConflicts++;
            }
        }

        run.CompletedAt = DateTimeOffset.UtcNow;
        run.Candidates = hits.Count;
        run.Verified = derivedEdges;
        await db.SaveChangesAsync(ct);

        return new ReasoningResult(
            rulesRun, derivedEdges, graphAvailable ? patterns.Count : 0,
            hits.Count, newConflicts, graphAvailable);
    }
}
