using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.Ontology;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van één brein-interactie-mining-run: het aantal verwerkte
/// focus-kaarten, de door de poort gehaalde interacties per tier, en of de per-run-
/// cap geraakt is (drain-signaal, #190).</summary>
public sealed record BreinInteractionMiningResult(
    int FocusCards, int Extracted, int Promoted, int Candidates,
    int Hypothesized, int Rejected, int Failed, bool CapHit)
{
    public string Summary =>
        $"{FocusCards} kaarten, {Extracted} kandidaten geëxtraheerd → " +
        $"{Promoted} gepromoveerd, {Candidates} kandidaat, {Hypothesized} hypothese, " +
        $"{Rejected} verworpen, {Failed} rb-ai-uitval";
}

/// <summary>Brein-mining-orkestratie voor gekwalificeerde interacties (#226, §3.1 +
/// §3.4). ADDITIEF naast de bestaande <see cref="InteractionService"/>/<see cref="InteractionMiner"/>
/// (lexicaal-paar-gebaseerd, conditie-loos): deze pijplijn haalt tool-forced,
/// ontologie-begrensde kandidaten bij rb-ai (<c>/extract/interactions</c>,
/// spiegelt <see cref="InteractionExtraction"/>), entity-resolvet de rol-refs (fase 1)
/// VÓÓR kandidaat-creatie, en laat elke kandidaat door de fase-2-promotie-poort
/// (<see cref="InteractionPromotionService"/>) — schema ∧ (lexicaal ∨ consensus) ∧
/// verdict, met de cold-start-tier voor emergente card×card-hypotheses. Feit +
/// provenance worden atomair door die service gepersisteerd; deze klasse voegt geen
/// eigen graaf-write toe.
///
/// Degradatie is het verwachte pad: rb-ai null → die focus-kaart wordt overgeslagen
/// (Failed++), er wordt GEEN half feit geschreven, en de job rondt netjes af. De
/// selectie is bewust bounded (cap per run) en idempotent via de dedupe-sleutel van
/// de promotie-poort — herhaald draaien maakt geen duplicaten.</summary>
public class BreinInteractionMiningService(
    RbRulesDbContext db, RbAiClient ai, EntityResolutionService entityResolution,
    InteractionPromotionService promotion)
{
    private const int DefaultMaxFocusCards = 40;
    private const int DefaultMaxPartners = 4;

    /// <summary>De prompt-versie-stempel op de <see cref="MiningRun"/> — bump bij een
    /// wijziging aan de extractie-prompt/vorm (stale-conditie voor her-mining, §3.5).</summary>
    public const string PromptVersion = "breinmine-interactions-v1";

    public async Task<BreinInteractionMiningResult> RunAsync(
        int maxFocusCards = DefaultMaxFocusCards, int maxPartners = DefaultMaxPartners,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var windowLexicon = InteractionQualifierLexicon.Windows;
        var statusLexicon = InteractionQualifierLexicon.Statuses;

        // Focus-kaarten: canonieke printings met tekst die keywords dragen (die tekst
        // ís het bewijsanker). Bounded per run; herhaald draaien is idempotent.
        var focus = await db.Cards.AsNoTracking()
            .Where(c => c.VariantOf == null && c.Mechanics != null
                        && c.TextPlain != null && c.TextPlain != "")
            .OrderBy(c => c.RiftboundId)
            .Take(maxFocusCards + 1)
            .ToListAsync(ct);
        var capHit = focus.Count > maxFocusCards;
        if (capHit) focus = focus.Take(maxFocusCards).ToList();

        if (focus.Count == 0)
            return new(0, 0, 0, 0, 0, 0, 0, false);

        var run = await StartRunAsync(windowLexicon, statusLexicon, ct);

        int extracted = 0, promoted = 0, candidates = 0, hypothesized = 0, rejected = 0, failed = 0;
        var processed = 0;
        foreach (var card in focus)
        {
            processed++;
            progress?.Invoke($"interacties extraheren via rb-ai: {processed}/{focus.Count}");

            var offered = await BuildOfferedRefsAsync(card, maxPartners, ct);
            if (offered.Refs.Count < 2) continue; // niets zinnigs om over te redeneren

            var vocab = new ExtractionVocab(
                offered.Refs.Select(r => new OfferedRef(r.Ref, r.Label, r.Type)).ToList(),
                windowLexicon, statusLexicon);

            var raw = await ai.ExtractStructuredAsync(
                "/extract/interactions",
                new
                {
                    system = InteractionExtraction.SystemPrompt,
                    text = offered.PromptText,
                    refs = offered.Refs.Select(r => new { r.Ref, r.Label }),
                    kinds = InteractionKinds.All,
                    conditionKinds = InteractionConditionKinds.All,
                    roles = InteractionRoles.All,
                    windowLexicon,
                    statusLexicon,
                }, ct);

            if (raw is null)
            {
                // Degradatie: rb-ai weg → geen half feit, sla deze kaart over.
                failed++;
                continue;
            }

            var byRef = offered.Refs.ToDictionary(r => r.Ref, StringComparer.Ordinal);
            foreach (var ix in InteractionExtraction.Parse(raw, vocab))
            {
                if (!byRef.TryGetValue(ix.FromRef, out var from)
                    || !byRef.TryGetValue(ix.ToRef, out var to))
                    continue; // buiten de aangeboden set — parse gate't dit al, dubbel slot
                extracted++;

                var conditions = ix.Conditions
                    .Select(c => new InteractionConditionInput(c.OnKind, c.SubjectRole, c.Value, c.Operator))
                    .ToList();

                // Lexicale steun (§3.4): staan beide rol-labels letterlijk in het
                // bewijsanker (de aangeboden kaarttekst)? In-memory, geen EF-Contains.
                var lexical = TextContains(offered.EvidenceText, from.Label)
                    && TextContains(offered.EvidenceText, to.Label);

                var request = new InteractionPromotionRequest(
                    AgentRef: from.Ref, AgentType: from.Type,
                    PatientRef: to.Ref, PatientType: to.Type,
                    Kind: ix.Kind,
                    DerivedFromRef: BrainRef.Card(card.RiftboundId).Format(),
                    GovernedByRef: null,
                    Conditions: conditions,
                    LexicalSupport: lexical,
                    ConsensusCount: 1, // één extractie-pass = één bron
                    LlmVerdictInteracts: ix.Interacts);

                var result = await promotion.PromoteAsync(request, run.Id, ct: ct);
                switch (result.Outcome)
                {
                    case InteractionGateOutcome.Promoted: promoted++; break;
                    case InteractionGateOutcome.Candidate: candidates++; break;
                    case InteractionGateOutcome.ModelHypothesizedUnruled: hypothesized++; break;
                    case InteractionGateOutcome.Rejected: rejected++; break;
                }
            }
        }

        run.Candidates = extracted;
        run.Verified = promoted;
        run.Rejected = rejected;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new(focus.Count, extracted, promoted, candidates, hypothesized, rejected, failed, capHit);
    }

    /// <summary>De aangeboden refs (enum-vocabulaire) + het bewijsanker-tekstblok voor
    /// één focus-kaart: de kaart zelf, partner-kaarten die ≥1 mechaniek delen (bounded),
    /// en de entity-geresolvete keyword-refs van de focus-kaart. Entity-resolutie
    /// (fase 1) draait VÓÓR de refs ontstaan zodat synoniem-varianten
    /// ("Deflect"/"Deflecting") op één ref landen (versla #2).</summary>
    private async Task<OfferedSet> BuildOfferedRefsAsync(Card card, int maxPartners, CancellationToken ct)
    {
        var refs = new List<OfferedRefRow>();
        var promptParts = new List<string>();  // met ref-headers, naar rb-ai
        var evidenceParts = new List<string>(); // rauwe kaarttekst, voor de lexicale poort

        // Focus-kaart.
        refs.Add(new(BrainRef.Card(card.RiftboundId).Format(), card.Name, CardEntityType(card)));
        promptParts.Add($"[{BrainRef.Card(card.RiftboundId).Format()} — {card.Name}] {card.TextPlain}");
        evidenceParts.Add(card.TextPlain ?? "");

        var mechanics = card.Mechanics ?? [];

        // Keyword-refs, entity-geresolvet (canoniek label → één ref).
        var seenKeyword = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var surface in mechanics.Where(m => !string.IsNullOrWhiteSpace(m)))
        {
            var label = await ResolveKeywordLabelAsync(surface, ct);
            if (label.Length == 0 || !seenKeyword.Add(label)) continue;
            refs.Add(new(BrainRef.Mechanic(label).Format(), label, EntityType.Keyword));
        }

        // Partner-kaarten die een mechaniek delen (deterministische kandidaat-buurt).
        if (maxPartners > 0 && mechanics.Length > 0)
        {
            var partners = await db.Cards.AsNoTracking()
                .Where(c => c.VariantOf == null && c.RiftboundId != card.RiftboundId
                            && c.Mechanics != null && c.TextPlain != null && c.TextPlain != "")
                .OrderBy(c => c.RiftboundId)
                .ToListAsync(ct);
            var mechSet = mechanics.Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var picked = 0;
            foreach (var p in partners)
            {
                if (picked >= maxPartners) break;
                if (!(p.Mechanics ?? []).Any(m => mechSet.Contains(m.Trim()))) continue;
                refs.Add(new(BrainRef.Card(p.RiftboundId).Format(), p.Name, CardEntityType(p)));
                promptParts.Add($"[{BrainRef.Card(p.RiftboundId).Format()} — {p.Name}] {p.TextPlain}");
                evidenceParts.Add(p.TextPlain ?? "");
                picked++;
            }
        }

        // Prompt-tekst draagt de ref-headers zodat het model refs↔kaarten mapt; de
        // evidence-tekst is de RAUWE kaarttekst — de lexicale poort mag niet triviaal
        // slagen op een label dat we zelf in een header plakten.
        return new(refs, string.Join("\n", promptParts), string.Join("\n", evidenceParts));
    }

    /// <summary>Resolveert een keyword-surface-form tegen de canonieke laag (fase 1):
    /// bij een match het canonieke label, anders de magnitude-vrije basis ("Assault 2"
    /// → "Assault"). Nooit een nieuwe entiteit registreren hier — dat blijft de
    /// entity-resolution-job; deze mining leest alleen.</summary>
    private async Task<string> ResolveKeywordLabelAsync(string surface, CancellationToken ct)
    {
        var res = await entityResolution.ResolveAsync(surface, CanonicalEntityKinds.Keyword, ct);
        return res.Entity?.CanonicalLabel ?? Magnitude.Parse(surface).BaseLabel.Trim();
    }

    private async Task<MiningRun> StartRunAsync(
        IReadOnlyList<string> windows, IReadOnlyList<string> statuses, CancellationToken ct)
    {
        var run = new MiningRun
        {
            Id = Ulid.NewUlid(),
            Kind = FactKinds.Interaction,
            LlmModel = "claude-sonnet-4-6",
            PromptVersion = PromptVersion,
            VocabSnapshot = TextUtils.Sha256(string.Join('|',
                InteractionKinds.All.Concat(windows).Concat(statuses))),
        };
        db.MiningRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Kaart-<see cref="EntityType"/> uit het printed type (Unit/Legend/…);
    /// valt terug op de Card-umbrella als het type ontbreekt of niet als
    /// kaart-subklasse parseert — altijd een geldige HAS_ROLE-filler.</summary>
    private static EntityType CardEntityType(Card c) =>
        c.Type is { } t && OntologySchema.ParseEntityType(t) is { } et
            && OntologySchema.IsA(et, EntityType.Card)
            ? et : EntityType.Card;

    private static bool TextContains(string text, string term) =>
        !string.IsNullOrWhiteSpace(term)
        && text.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed record OfferedRefRow(string Ref, string Label, EntityType Type);
    private sealed record OfferedSet(
        IReadOnlyList<OfferedRefRow> Refs, string PromptText, string EvidenceText);
}
