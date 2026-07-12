using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record RelationMineResult(
    int Units, int NewRelations, int Duplicates, int NewKinds, int Failed, string Message);

/// <summary>Relatie-mining (#116): de LLM ontdekt relaties over de kennislagen
/// heen (kaart↔mechaniek↔sectie↔concept↔claim) en levert ze als reviewbare
/// voorstellen in Postgres — nooit rechtstreeks de graph in. Twee soorten
/// mining-eenheden, elk één cheap-call (zelfde inschatting als de andere
/// miners: extractie is patroonwerk, het dure model blijft voor
/// gebruikersantwoorden):
/// 1. Primer-concepten als anker (gecureerde tekst, alle lagen als context);
///    idempotent via knowledge_doc.relations_mined_at, zelf-invaliderend
///    wanneer het doc later wijzigt (#92-les: markeren pas ná succes).
/// 2. Eén mechanieken-overzichtspass per run (mechanieken + hun §-context):
///    geen marker nodig — de dedupe op (van, naar, kind) maakt her-runs
///    idempotent en de pass is gecapt op één call.
/// #93-lessen overal: gedeelde LlmJson-parser, rauwe respons in run_log bij
/// parse-uitval, rb-ai-uitval is een gelogde stap en nooit een crash.</summary>
public class RelationMiningService(RbRulesDbContext db, RbAiClient ai)
{
    private const int MaxBodyChars = 3000;
    private const int MaxContextSections = 6;
    private const int MaxContextMechanics = 8;
    private const int MaxContextCards = 6;
    private const int MaxContextClaims = 5;
    private const int SectionSnippetChars = 220;
    private const int MechanicsPassMechanics = 24;
    /// <summary>Afkaplengte voor de rauwe LLM-respons in run_log-diagnose
    /// (patroon van de scout-fix, PR #87 / #93).</summary>
    private const int ResponseSnippetLength = 400;

    /// <summary>Relaties zijn LLM-interpretatie van gecureerd/officieel
    /// materiaal — de bewijsbron weegt als een tier-2-bron op de
    /// ClaimScoring-schaal, niet als de officiële laag zelf.</summary>
    private static readonly double MinedTrust = ClaimScoring.TierWeight(2);

    public async Task<RelationMineResult> RunAsync(
        bool force = false, int maxUnits = 12,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        maxUnits = Math.Clamp(maxUnits, 1, 50);

        // Kind-vocabulaire (#52-patroon): seed + geaccepteerd stuurt de
        // prompt; álle bekende rijen voor de kandidaat-upsert (verworpen
        // blijft verworpen — zo'n voorstel komt niet opnieuw de queue in).
        var kindRows = await db.RelationKinds.ToListAsync(ct);
        var acceptedKinds = kindRows
            .Where(k => k.Status == "accepted")
            .Select(k => k.Kind)
            .ToList();
        var rejectedKinds = kindRows
            .Where(k => k.Status == "rejected")
            .Select(k => k.Kind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vocabularySet = new HashSet<string>(
            RelationMiner.KindVocabulary(acceptedKinds), StringComparer.OrdinalIgnoreCase);
        var kindsByName = kindRows.ToDictionary(k => k.Kind, StringComparer.OrdinalIgnoreCase);
        var systemPrompt = RelationMiner.GetSystemPrompt(acceptedKinds);

        // Idempotentie over runs heen: alles wat al bestaat (élke status)
        // wordt niet opnieuw voorgesteld.
        var known = (await db.Relations.AsNoTracking()
                .Select(r => new { r.FromRef, r.ToRef, r.Kind })
                .ToListAsync(ct))
            .Select(r => RelationMiner.DedupeKey(r.FromRef, r.ToRef, r.Kind))
            .ToHashSet(StringComparer.Ordinal);

        // Ref-catalogus voor context én validatie: alleen canonieke kaarten
        // (#57), zonder embedding-vectoren (#43).
        var cards = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null)
            .Select(c => new CardInfo(c.RiftboundId, c.Name, c.Mechanics))
            .ToListAsync(ct);
        var mechanics = cards
            .SelectMany(c => c.Mechanics ?? [])
            .GroupBy(m => m, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, CardCount: g.Count()))
            .OrderByDescending(m => m.CardCount)
            .ThenBy(m => m.Name)
            .ToList();

        var units = 0;
        var newRelations = 0;
        var duplicates = 0;
        var newKinds = 0;
        var failed = 0;

