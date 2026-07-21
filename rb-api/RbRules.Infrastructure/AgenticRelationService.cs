using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van één voorstellen-oogst (#120), met een compacte
/// trace-regel voor AskTrace.BrainSteps — de teller die de beheerder in de
/// vraag-trace ziet. <see cref="OutsideProjection"/> (#321): kandidaat-refs
/// die als knoop bestáán maar waarvan de soort nooit als RELATES_TO-eindpunt
/// projecteert — geweigerd mét reden i.p.v. opgeslagen-en-elke-rebuild-stil-
/// verdampt.</summary>
public record AgenticRelationResult(
    int Stored, int Blocked, int Duplicates, int NewKinds,
    int OutsideProjection, string TraceLine);

/// <summary>Agentic-terugkoppeling (#120): het relatievoorstellen-blok dat
/// de ask-agent na zijn antwoord achterliet parseren, valideren en als
/// Relation-voorstellen opslaan — het brein verrijkt zichzelf al
/// antwoordend. Zelfde poorten als de relatie-mining (#116): dedupe op
/// (van, naar, kind) over álle statussen, verworpen kinds niet opnieuw
/// opvoeren, nieuwe kinds als kandidaat. Eén poort is anders: de agentic
/// prompt bood geen ref-lijst aan, dus het hallucinatie-weer is hier
/// "bestaat de knoop echt in het brein?" — elke kandidaat-ref gaat langs
/// BrainService.NodeAsync, dat meteen naar de canonieke ref resolvet
/// (variant→canoniek #57, mechanic-case). LLM-relaties gaan nooit
/// rechtstreeks de graph in: alles landt als unreviewed voorstel.</summary>
public class AgenticRelationService(RbRulesDbContext db, BrainService brain)
{
    /// <summary>Provenance-voorvoegsel; de vraag gaat er als bron-context
    /// achteraan zodat het relaties-overzicht toont wáár het voorstel uit
    /// voortkwam.</summary>
    public const string ProvenancePrefix = "agentic-ask";

    private const int MaxProvenanceQuestionChars = 160;

    /// <summary>Zelfde afkap als de mining-diagnose (PR #87 / #93).</summary>
    private const int ResponseSnippetLength = 400;

    /// <summary>Zelfde weging als de mining (#116): LLM-interpretatie van
    /// gecureerd/officieel materiaal weegt als tier-2-bron.</summary>
    private static readonly double AgenticTrust = ClaimScoring.TierWeight(2);

    /// <summary>Parseert en bewaart één voorstellen-blok. Best-effort in de
    /// geest van "fouten zijn data": een onparseerbaar blok is een run_log-
    /// regel plus een trace-melding, nooit een exception richting het
    /// antwoordpad (de aanroeper vangt desondanks — een voorstel mag een
    /// gegeven antwoord nooit blokkeren).</summary>
    public async Task<AgenticRelationResult> StoreProposalsAsync(
        string question, string rawProposals, CancellationToken ct = default)
    {
        var questionSnippet = LlmJson.Snippet(question, MaxProvenanceQuestionChars);

        var candidates = RelationMiner.CandidateRefs(rawProposals);
        if (candidates is null)
        {
            // Onzin-output: reden + afgekapte respons in run_log (#93),
            // zodat de beheerder ziet wát de agent werkelijk achterliet.
            db.RunLogs.Add(new RunLog
            {
                Kind = "relations", Ref = ProvenancePrefix, Status = "error",
                Detail = $"voorstellenblok van de agentic ask onbruikbaar (vraag: {questionSnippet}). "
                         + $"Blok (afgekapt): {LlmJson.Snippet(rawProposals, ResponseSnippetLength)}",
            });
            await db.SaveChangesAsync(ct);
            return new(0, 0, 0, 0, 0,
                "[relatievoorstellen: blok onparseerbaar — genegeerd, zie run_log]");
        }

        // Twee poorten, elk met een eigen teller en reden (#321):
        // 1. De poort spiegelt de projectie (#286a-les): een ref-soort die de
        //    RELATES_TO-projectie nooit als eindpunt schrijft (alles buiten
        //    Card/Mechanic/Concept/RuleSection/Claim — de lijst komt uit de
        //    catalogus, niet uit een kopie hier) wordt geweigerd mét reden.
        //    Zonder deze poort landt zo'n voorstel als geldige rij die sinds
        //    #320 elke rebuild stil verdampt: BrainService.NodeAsync resolvet
        //    immers óók ruling:/source:/erratum:/change:/set:/domain:/tag:.
        // 2. Hallucinatie-weer: alleen refs die als knoop in het brein bestaan.
        //    De agent kreeg zijn refs uit de tool-resultaten en die komen uit
        //    ditzelfde brein — een ref die hier niet resolvet is per definitie
        //    verzonnen (of inmiddels verdwenen) en komt de database nooit in.
        var canonicalByOffered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outsideProjection = 0;
        var refusedKinds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!BrainRef.TryParse(candidate, out var parsed)) continue;
            if (!RelationProjection.CanBeEndpoint(parsed.Kind))
            {
                outsideProjection++;
                refusedKinds.Add(RefKindLabel(parsed));
                continue;
            }
            if (await brain.NodeAsync(parsed, ct) is { } node)
                canonicalByOffered[candidate] = node.Ref;
        }
        var blocked = candidates.Count - outsideProjection - canonicalByOffered.Count;
        var refusalNote = outsideProjection > 0
            ? $", {outsideProjection} geweigerd (eindpunt-soort projecteert niet: "
              + $"{string.Join(", ", refusedKinds)})"
            : "";

        // Zelfde parser als de mining (gedeelde LlmJson): normalisatie, caps,
        // zelf-relaties en dedupe binnen het blok. offeredRefs is hier de
        // gevalideerde kandidatenset i.p.v. de prompt-lijst.
        var extracted = RelationMiner.ParseRelations(
            rawProposals, canonicalByOffered.Keys) ?? [];

        // Kind-vocabulaire (#116): verworpen kinds niet opnieuw opvoeren,
        // onbekende kinds als kandidaat in de reviewqueue.
        var kindRows = await db.RelationKinds.ToListAsync(ct);
        var rejectedKinds = kindRows
            .Where(k => k.Status == "rejected")
            .Select(k => k.Kind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vocabularySet = new HashSet<string>(
            RelationMiner.KindVocabulary(
                kindRows.Where(k => k.Status == "accepted").Select(k => k.Kind)),
            StringComparer.OrdinalIgnoreCase);
        var kindsByName = kindRows.ToDictionary(k => k.Kind, StringComparer.OrdinalIgnoreCase);

        // Dedupe over runs heen op (van, naar, kind), álle statussen —
        // gericht bevraagd op de betrokken from-refs (per vraag hooguit een
        // handvol; de mining laadt álles, maar die draait per job).
        var fromRefs = extracted
            .Select(r => canonicalByOffered[r.FromRef])
            .Distinct()
            .ToList();
        var known = (await db.Relations.AsNoTracking()
                .Where(r => fromRefs.Contains(r.FromRef))
                .Select(r => new { r.FromRef, r.ToRef, r.Kind })
                .ToListAsync(ct))
            .Select(r => RelationMiner.DedupeKey(r.FromRef, r.ToRef, r.Kind))
            .ToHashSet(StringComparer.Ordinal);

        var stored = 0;
        var duplicates = 0;
        var newKinds = 0;
        foreach (var rel in extracted)
        {
            var fromCanonical = canonicalByOffered[rel.FromRef];
            var toCanonical = canonicalByOffered[rel.ToRef];
            // Twee verschillende agent-refs kunnen op dezelfde canonieke knoop
            // uitkomen (variant-id, #57) — dan is het alsnog een zelf-relatie
            // en telt hij als geweerd.
            if (string.Equals(fromCanonical, toCanonical, StringComparison.OrdinalIgnoreCase))
            {
                blocked++;
                continue;
            }
            if (rejectedKinds.Contains(rel.Kind)) { duplicates++; continue; }
            if (!known.Add(RelationMiner.DedupeKey(fromCanonical, toCanonical, rel.Kind)))
            {
                duplicates++;
                continue;
            }

            db.Relations.Add(new Relation
            {
                FromRef = fromCanonical, ToRef = toCanonical, Kind = rel.Kind,
                Explanation = rel.Explanation,
                Provenance = $"{ProvenancePrefix}: {questionSnippet}",
                Trust = AgenticTrust,
            });
            stored++;

            if (vocabularySet.Contains(rel.Kind)) continue;
            if (kindsByName.TryGetValue(rel.Kind, out var row))
            {
                row.Occurrences++;
            }
            else
            {
                var candidateKind = new RelationKind { Kind = rel.Kind, Occurrences = 1 };
                db.RelationKinds.Add(candidateKind);
                kindsByName[rel.Kind] = candidateKind;
                newKinds++;
            }
        }

        db.RunLogs.Add(new RunLog
        {
            Kind = "relations", Ref = ProvenancePrefix, Status = "ok",
            Detail = $"{stored} relatievoorstellen uit agentic ask, {blocked} geweerd "
                     + $"(onbekende ref), {duplicates} al bekend"
                     + refusalNote
                     + (newKinds > 0 ? $", {newKinds} nieuwe kind-kandidaten" : "")
                     + $" — vraag: {questionSnippet}",
        });
        await db.SaveChangesAsync(ct);

        return new(stored, blocked, duplicates, newKinds, outsideProjection,
            $"[relatievoorstellen: {stored} opgeslagen, {blocked} geweerd (onbekende ref), "
            + $"{duplicates} al bekend"
            + refusalNote
            + (newKinds > 0 ? $", {newKinds} nieuwe kind-kandidaten" : "") + "]");
    }

    /// <summary>Het ref-prefix ("ruling", "source") als soortnaam in de
    /// weiger-reden — dezelfde spelling die de agent zelf aanbood.</summary>
    private static string RefKindLabel(BrainRef parsed)
    {
        var formatted = parsed.Format();
        return formatted[..formatted.IndexOf(':')];
    }
}
