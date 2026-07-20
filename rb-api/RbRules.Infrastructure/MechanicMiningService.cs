using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary><paramref name="Mined"/> = kaarten waarvoor de LLM een bruikbaar
/// antwoord gaf (triggers/effects geschreven); <paramref name="LlmAdded"/> =
/// hoeveel mechanieken dat LLM-oordeel bovenop de gebrackete vorm opleverde —
/// de meetlat of dat pad zijn kosten waard blijft (#211);
/// <paramref name="Reconciled"/> = kaarten waarvan de mechanieken door de
/// deterministische hersynchronisatie zijn bijgesteld.</summary>
public record MiningResult(
    int Mined, int Remaining, int Failed, int NewCandidates,
    int LlmAdded = 0, int Reconciled = 0);

/// <summary>F3: mine mechanieken/triggers/effects uit kaartteksten.
/// Herhaalbaar per set-release; idempotent op de wachtrij-poort
/// (<see cref="Pending"/>).
///
/// <b>Werkverdeling sinds #211.</b> De mechanieken zelf komen deterministisch
/// uit de gebrackete kaarttekst (<see cref="MechanicMiner.Analyze"/>) en worden
/// geschreven vóór en ONAFHANKELIJK van de rb-ai-call — bij AI-uitval heeft een
/// kaart dus gewoon zijn keywords (en zijn HAS_KEYWORD-edges in de graaf), in
/// plaats van niets. De LLM doet alleen wat de druk-vorm niet kan: het oordeel
/// over ongebrackete vermeldingen (gesloten kandidatenlijst, deterministisch
/// gevalideerd in <see cref="MechanicMiner.MergeMechanics"/>) plus de
/// semantische triggers/effects. Die twee velden blijven daarom de wachtrij-
/// poort: zonder LLM-antwoord blijft <c>Triggers == null</c> en komt de kaart
/// de volgende run terug — nooit een half feit dat als "klaar" telt.
///
/// Evolutie (#52): het vocabulaire = seed + geaccepteerde keywords, en elke run
/// rapporteert bracketed termen buiten dat vocabulaire als kandidaat voor de
/// reviewqueue.</summary>
public class MechanicMiningService(RbRulesDbContext db, RbAiClient ai)
{
    private const int BatchSize = 8;

    /// <summary>Nog te minen. Eén expressie voor de werklijst én de
    /// Remaining-telling — die liepen eerder uiteen (het ontbrekende
    /// VariantOf-filter hield Remaining eeuwig boven nul). EF-vertaalbaar.
    /// <c>Triggers == null</c> hoort erbij sinds #211: mechanieken worden ook
    /// zonder LLM geschreven, dus <c>Mechanics != null</c> alléén zou een kaart
    /// bij rb-ai-uitval definitief uit de wachtrij duwen.</summary>
    public static readonly Expression<Func<Card, bool>> Pending =
        c => c.VariantOf == null && c.TextPlain != null && c.TextPlain != ""
             && (c.Mechanics == null || c.Triggers == null);

    public async Task<MiningResult> RunAsync(
        int maxBatches = 25, DateTimeOffset? deadline = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var reviewed = await db.MechanicKeywords.AsNoTracking()
            .Where(k => k.Status == "accepted" || k.Status == "rejected")
            .OrderBy(k => k.Term)
            .Select(k => new { k.Term, k.Status })
            .ToListAsync(ct);
        var accepted = reviewed.Where(k => k.Status == "accepted").Select(k => k.Term).ToList();
        var rejected = reviewed.Where(k => k.Status == "rejected").Select(k => k.Term).ToList();
        var vocabulary = MechanicMiner.Vocabulary(accepted);

        var todo = await db.Cards
            .Where(Pending)
            .OrderBy(c => c.RiftboundId)
            .Take(maxBatches * BatchSize)
            .ToListAsync(ct);

        var mined = 0;
        var failed = 0;
        var llmAdded = 0;
        var done = 0;
        foreach (var batch in todo.Chunk(BatchSize))
        {
            // Nachtrun-deadline (#245): stop netjes op venster-einde — nog niet
            // gemijnde kaarten blijven in de wachtrij en komen de volgende run terug.
            if (deadline is { } dl && DateTimeOffset.UtcNow >= dl) break;
            done += batch.Length;
            progress?.Invoke($"kaartteksten analyseren: {done}/{todo.Count} in deze run");

            var inputs = batch
                .Select(c => new MiningInput(c, MechanicMiner.Analyze(c.TextPlain, vocabulary, rejected)))
                .ToList();
            // Deterministisch deel eerst: dit overleeft rb-ai-uitval, en gaat in
            // dezelfde SaveChanges mee als de rest van de batch. De
            // hersynchronisatie verderop zou het ook zetten, maar dan zou een
            // gewone uitval-kaart als "hergesynchroniseerd" tellen en die
            // teller meet juist de achterstand van vóór #211.
            foreach (var input in inputs) input.Card.Mechanics = input.Analysis.Bracketed;

            var raw = await ai.AskAsync(
                MechanicMiner.BuildPrompt(inputs), MechanicMiner.SystemPrompt, ct: ct);
            var parsed = raw is null ? [] : MechanicMiner.ParseBatch(raw);
            // Een model dat een id herhaalt mag de job niet laten crashen.
            var byId = parsed.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());

            foreach (var input in inputs)
            {
                var card = input.Card;
                if (byId.TryGetValue(card.RiftboundId, out var m))
                {
                    var merged = MechanicMiner.MergeMechanics(
                        input.Analysis.Bracketed, m.ExtraMechanics, input.Analysis.Candidates);
                    llmAdded += merged.Length - input.Analysis.Bracketed.Length;
                    card.Mechanics = merged;
                    card.Triggers = m.Triggers;
                    card.Effects = m.Effects;
                    mined++;
                }
                else
                {
                    failed++; // Triggers blijft null → volgende run opnieuw
                }
            }
            await db.SaveChangesAsync(ct);
        }