        void Tally((int New, int Dupes, int Kinds, bool Failed) outcome)
        {
            units++;
            newRelations += outcome.New;
            duplicates += outcome.Dupes;
            newKinds += outcome.Kinds;
            if (outcome.Failed) failed++;
        }

        // ── 1. Concept-ankers: primer-docs die nog niet (of vóór hun laatste
        // wijziging) gemined zijn — de marker is zelf-invaliderend.
        var docs = await db.KnowledgeDocs
            .Where(k => k.Kind == "primer")
            .OrderBy(k => k.Topic)
            .ToListAsync(ct);
        var todo = docs
            .Where(k => force || k.RelationsMinedAt is null || k.RelationsMinedAt < k.UpdatedAt)
            .Take(maxUnits)
            .ToList();

        foreach (var doc in todo)
        {
            if (units >= maxUnits) break;
            progress?.Invoke(
                $"concept '{doc.Topic}' minen ({units + 1}, {newRelations} nieuw, {newKinds} kind-kandidaten)");

            var (lines, offered) = await BuildConceptContextAsync(doc, cards, mechanics, ct);
            var outcome = await ProcessUnitAsync(
                anchorLabel: $"spelconcept '{doc.Title}'",
                provenance: BrainRef.Concept(doc.Topic).Format(),
                systemPrompt, lines, offered,
                vocabularySet, rejectedKinds, kindsByName, known, ct);
            Tally(outcome);

            // #92-les: pas markeren wanneer de eenheid volledig slaagde — een
            // mislukte extractie komt de volgende run vanzelf opnieuw langs.
            if (!outcome.Failed) doc.RelationsMinedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // ── 2. Mechanieken-overzichtspass: één gecapte call per run; de
        // dedupe maakt hem idempotent (her-run zonder nieuwe kennis is een
        // no-op in opslag, hooguit één cheap-call).
        if (units < maxUnits && mechanics.Count >= 2)
        {
            progress?.Invoke("mechanieken-overzicht minen (relaties tussen mechanieken, secties en concepten)");
            var (lines, offered) = await BuildMechanicsContextAsync(docs, mechanics, ct);
            var outcome = await ProcessUnitAsync(
                anchorLabel: "mechanieken-overzicht",
                provenance: "mechanics-overzicht",
                systemPrompt, lines, offered,
                vocabularySet, rejectedKinds, kindsByName, known, ct);
            Tally(outcome);
            await db.SaveChangesAsync(ct);
        }

        var message =
            $"{units} eenheden verwerkt: {newRelations} nieuwe relatievoorstellen, "
            + $"{duplicates} al bekend of met verworpen kind, {newKinds} nieuwe kind-kandidaten, "
            + $"{failed} mislukt"
            + (failed > 0 ? " (redenen in run_log)" : "");
        return new(units, newRelations, duplicates, newKinds, failed, message);
    }

