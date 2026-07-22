using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.Reasoning;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van één audit-run (#255).</summary>
/// <param name="Audited">Aantal geveld oordeel (audit-rij geschreven).</param>
/// <param name="Sound">Oordeel "correct én gedragen door het bewijs".</param>
/// <param name="Disputed">Oordeel "onjuist" of "niet gedragen" — zichtbaar gemaakt
/// via het reviewqueue-kanaal, nooit als degradatie.</param>
/// <param name="Failed">rb-ai-uitval of onleesbaar oordeel — géén audit-rij, de
/// interactie komt de volgende run terug.</param>
public sealed record BreinInteractionAuditResult(
    int Audited, int Sound, int Disputed, int Failed, bool CapHit,
    string? FailureDetail = null,
    string Model = BreinInteractionAuditService.AuditModel)
{
    public string Summary =>
        $"{Audited} gepromoveerde interacties geauditeerd (steekproef door " +
        $"{Model}) → {Sound} bevestigd, " +
        $"{Disputed} betwist, {Failed} rb-ai-uitval" +
        (string.IsNullOrEmpty(FailureDetail) ? "" : $" ({FailureDetail})");
}

/// <summary>Steekproef-audit van gepromoveerde interacties door een STERKER model
/// (#255). De observability toonde "precisie ≈ 0,91" voor de interactie-mining, maar
/// dat is de accept-ratio van onze eigen promotie-poort (verified ÷ judged) —
/// zelfreferentieel: een pijplijn die tautologieën promoveert scoort er uitstekend
/// op. Deze pass laat rb-ai's <c>/audit/interaction</c> via een gesloten,
/// beheerbare modelalias per gesamplede interactie een tool-forced oordeel vellen:
/// klopt de bewering, en wordt ze gedragen door het bewijs waarop ze promoveerde?
///
/// <b>DE HARDE REGEL</b> (issue #255, rode draad #236): het oordeel wordt vastgelegd
/// als aparte <see cref="InteractionAudit"/>-rij met eigen provenance (model +
/// promptversie + datum) en verandert NOOIT zelfstandig een tier. Deze service
/// schrijft per constructie niet aan <see cref="Interaction"/>-rijen, tombstones of
/// beslissings-memo's; een negatief oordeel wordt hooguit zichtbaar gemaakt als
/// <see cref="ReasoningConflict"/> in het reviewqueue-kanaal, waar een beheerder
/// beslist. Een LLM-oordeel draagt geen actie alleen.
///
/// <b>1-op-N-selectie met watermark.</b> De steekproef is deterministisch
/// <c>Id % N == 0</c> (N beheerd via <see cref="ManagedSettingsCatalog"/>, #254 —
/// geen env-only vlag, gelezen op het GEBRUIKSMOMENT zodat een bijgestelde
/// dichtheid meteen geldt). Let op wat dat WEL en NIET is (#255-review): een
/// VASTE deelverzameling, geen rotatie — bij N=10 wordt ~90% van de gepromoveerde
/// interacties nooit geauditeerd, onder geen enkele promptversie. Dat is precies
/// wat een steekproef hoort te zijn (de meting generaliseert over de populatie);
/// wie volledige dekking wil, zet N op 1. Binnen de steekproef is de audit-rij
/// zélf het watermark: een beoordeeld lid komt niet terug vóór de steekproef
/// rond is (een prompt-bump heropent hem), en een GEFAALDE audit schrijft géén
/// rij — die interactie komt de volgende run gewoon terug (#249-les: nooit een
/// watermark op uitval).
///
/// Sequentieel, bewust: de steekproef is per constructie klein (pool ÷ N, gecapt
/// per run) en elke oproep is een dure audit-model-call — parallelliseren koopt hier
/// niets en zou de drie #279-plichten (context-per-worker, schrijfslot, deelcap)
/// zonder winst importeren. De rb-ai-semafoor doet de rest: het endpoint draait op
/// achtergrond-prioriteit, dus /ask houdt zijn reserve.</summary>
public class BreinInteractionAuditService(
    RbRulesDbContext db, RbAiClient ai, ManagedSettingsService settings)
{
    /// <summary>Concreet legacy/defaultmodel achter de opus-alias. Nieuwe rb-ai-
    /// antwoorden dragen altijd hun werkelijke provider/model; deze waarde is alleen
    /// de eerlijke fallback bij een oudere sidecar zonder die metadata.</summary>
    public const string AuditModel = "claude-opus-4-8";

    /// <summary>Per-run cap (overdag): een handvol dure hard-model-calls per klik.
    /// De nachtrun draait de ongecapte variant met de venster-deadline.</summary>
    public const int DefaultMaxAudits = 10;

    /// <summary>PatternId in het conflict-kanaal — geen detector-patroon maar de
    /// audit-pass; de router stuurt <see cref="ReasoningConflictKind.AuditDisputesInteraction"/>
    /// naar de reviewqueue.</summary>
    public const string ConflictPatternId = "interaction-audit-sample";

    public async Task<BreinInteractionAuditResult> RunAsync(
        int maxAudits = DefaultMaxAudits, DateTimeOffset? deadline = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        // Op het gebruiksmoment gelezen (#254): een in beheer bijgestelde dichtheid
        // geldt voor de eerstvolgende run, zonder herstart.
        var auditSettings = await settings.BreinAuditAsync(ct);
        var divisor = Math.Max(1, auditSettings.SampleDivisor);
        // Eén snapshot voor de hele run: een beheerwijziging halverwege mag nooit
        // één MiningRun over twee providers/modellen verdelen.
        var modelAlias = auditSettings.ModelAlias;
        var fallbackModel = modelAlias == BreinExtractModelAliases.Opus ? AuditModel : null;
        var promptVersion = InteractionAuditExtraction.PromptVersion;

        // De steekproef-pool: gepromoveerd, in de deterministische 1-op-N-greep, en
        // zonder recent (= huidige promptversie) oordeel. Id % N is stabiel over
        // runs — een interactie hoort bij de steekproef of niet, ongeacht hoe de
        // pool intussen kromp — en EF-vertaalbaar (SQL %).
        var selected = await db.Interactions
            .Include(x => x.Conditions)
            .Where(x => x.Status == InteractionStatus.Promoted)
            .Where(x => x.Id % divisor == 0)
            .Where(x => !db.InteractionAudits.Any(a =>
                a.InteractionId == x.Id && a.PromptVersion == promptVersion))
            .OrderBy(x => x.Id)
            .Take(maxAudits + 1)
            .ToListAsync(ct);
        var capHit = selected.Count > maxAudits;
        if (capHit) selected = selected.Take(maxAudits).ToList();
        if (selected.Count == 0)
            return new(0, 0, 0, 0, false, Model: fallbackModel ?? modelAlias);

        var run = new MiningRun
        {
            Id = Ulid.NewUlid(),
            Kind = FactKinds.InteractionAudit,
            LlmModelAlias = modelAlias,
            LlmModel = fallbackModel,
            PromptVersion = promptVersion,
        };
        db.MiningRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var evidence = await LoadEvidenceSourcesAsync(selected, ct);

        var tally = new AiOutcomeTally();
        int audited = 0, sound = 0, disputed = 0, failed = 0;
        int llmCalls = 0;
        long inputTokens = 0, outputTokens = 0;
        decimal costUsd = 0;
        bool hasUsage = false, hasCost = false;
        string? provider = null, actualModel = null, usageUnit = null;
        var stoppedOnDeadline = false;

        foreach (var (interaction, index) in selected.Select((x, i) => (x, i)))
        {
            if (deadline is { } dl && DateTimeOffset.UtcNow >= dl)
            {
                // Netjes stoppen op venster-einde: wat geen oordeel kreeg draagt
                // geen watermark en volgt de volgende nacht.
                stoppedOnDeadline = true;
                break;
            }
            progress?.Invoke($"interactie-audit via rb-ai (sterker model): {index + 1}/{selected.Count}");

            var text = ComposeAuditText(interaction, evidence);
            var call = await ai.ExtractStructuredDetailedAsync(
                "/audit/interaction",
                new { model = modelAlias, system = InteractionAuditExtraction.SystemPrompt, text }, ct);
            llmCalls++;
            provider ??= call.Provider;
            actualModel = call.Model ?? actualModel;
            if (call.Usage is { } usage)
            {
                hasUsage = true;
                inputTokens += usage.InputTokens;
                outputTokens += usage.OutputTokens;
                usageUnit ??= usage.Unit;
                if (usage.CostUsd is { } cost)
                {
                    hasCost = true;
                    costUsd += cost;
                }
            }
            if (call.Raw is null)
            {
                // Degradatie (#249-les): géén audit-rij — dus géén watermark — bij
                // uitval; deze interactie komt de volgende run terug.
                failed++;
                tally.Add(call.Outcome, call.Reason);
                continue;
            }

            var parsed = InteractionAuditExtraction.ParseDetailed(call.Raw);
            if (parsed.Malformed)
            {
                // Een oordeel buiten het gesloten schema is UITVAL, geen coulant
                // gelezen oordeel (mutatie-eis (b) van #255).
                failed++;
                tally.Add(AiCallOutcome.Unparseable);
                continue;
            }
            tally.Add(AiCallOutcome.Ok);

            var verdict = parsed.Items[0];
            audited++;
            if (verdict.Correct && verdict.SupportedByEvidence) sound++; else disputed++;

            // Audit-rij + (bij een negatief oordeel) de zichtbare-kanaal-rij in één
            // transactie — geen half vastgelegd oordeel (rode draad #236). En NIETS
            // anders: de interactie-rij zelf blijft onaangeraakt.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.InteractionAudits.Add(new InteractionAudit
            {
                InteractionId = interaction.Id,
                RunId = run.Id,
                Model = call.Model ?? fallbackModel ?? modelAlias,
                PromptVersion = promptVersion,
                Correct = verdict.Correct,
                SupportedByEvidence = verdict.SupportedByEvidence,
                Motivation = verdict.Motivation,
                InteractionStatusAtAudit = interaction.Status,
            });
            if (!(verdict.Correct && verdict.SupportedByEvidence))
                await QueueDisputeAsync(
                    interaction, verdict, call.Model ?? fallbackModel ?? modelAlias, run.Id, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        run.Candidates = audited + failed; // aangeboden aan het model
        run.Verified = sound;
        run.Rejected = disputed;
        run.LlmCalls = llmCalls;
        run.LlmProvider = provider;
        run.LlmModel = actualModel ?? fallbackModel;
        run.InputTokens = hasUsage ? inputTokens : null;
        run.OutputTokens = hasUsage ? outputTokens : null;
        run.UsageUnit = hasUsage ? usageUnit : null;
        run.CostUsd = hasCost ? costUsd : null;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new(audited, sound, disputed, failed,
            capHit || stoppedOnDeadline,
            tally.Summary is { Length: > 0 } detail ? detail : null,
            actualModel ?? fallbackModel ?? modelAlias);
    }

    /// <summary>Het negatieve oordeel zichtbaar maken — en niets anders. Het
    /// bestaande reviewqueue-kanaal (<see cref="ReasoningConflict"/>) is precies de
    /// "reviewqueue-achtige" plek die het issue eist: de rij verschijnt in de
    /// conflicts-verkenner, een beheerder beslist. Idempotent op de dedupe-sleutel
    /// zodat een her-audit (na een prompt-bump) geen tweede open rij maakt.
    ///
    /// LET OP (#255-review): die dedupe is bewust promptversie- én status-blind —
    /// de sleutel is <c>patternId|subject|</c> en de check telt élke status mee.
    /// Vandaag is dat onschuldig omdat er geen resolve-flow voor deze rijen
    /// bestaat: één zichtbare melding per interactie is precies genoeg. Komt er
    /// ooit een pad dat zo'n conflict op resolved/dismissed zet, dan moet deze
    /// dedupe mee herzien worden — anders verdwijnt een héér-dispute na een
    /// prompt-bump onzichtbaar achter de afgehandelde rij.</summary>
    private async Task QueueDisputeAsync(
        Interaction interaction, InteractionAuditVerdict verdict, string model, string runId,
        CancellationToken ct)
    {
        var subjectRef = interaction.Ref.Format();
        var dedupeKey = ReasoningConflictDedupe.Key(ConflictPatternId, subjectRef, null);
        if (await db.ReasoningConflicts.AnyAsync(c => c.DedupeKey == dedupeKey, ct)) return;

        var kind = ReasoningConflictKind.AuditDisputesInteraction;
        db.ReasoningConflicts.Add(new ReasoningConflict
        {
            PatternId = ConflictPatternId,
            Kind = kind,
            Channel = ConflictRouter.ChannelString(ConflictRouter.Route(kind)),
            SubjectRef = subjectRef,
            CounterRef = null,
            Memo = $"steekproef-audit ({model}, {InteractionAuditExtraction.PromptVersion}): "
                + $"correct={(verdict.Correct ? "ja" : "nee")}, "
                + $"gedragen={(verdict.SupportedByEvidence ? "ja" : "nee")}"
                + (string.IsNullOrWhiteSpace(verdict.Motivation) ? "" : $" — {verdict.Motivation}"),
            DedupeKey = dedupeKey,
            RunId = runId,
        });
    }

    // ── Bewering + bewijs ────────────────────────────────────────────────────

    /// <summary>Alles wat de prompt-bouw nodig heeft, in een paar begrensde
    /// queries vooraf i.p.v. per interactie: kaartteksten en keyword-definities
    /// voor de rollen, de regelsectie achter een GOVERNED_BY-anker, de
    /// DERIVED_FROM-bron uit de provenance-Assertion, en het officiële
    /// regelcorpus voor co-occurrence-bewijs (dezelfde bron als de lexicale poort
    /// van de mining — de audit hoort te zien wat de poort zag).</summary>
    private async Task<EvidenceSources> LoadEvidenceSourcesAsync(
        IReadOnlyList<Interaction> selected, CancellationToken ct)
    {
        var subjects = selected
            .Select(x => BrainRef.Interaction(x.Id).Format())
            .ToList();
        var assertionRows = await db.Assertions.AsNoTracking()
            .Where(a => a.FactKind == FactKinds.Interaction && subjects.Contains(a.Subject))
            .Select(a => new { a.Subject, a.DerivedFromRef, a.AssertedAt })
            .ToListAsync(ct);
        var derivedBySubject = assertionRows
            .GroupBy(a => a.Subject, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(a => a.AssertedAt).First().DerivedFromRef,
                StringComparer.Ordinal);

        var cardIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var refText in selected.SelectMany(x => new[] { x.AgentRef, x.PatientRef })
                     .Concat(derivedBySubject.Values))
            if (BrainRef.TryParse(refText, out var r) && r.Kind == BrainRefKind.Card)
                cardIds.Add(r.Key);
        var cards = (await db.Cards.AsNoTracking()
                .Where(c => cardIds.Contains(c.RiftboundId))
                .Select(c => new { c.RiftboundId, c.Name, c.TextPlain })
                .ToListAsync(ct))
            .ToDictionary(c => c.RiftboundId, c => (c.Name, Text: c.TextPlain ?? ""),
                StringComparer.Ordinal);

        var entities = (await db.CanonicalEntities.AsNoTracking()
                .Where(e => e.Status != CanonicalEntityStatus.Merged
                            && (e.Kind == CanonicalEntityKinds.Mechanic
                                || e.Kind == CanonicalEntityKinds.Keyword))
                .Select(e => new { e.CanonicalLabel, e.Definition })
                .ToListAsync(ct))
            .GroupBy(e => e.CanonicalLabel, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Definition, StringComparer.OrdinalIgnoreCase);

        // Officiële secties: voor het GOVERNED_BY-anker én als co-occurrence-bewijs.
        var officialSourceIds = await db.Sources.AsNoTracking()
            .Where(s => s.TrustTier == 1)
            .Select(s => s.Id)
            .ToListAsync(ct);
        var sections = await db.RuleChunks.AsNoTracking()
            .Where(c => c.Text != "" && officialSourceIds.Contains(c.SourceId))
            .OrderBy(c => c.SourceId).ThenBy(c => c.ChunkIndex)
            .Select(c => new SectionText(c.SourceId, c.SectionCode, c.Text))
            .ToListAsync(ct);

        return new EvidenceSources(derivedBySubject, cards, entities, sections);
    }

    private string ComposeAuditText(Interaction interaction, EvidenceSources sources)
    {
        var (agentLabel, agentEvidence) = Describe(interaction.AgentRef, sources);
        var (patientLabel, patientEvidence) = Describe(interaction.PatientRef, sources);

        // De bewering in de brontaal (#187) — dicht op de opslagvorm, zodat het
        // oordeel over het FEIT gaat en niet over onze parafrase.
        var claim = $"{agentLabel} {interaction.Kind} {patientLabel}";

        var conditions = interaction.Conditions
            .Select(c => $"{c.OnKind}={c.Value}"
                + (c.Operator is { Length: > 0 } op ? $" ({op})" : "")
                + (c.SubjectRole is { Length: > 0 } role ? $" [{role}]" : ""))
            .ToList();

        var evidence = new List<InteractionAuditPrompt.EvidenceUnit>();
        if (agentEvidence is not null) evidence.Add(agentEvidence);
        if (patientEvidence is not null) evidence.Add(patientEvidence);

        // Het normatieve anker, als het er is.
        if (interaction.GovernedByRef is { Length: > 0 } governed
            && BrainRef.TryParse(governed, out var gr) && gr.Kind == BrainRefKind.Section)
        {
            var slash = gr.Key.IndexOf('/');
            if (slash > 0)
            {
                var sourceId = gr.Key[..slash];
                var code = gr.Key[(slash + 1)..];
                var section = sources.Sections.FirstOrDefault(s =>
                    s.SourceId == sourceId && s.SectionCode == code);
                if (section is not null)
                    evidence.Add(new($"regels {sourceId} §{code}", section.Text));
            }
        }

        // De DERIVED_FROM-bron (provenance): de kaart of mechaniek waaruit het feit
        // gemined is — vaak de tekst die de lexicale poort als bewijs zag.
        var subject = interaction.Ref.Format();
        if (sources.DerivedBySubject.TryGetValue(subject, out var derivedRef)
            && derivedRef != interaction.AgentRef && derivedRef != interaction.PatientRef)
        {
            var (label, unit) = Describe(derivedRef, sources);
            _ = label;
            if (unit is not null) evidence.Add(unit);
        }

        // Co-occurrence-bewijs uit het officiële corpus: de sectie(s) waarin beide
        // rollen samen voorkomen — dezelfde bron die de mining als bewijs aanbood.
        var agentTerm = RoleTerm(interaction.AgentRef, sources);
        var patientTerm = RoleTerm(interaction.PatientRef, sources);
        if (agentTerm is not null && patientTerm is not null)
            foreach (var s in sources.Sections)
            {
                if (evidence.Count >= InteractionAuditPrompt.MaxEvidenceUnits) break;
                if (!TermMatch.ContainsWord(s.Text, agentTerm)
                    || !TermMatch.ContainsWord(s.Text, patientTerm)) continue;
                var label = s.SectionCode is { Length: > 0 } code
                    ? $"regels {s.SourceId} §{code}"
                    : $"regels {s.SourceId}";
                if (evidence.Any(e => e.Label == label)) continue;
                evidence.Add(new(label, s.Text));
            }

        return InteractionAuditPrompt.Compose(claim, conditions, evidence);
    }

    /// <summary>Label + bewijs-eenheid voor één rol-ref: een kaart draagt haar naam
    /// + kaarttekst, een mechaniek haar label + (indien bekend) officiële definitie.</summary>
    private static (string Label, InteractionAuditPrompt.EvidenceUnit? Evidence) Describe(
        string refText, EvidenceSources sources)
    {
        if (!BrainRef.TryParse(refText, out var r)) return (refText, null);
        if (r.Kind == BrainRefKind.Card && sources.Cards.TryGetValue(r.Key, out var card))
            return ($"card \"{card.Name}\" ({refText})",
                new($"kaarttekst {card.Name}", card.Text));
        if (r.Kind == BrainRefKind.Mechanic)
        {
            var definition = sources.Entities.GetValueOrDefault(r.Key);
            return ($"keyword \"{r.Key}\" ({refText})",
                definition is { Length: > 0 }
                    ? new($"definitie {r.Key}", definition)
                    : null);
        }
        return (refText, null);
    }

    /// <summary>De zoekterm van een rol in het regelcorpus: het keyword-label of de
    /// kaartnaam; null als de ref niet resolvet.</summary>
    private static string? RoleTerm(string refText, EvidenceSources sources)
    {
        if (!BrainRef.TryParse(refText, out var r)) return null;
        if (r.Kind == BrainRefKind.Mechanic) return r.Key;
        if (r.Kind == BrainRefKind.Card && sources.Cards.TryGetValue(r.Key, out var card))
            return card.Name;
        return null;
    }

    private sealed record SectionText(string SourceId, string? SectionCode, string Text);

    private sealed record EvidenceSources(
        IReadOnlyDictionary<string, string> DerivedBySubject,
        IReadOnlyDictionary<string, (string Name, string Text)> Cards,
        IReadOnlyDictionary<string, string?> Entities,
        IReadOnlyList<SectionText> Sections);
}