        progress?.Invoke("mechanieken hersynchroniseren met de kaarttekst");
        var reconciled = await ReconcileMechanicsAsync(vocabulary, rejected, ct);

        progress?.Invoke("keyword-kandidaten zoeken in kaartteksten");
        var newCandidates = await HarvestKeywordCandidatesAsync(vocabulary, ct);

        var remaining = await db.Cards.CountAsync(Pending, ct);
        return new(mined, remaining, failed, newCandidates, llmAdded, reconciled);
    }

    /// <summary>Brengt <see cref="Card.Mechanics"/> van álle canonieke kaarten
    /// in lijn met wat er gebracket in hun tekst staat (#211). Nodig omdat een
    /// eerder gemínede kaart niet meer in de wachtrij komt: haar mechanieken
    /// zijn nog het vrije-vorm LLM-resultaat van vóór deze increment, met de
    /// magnitude-splitsing die daarbij hoorde ("Assault 2" als eigen facet
    /// naast "Assault"). Deterministisch, dus gratis en herhaalbaar.
    ///
    /// Niet-destructief: de gebrackete mechanieken zijn leidend, en een
    /// bestaande term blijft alleen staan als hij nóg steeds een geldige
    /// ongebrackete kandidaat is — precies de vorm die het LLM-oordeel
    /// oplevert, dus een eerder oordeel gaat niet verloren. Alles daarbuiten
    /// (vrije vorm, magnitude-splitsing, inmiddels verworpen term) valt weg.
    /// Idempotent: een kaart die deze run net gemined is, levert dezelfde
    /// uitkomst en wordt dus niet nogmaals aangeraakt.</summary>
    private async Task<int> ReconcileMechanicsAsync(
        IReadOnlyList<string> vocabulary, IReadOnlyList<string> rejected, CancellationToken ct)
    {
        var cards = await db.Cards
            .Where(c => c.VariantOf == null && c.TextPlain != null && c.TextPlain != "")
            .ToListAsync(ct);

        var changed = 0;
        foreach (var card in cards)
        {
            var analysis = MechanicMiner.Analyze(card.TextPlain, vocabulary, rejected);
            var desired = MechanicMiner.MergeMechanics(
                analysis.Bracketed, card.Mechanics ?? [], analysis.Candidates);
            if (card.Mechanics is { } current && current.SequenceEqual(desired)) continue;
            card.Mechanics = desired;
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Scant álle canonieke kaartteksten op bracketed termen buiten
    /// het vocabulaire (deterministisch, geen LLM) en zet nieuwe termen als
    /// kandidaat in de reviewqueue. Occurrences worden bijgewerkt zodat de
    /// beheerder op impact kan sorteren; eerder verworpen termen blijven
    /// verworpen en komen dus niet opnieuw de queue in.</summary>
    private async Task<int> HarvestKeywordCandidatesAsync(
        IReadOnlyList<string> vocabulary, CancellationToken ct)
    {
        var texts = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null && c.TextPlain != null && c.TextPlain != "")
            .Select(c => c.TextPlain!)
            .ToListAsync(ct);

        // Aantal kaarten per term (case-insensitive; eerst geziene spelling wint).
        var counts = new Dictionary<string, (string Term, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        foreach (var term in MechanicMiner.ExtractKeywordCandidates(text, vocabulary))
        {
            counts[term] = counts.TryGetValue(term, out var c)
                ? (c.Term, c.Count + 1)
                : (term, 1);
        }
        if (counts.Count == 0) return 0;

        var known = await db.MechanicKeywords.ToListAsync(ct);
        var knownByTerm = known.ToDictionary(k => k.Term, StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var (term, count) in counts.Values)
        {
            if (knownByTerm.TryGetValue(term, out var row))
            {
                row.Occurrences = count; // status blijft staan (ook rejected)
            }
            else
            {
                db.MechanicKeywords.Add(new MechanicKeyword { Term = term, Occurrences = count });
                added.Add(term);
            }
        }
        if (added.Count > 0)
        {
            // Zichtbaar in "Recente activiteit": hier is beheer-actie gewenst.
            db.RunLogs.Add(new RunLog
            {
                Kind = "mine", Ref = "keywords", Status = "new",
                Detail = $"{added.Count} nieuwe keyword-kandidaten: {string.Join(", ", added.OrderBy(t => t))}",
            });
        }
        await db.SaveChangesAsync(ct);
        return added.Count;
    }
}