    /// <summary>Eén mining-eenheid: cheap-call → tolerante parse → opslag als
    /// unreviewed voorstellen + kind-kandidaten. Failed betekent: geen
    /// bruikbaar LLM-resultaat (uitval of onparseerbaar) — de aanroeper
    /// markeert het anker dan niet.</summary>
    private async Task<(int New, int Dupes, int Kinds, bool Failed)> ProcessUnitAsync(
        string anchorLabel, string provenance, string systemPrompt,
        IReadOnlyList<string> contextLines, IReadOnlyCollection<string> offeredRefs,
        HashSet<string> vocabularySet, HashSet<string> rejectedKinds,
        Dictionary<string, RelationKind> kindsByName, HashSet<string> known,
        CancellationToken ct)
    {
        var raw = await AskSafeAsync(
            RelationMiner.BuildPrompt(anchorLabel, contextLines), systemPrompt, ct);
        if (raw is null)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "relations", Ref = provenance, Status = "error",
                Detail = "rb-ai niet beschikbaar — eenheid overgeslagen, komt de volgende run terug",
            });
            return (0, 0, 0, true);
        }

        var extracted = RelationMiner.ParseRelations(raw, offeredRefs);
        if (extracted is null)
        {
            // Onzin-output: reden + afgekapte respons in run_log (#93/PR #87),
            // zodat de beheerder ziet wát het model werkelijk antwoordde.
            db.RunLogs.Add(new RunLog
            {
                Kind = "relations", Ref = provenance, Status = "error",
                Detail = "LLM-antwoord onbruikbaar — geen parseerbare relaties. "
                         + $"Respons (afgekapt): {LlmJson.Snippet(raw, ResponseSnippetLength)}",
            });
            return (0, 0, 0, true);
        }

        var added = 0;
        var dupes = 0;
        var newKinds = 0;
        foreach (var rel in extracted)
        {
            // Verworpen kind = de beheerder wil dit relatietype niet: het
            // voorstel wordt niet opnieuw opgevoerd (MechanicKeyword-afspraak).
            if (rejectedKinds.Contains(rel.Kind)) { dupes++; continue; }
            if (!known.Add(RelationMiner.DedupeKey(rel.FromRef, rel.ToRef, rel.Kind)))
            {
                dupes++;
                continue;
            }

            db.Relations.Add(new Relation
            {
                FromRef = rel.FromRef, ToRef = rel.ToRef, Kind = rel.Kind,
                Explanation = rel.Explanation, Provenance = provenance,
                Trust = MinedTrust,
            });
            added++;

            // Onbekend kind → kandidaat in de reviewqueue; bestaande
            // kandidaat telt op zodat de beheerder op impact kan sorteren.
            if (vocabularySet.Contains(rel.Kind)) continue;
            if (kindsByName.TryGetValue(rel.Kind, out var row))
            {
                row.Occurrences++;
            }
            else
            {
                var candidate = new RelationKind { Kind = rel.Kind, Occurrences = 1 };
                db.RelationKinds.Add(candidate);
                kindsByName[rel.Kind] = candidate;
                newKinds++;
            }
        }

        db.RunLogs.Add(new RunLog
        {
            Kind = "relations", Ref = provenance, Status = "ok",
            Detail = $"{added} nieuwe relatievoorstellen, {dupes} al bekend"
                     + (newKinds > 0 ? $", {newKinds} nieuwe kind-kandidaten" : ""),
        });
        return (added, dupes, newKinds, false);
    }

    /// <summary>Context voor een concept-anker: het doc zelf, zijn §-secties
    /// (SectionRefs), plus mechanieken/kaarten die de doc-tekst noemt en
    /// accepted/unreviewed claims op het topic — alle lagen als kandidaat.</summary>
    private sealed record CardInfo(string RiftboundId, string Name, string[]? Mechanics);

    private async Task<(IReadOnlyList<string> Lines, IReadOnlyCollection<string> Offered)>
        BuildConceptContextAsync(
            KnowledgeDoc doc,
            IReadOnlyList<CardInfo> cards, IReadOnlyList<(string Name, int CardCount)> mechanics,
            CancellationToken ct)
    {
        var lines = new List<string>();
        var offered = new List<string>();
        var body = doc.Body.Length > MaxBodyChars ? doc.Body[..MaxBodyChars] : doc.Body;

        var conceptRef = BrainRef.Concept(doc.Topic).Format();
        offered.Add(conceptRef);
        lines.Add($"- {conceptRef} — {doc.Title}:\n{body}");

        // §-secties waarop het doc gebaseerd is.
        var codes = (doc.SectionRefs ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Take(MaxContextSections)
            .ToList();
        if (codes.Count > 0)
        {
            var chunks = await db.RuleChunks.AsNoTracking()
                .Where(r => r.SectionCode != null && codes.Contains(r.SectionCode))
                .OrderBy(r => r.SourceId).ThenBy(r => r.ChunkIndex)
                .Select(r => new { r.SourceId, Code = r.SectionCode!, r.Text })
                .ToListAsync(ct);
            foreach (var chunk in chunks
                         .GroupBy(c => (c.SourceId, c.Code))
                         .Select(g => g.First())
                         .Take(MaxContextSections))
            {
                var sectionRef = BrainRef.Section(chunk.SourceId, chunk.Code).Format();
                offered.Add(sectionRef);
                lines.Add($"- {sectionRef} — §{chunk.Code}: {Snippet(chunk.Text)}");
            }
        }

        // Mechanieken die de doc-tekst noemt.
        var mentionedMechanics = mechanics
            .Where(m => body.Contains(m.Name, StringComparison.OrdinalIgnoreCase))
            .Take(MaxContextMechanics)
            .ToList();
        foreach (var m in mentionedMechanics)
        {
            var mechanicRef = BrainRef.Mechanic(m.Name).Format();
            offered.Add(mechanicRef);
            lines.Add($"- {mechanicRef} — mechaniek, komt voor op {m.CardCount} kaarten");
        }

        // Kaarten die de doc-tekst bij naam noemt (namen ≥ 4 tekens — korte
        // namen geven te veel toevalstreffers).
        var mentionedCards = 0;
        foreach (var card in cards)
        {
            if (mentionedCards >= MaxContextCards) break;
            if (card.Name.Length < 4
                || !body.Contains(card.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var cardRef = BrainRef.Card(card.RiftboundId).Format();
            offered.Add(cardRef);
            lines.Add($"- {cardRef} — kaart '{card.Name}'");
            mentionedCards++;
        }

        // Community-claims op dit topic (accepted/unreviewed — zelfde scope
        // als de graph; de status gaat mee als label).
        var topicLower = doc.Topic.ToLowerInvariant();
        var claims = await db.Claims.AsNoTracking()
            .Where(c => (c.Status == "accepted" || c.Status == "unreviewed")
                        && c.TopicRef.ToLower() == topicLower)
            .OrderByDescending(c => c.TrustScore)
            .Take(MaxContextClaims)
            .Select(c => new { c.Id, c.Statement, c.Status })
            .ToListAsync(ct);
        foreach (var claim in claims)
        {
            var claimRef = BrainRef.Claim(claim.Id).Format();
            offered.Add(claimRef);
            lines.Add($"- {claimRef} — community-claim ({claim.Status}): {Snippet(claim.Statement)}");
        }

        return (lines, offered);
    }

    /// <summary>Context voor de mechanieken-overzichtspass: de meest gebruikte
    /// mechanieken met per stuk een §-snippet dat de term noemt, plus de
    /// primer-concepten als mogelijke relatiedoelen.</summary>
    private async Task<(IReadOnlyList<string> Lines, IReadOnlyCollection<string> Offered)>
        BuildMechanicsContextAsync(
            IReadOnlyList<KnowledgeDoc> docs,
            IReadOnlyList<(string Name, int CardCount)> mechanics,
            CancellationToken ct)
    {
        var lines = new List<string>();
        var offered = new List<string>();

        foreach (var (name, cardCount) in mechanics.Take(MechanicsPassMechanics))
        {
            var mechanicRef = BrainRef.Mechanic(name).Format();
            offered.Add(mechanicRef);

            // Eerste §-sectie die de term noemt — de officiële definitie is
            // doorgaans de eerste treffer. LIKE-metatekens escapen zodat een
            // term als "100%" letterlijk matcht.
            var pattern = $"%{EscapeLike(name)}%";
            var chunk = await db.RuleChunks.AsNoTracking()
                .Where(r => r.SectionCode != null && r.SectionCode != ""
                            && EF.Functions.ILike(r.Text, pattern))
                .OrderBy(r => r.SourceId).ThenBy(r => r.ChunkIndex)
                .Select(r => new { r.SourceId, Code = r.SectionCode!, r.Text })
                .FirstOrDefaultAsync(ct);

            if (chunk is null)
            {
                lines.Add($"- {mechanicRef} — mechaniek, komt voor op {cardCount} kaarten");
                continue;
            }

            var sectionRef = BrainRef.Section(chunk.SourceId, chunk.Code).Format();
            if (!offered.Contains(sectionRef))
            {
                offered.Add(sectionRef);
                lines.Add($"- {sectionRef} — §{chunk.Code}: {Snippet(chunk.Text)}");
            }
            lines.Add($"- {mechanicRef} — mechaniek, komt voor op {cardCount} kaarten; zie §{chunk.Code}");
        }

        foreach (var doc in docs)
        {
            var conceptRef = BrainRef.Concept(doc.Topic).Format();
            offered.Add(conceptRef);
            lines.Add($"- {conceptRef} — spelconcept '{doc.Title}'");
        }

        return (lines, offered);
    }

    /// <summary>AskAsync met het scout-timeoutpatroon (ClaimMiningService):
    /// een HttpClient-timeout telt als uitval van één stap, niet als crash
    /// van de hele run.</summary>
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

    private static string Snippet(string text)
    {
        var flat = string.Join(' ',
            text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= SectionSnippetChars ? flat : flat[..SectionSnippetChars] + "…";
    }

    /// <summary>LIKE-metatekens escapen (AdminOverviewService-patroon).</summary>
    private static string EscapeLike(string s) =>
        s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
