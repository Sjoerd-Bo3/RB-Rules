using Microsoft.EntityFrameworkCore;
using RbRules.Domain;
using RbRules.Domain.Ontology;

namespace RbRules.Infrastructure;

/// <summary>Uitkomst van één brein-interactie-mining-run: het aantal verwerkte
/// focus-kaarten, de door de poort gehaalde interacties per tier, en of de per-run-
/// cap geraakt is (drain-signaal, #190).</summary>
public sealed record BreinInteractionMiningResult(
    int FocusCards, int Extracted, int Promoted, int Candidates,
    int Hypothesized, int Rejected, int Failed, bool CapHit,
    int SkippedKnown = 0, string? FailureDetail = null)
{
    public string Summary =>
        $"{FocusCards} kaarten, {Extracted} kandidaten geëxtraheerd → " +
        $"{Promoted} gepromoveerd, {Candidates} kandidaat, {Hypothesized} hypothese, " +
        $"{Rejected} verworpen, {SkippedKnown} al-bekend (kaart↔eigen-keyword), " +
        $"{Failed} rb-ai-uitval" +
        (string.IsNullOrEmpty(FailureDetail) ? "" : $" ({FailureDetail})");
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
/// de promotie-poort — herhaald draaien maakt geen duplicaten.
///
/// Herijkt in #249. De meting op 383 live interacties liet zien dat 69% kaart↔
/// eigen-keyword was — een feit dat al gratis en deterministisch bestaat
/// (<c>GraphSyncService</c> projecteert <c>Card.Mechanics[]</c> als HAS_MECHANIC),
/// terwijl mech↔mech (het échte doel) op 1,3% bleef en 77% geen enkele conditie
/// droeg. De oorzaak zat in de aanbieding én in de poort: we boden vooral een kaart
/// mét haar eigen keywords aan, en de lexicale poort beloonde precies die
/// tautologie (de kaart ís de ene rol; haar keyword staat gebracket in haar eigen
/// tekst). Drie wijzigingen, samen:
/// <list type="number">
/// <item>kaart↔eigen-keyword wordt niet meer geminded (overgeslagen ná parse, vóór
/// promotie) én de promotie-poort weigert het als guard;</item>
/// <item>de aanbieding draagt nu de keywords van de HELE buurt (focus + partners)
/// plus relevante regelsecties als bewijstekst, zodat mech↔mech-interacties
/// überhaupt kunnen ontstaan en een officieel anker kunnen hebben;</item>
/// <item>de lexicale poort eist dat het bewijs een RELATIE uitdrukt: beide rollen
/// verankerd in ÉÉN bewijs-eenheid én minstens één van beide textueel
/// (<see cref="InteractionEvidence"/>) — twee identiteits-ankers tellen niet.</item>
/// </list>
/// De deterministische graph-projectie blijft ONGEMOEID: kaart→mechanic-edges
/// bestaan gewoon door, ze komen alleen niet meer uit een dure LLM-omweg.</summary>
public class BreinInteractionMiningService(
    RbRulesDbContext db, RbAiClient ai, EntityResolutionService entityResolution,
    InteractionPromotionService promotion)
{
    private const int DefaultMaxFocusCards = 40;
    private const int DefaultMaxPartners = 4;

    /// <summary>Maximaal aantal regelsecties dat als bewijstekst meegaat per focus-
    /// kaart (#249). Alleen secties die MINSTENS TWEE aangeboden keyword-labels
    /// noemen: dat is precies waar een keyword↔keyword-relatie officieel staat
    /// opgeschreven, en het houdt de prompt begrensd.</summary>
    private const int MaxRuleSections = 3;

    /// <summary>De prompt-versie-stempel op de <see cref="MiningRun"/> — bump bij een
    /// wijziging aan de extractie-prompt/vorm (stale-conditie voor her-mining, §3.5).
    /// v2 (#249): andere aanbieding (buurt-keywords + regelsecties) en een
    /// aangescherpte systeem-prompt.</summary>
    public const string PromptVersion = "breinmine-interactions-v2";

    public async Task<BreinInteractionMiningResult> RunAsync(
        int maxFocusCards = DefaultMaxFocusCards, int maxPartners = DefaultMaxPartners,
        DateTimeOffset? deadline = null, Action<string>? progress = null, CancellationToken ct = default)
    {
        var windowLexicon = InteractionQualifierLexicon.Windows;
        var statusLexicon = InteractionQualifierLexicon.Statuses;

        // Voortgangs-watermark (#226-review defect 1/2; herzien in #249-review).
        //
        // Het watermark kwam uit de Assertion-provenance (DERIVED_FROM = card:X,
        // FactKind = interaction). Die proxy kan het noodzakelijke onderscheid
        // principieel niet maken: een Assertion ontstaat ALLEEN op het accept-pad, dus
        // elke kaart die niets promoveerde — sinds #249 de meerderheid, want 69% van de
        // live-tabel was kaart↔eigen-keyword en dat wordt nu overgeslagen — liet géén
        // spoor achter. Met OrderBy(RiftboundId).Take(cap) blijft zo'n kaart aan de kop
        // van de wachtrij staan: de gecapte job herkauwt eeuwig dezelfde 40, de nachtrun
        // betaalt elke nacht opnieuw rb-ai-calls, en Drained (!CapHit) blijft permanent
        // false. Dezelfde gaten bestonden al bij Rejected en bij offered.Refs.Count < 2.
        //
        // Nu een EXPLICIETE markering per kaart (Card.InteractionsMinedAt): gezet zodra
        // de extractie GESLAAGD is (rb-ai antwoordde, envelop parseerde), ook zonder
        // promotie — en bewust NIET bij rb-ai-uitval of een kapotte envelop, zodat zo'n
        // kaart juist terugkomt. Het oude Assertion-watermark blijft als achtervang
        // meelopen zodat de al-verwerkte productiekaarten na deploy niet één keer
        // gratis opnieuw gemined worden. Her-minen blijft een expliciete stap (veld
        // leegmaken), zo blijft de abonnement-tokenkost begrensd (#232).
        var minedRefs = await db.Assertions.AsNoTracking()
            .Where(a => a.FactKind == FactKinds.Interaction)
            .Select(a => a.DerivedFromRef)
            .Distinct()
            .ToListAsync(ct);
        var minedIds = minedRefs
            .Select(r => BrainRef.TryParse(r, out var br) && br.Kind == BrainRefKind.Card ? br.Key : null)
            .Where(k => k is not null).Select(k => k!)
            .Distinct().ToList();

        // Focus-kaarten: canonieke printings met tekst die keywords dragen (die tekst
        // ís het bewijsanker), minus de al-verwerkte. Bounded per run; herhaald draaien
        // is idempotent én schuift op.
        var focus = await db.Cards
            .Where(c => c.VariantOf == null && c.Mechanics != null
                        && c.TextPlain != null && c.TextPlain != ""
                        && c.InteractionsMinedAt == null
                        && !minedIds.Contains(c.RiftboundId))
            .OrderBy(c => c.RiftboundId)
            .Take(maxFocusCards + 1)
            .ToListAsync(ct);
        var capHit = focus.Count > maxFocusCards;
        if (capHit) focus = focus.Take(maxFocusCards).ToList();

        if (focus.Count == 0)
            return new(0, 0, 0, 0, 0, 0, 0, false);

        // Partner-buurt één keer laden (versla defect 5): de gedeelde-mechaniek-buurt
        // wordt per focus-kaart in-memory gefilterd i.p.v. de kaarttabel per focus-
        // kaart opnieuw te materialiseren (spiegelt BreinPredicateMiningService).
        var partnerPool = maxPartners > 0
            ? await db.Cards.AsNoTracking()
                .Where(c => c.VariantOf == null && c.Mechanics != null
                            && c.TextPlain != null && c.TextPlain != "")
                .OrderBy(c => c.RiftboundId)
                .Select(c => new CardLite(c.RiftboundId, c.Name, c.Type, c.Mechanics, c.TextPlain))
                .ToListAsync(ct)
            : [];

        // Officiële regeltekst als bewijsbron (#249): één keer geladen, per focus-
        // kaart in-memory gefilterd (zelfde patroon als de partner-pool). Dit is de
        // bron waar keyword↔keyword-relaties daadwerkelijk staan opgeschreven — de
        // kaartteksten alleen leverden vrijwel geen mech↔mech op.
        //
        // Alléén trust-tier-1-bronnen (#249-review): rule_chunks bevat ook de
        // community-gidsen (trust 3), en een parafrase daaruit zou als "bewijszin
        // gevonden" een LLM-voorstel direct promoveren. Dat breekt de kennislagen
        // (officieel > … > community, docs/KNOWLEDGE.md) op precies de plek waar de
        // deterministische steun náást het LLM-verdict moet staan. Zelfde filter als
        // BanErrataSyncService ("officieel wint").
        var officialSourceIds = await db.Sources.AsNoTracking()
            .Where(s => s.TrustTier == 1)
            .Select(s => s.Id)
            .ToListAsync(ct);
        var ruleSections = await db.RuleChunks.AsNoTracking()
            .Where(c => c.Text != "" && officialSourceIds.Contains(c.SourceId))
            .OrderBy(c => c.SourceId).ThenBy(c => c.ChunkIndex)
            .Select(c => new SectionLite(c.SourceId, c.SectionCode, c.Text))
            .ToListAsync(ct);

        var run = await StartRunAsync(windowLexicon, statusLexicon, ct);

        int extracted = 0, promoted = 0, candidates = 0, hypothesized = 0, rejected = 0, failed = 0;
        var skippedKnown = 0;
        var aiTally = new AiOutcomeTally();   // uitval per oorzaak (#251)
        var processed = 0;
        var deadlineHit = false;
        foreach (var card in focus)
        {
            // Nachtrun-deadline (#245): stop netjes op venster-einde — de al verwerkte
            // kaarten dragen hun watermark, de rest volgt de volgende nacht.
            if (deadline is { } dl && DateTimeOffset.UtcNow >= dl) { deadlineHit = true; break; }
            processed++;
            progress?.Invoke($"interacties extraheren via rb-ai: {processed}/{focus.Count}");

            var offered = await BuildOfferedRefsAsync(card, partnerPool, ruleSections, maxPartners, ct);
            if (offered.Refs.Count < 2)
            {
                // Niets zinnigs om over te redeneren — en dat is een DETERMINISTISCHE
                // uitkomst, geen uitval: opnieuw aanbieden levert opnieuw niets. Wel
                // markeren, anders blijft deze kaart de wachtrij-kop bezetten (#249-review).
                MarkMined(card, run.Id);
                continue;
            }

            var vocab = new ExtractionVocab(
                offered.Refs.Select(r => new OfferedRef(r.Ref, r.Label, r.Type)).ToList(),
                windowLexicon, statusLexicon);

            var call = await ai.ExtractStructuredDetailedAsync(
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

            if (call.Raw is null)
            {
                // Degradatie: rb-ai weg → geen half feit, sla deze kaart over. De
                // OORZAAK wordt wel geteld (#251) zodat het run-detail laat zien of
                // dit rate-limits, timeouts of onleesbare antwoorden waren.
                failed++;
                aiTally.Add(call.Outcome);
                continue;
            }

            var byRef = offered.Refs.ToDictionary(r => r.Ref, StringComparer.Ordinal);
            var parsed = InteractionExtraction.ParseDetailed(call.Raw, vocab);
            if (parsed.Malformed)
            {
                // HTTP 200 met een afgekapte of schema-vreemde body (bv.
                // {"interactions":"none"}) is UITVAL, geen leeg resultaat (#251-review):
                // stil tot [] reduceren telde parse-fouten als geslaagd werk en maakte
                // de uitvalmeting blind. Géén watermark: deze kaart moet terugkomen.
                failed++;
                aiTally.Add(AiCallOutcome.Unparseable);
                continue;
            }
            // Geldig antwoord zonder kandidaten is geslaagd werk, geen uitval —
            // apart geteld zodat "rb-ai gaf niets" en "rb-ai wist niets" niet meer
            // op één hoop belanden.
            aiTally.Add(parsed.Items.Count == 0 ? AiCallOutcome.Empty : AiCallOutcome.Ok);

            // Vanaf hier is de extractie GESLAAGD (#249-review): het watermark hoort bij
            // "deze kaart is aangeboden en beantwoord", niet bij "deze kaart leverde een
            // feit op". Zetten vóór de promotie-lus, zodat ook een kaart die uitsluitend
            // tautologieën of verwerpingen oplevert de wachtrij verlaat.
            MarkMined(card, run.Id);

            foreach (var ix in parsed.Items)
            {
                if (!byRef.TryGetValue(ix.FromRef, out var from)
                    || !byRef.TryGetValue(ix.ToRef, out var to))
                    continue; // buiten de aangeboden set — parse gate't dit al, dubbel slot

                // Tautologie-poort (#249): kaart↔eigen-keyword is al deterministisch
                // bekend (HAS_MECHANIC uit Card.Mechanics, GraphSyncService) — niet
                // minen, niet promoveren, en ook niet als "geëxtraheerd" tellen: het
                // is geen kandidaat maar herkauwde kennis. Apart geteld zodat de
                // cockpit ziet hoe vaak het model er nog naartoe trekt.
                if (InteractionTautology.IsCardOwnKeywordPair(from.Ref, to.Ref, offered.OwnKeywordRefs))
                {
                    skippedKnown++;
                    continue;
                }
                extracted++;

                var conditions = ix.Conditions
                    .Select(c => new InteractionConditionInput(c.OnKind, c.SubjectRole, c.Value, c.Operator))
                    .ToList();

                // Lexicale steun (§3.4, versla defect 3/4; verscherpt in #249):
                // bestaat er ÉÉN bewijs-eenheid — een aangeboden kaart óf een
                // regelsectie — die een RELATIE tussen beide rollen uitdrukt? Beide
                // rollen moeten er verankerd zijn (co-occurrence binnen één eenheid,
                // geen cross-card-toeval) én minstens één van beide TEXTUEEL: een
                // kaart die alleen zichzelf verankert bewijst niets over een relatie.
                var lexical = offered.Evidence.Any(unit => InteractionEvidence.ExpressesRelation(
                    Anchor(unit, from), Anchor(unit, to)));

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

        // FocusCards = daadwerkelijk verwerkt (bij een deadline-stop < focus.Count).
        // CapHit ⇔ er blijft vers werk liggen: cap geraakt óf deadline afgekapt.
        return new(processed, extracted, promoted, candidates, hypothesized, rejected, failed,
            capHit || deadlineHit, skippedKnown,
            aiTally.Summary is { Length: > 0 } detail ? detail : null);
    }

    /// <summary>De aangeboden refs (enum-vocabulaire) + de bewijs-eenheden voor één
    /// focus-kaart. Herzien in #249 om mech↔mech mogelijk te maken:
    /// <list type="bullet">
    /// <item>de focus-kaart en partner-kaarten die ≥1 mechaniek delen (bounded, uit
    /// de vooraf geladen <paramref name="partnerPool"/>) — de card-rollen;</item>
    /// <item>de entity-geresolvete keyword-refs van de HELE buurt (focus + partners),
    /// niet alleen van de focus-kaart. Alleen focus-keywords aanbieden maakte
    /// kaart↔eigen-keyword vrijwel de enige mogelijke uitkomst; met de buurt-
    /// keywords ontstaan er überhaupt keyword-PAREN om over te redeneren;</item>
    /// <item>regelsecties die minstens twee aangeboden keyword-labels noemen, als
    /// BEWIJSTEKST (niet als rol — de HAS_ROLE-range is Card/Keyword, een
    /// RuleSection kan geen agent/patient zijn). Dáár staat een keyword↔keyword-
    /// relatie officieel opgeschreven.</item>
    /// </list>
    /// Entity-resolutie (fase 1) draait VÓÓR de refs ontstaan zodat synoniem-
    /// varianten ("Deflect"/"Deflecting") op één ref landen (versla #2). De
    /// bewijsteksten blijven per eenheid gescheiden zodat de lexicale poort
    /// co-occurrence binnen ÉÉN eenheid eist, niet over de samengevoegde tekst
    /// (versla defect 3). <see cref="OfferedSet.OwnKeywordRefs"/> legt per kaart-ref
    /// vast welke keyword-refs haar EIGEN mechanics zijn — de tautologie-poort.</summary>
    private async Task<OfferedSet> BuildOfferedRefsAsync(
        Card card, IReadOnlyList<CardLite> partnerPool, IReadOnlyList<SectionLite> ruleSections,
        int maxPartners, CancellationToken ct)
    {
        var refs = new List<OfferedRefRow>();
        var promptParts = new List<string>();       // met ref-headers, naar rb-ai
        var evidence = new List<EvidenceUnit>();    // rauwe tekst per eenheid, lexicale poort
        var ownKeywords = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        // Focus-kaart.
        var focusRef = BrainRef.Card(card.RiftboundId).Format();
        refs.Add(new(focusRef, card.Name, CardEntityType(card.Type)));
        promptParts.Add($"[{focusRef} — {card.Name}] {card.TextPlain}");
        evidence.Add(new(focusRef, card.TextPlain ?? ""));

        var mechanics = card.Mechanics ?? [];

        // Keyword-refs, entity-geresolvet (canoniek label → één ref). Per kaart
        // onthouden welke refs HAAR eigen mechanics zijn (tautologie-poort, #249).
        var seenKeyword = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task<IReadOnlySet<string>> AddKeywordsAsync(IEnumerable<string> surfaces)
        {
            var own = new HashSet<string>(StringComparer.Ordinal);
            foreach (var surface in surfaces.Where(m => !string.IsNullOrWhiteSpace(m)))
            {
                var label = await ResolveKeywordLabelAsync(surface, ct);
                if (label.Length == 0) continue;
                var kwRef = BrainRef.Mechanic(label).Format();
                own.Add(kwRef);
                if (seenKeyword.Add(label))
                    refs.Add(new(kwRef, label, EntityType.Keyword));
            }
            return own;
        }

        ownKeywords[focusRef] = await AddKeywordsAsync(mechanics);

        // Partner-kaarten die een mechaniek delen (deterministische kandidaat-buurt) —
        // in-memory uit de vooraf geladen pool (versla defect 5). Hun keywords komen
        // óók in de aanbieding: dat maakt kruisverbanden (mech↔mech, kaart↔ANDERS
        // kaarts keyword) mogelijk in plaats van alleen de eigen-keyword-tautologie.
        if (maxPartners > 0 && mechanics.Length > 0)
        {
            var mechSet = mechanics.Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var picked = 0;
            foreach (var p in partnerPool)
            {
                if (picked >= maxPartners) break;
                if (string.Equals(p.RiftboundId, card.RiftboundId, StringComparison.Ordinal)) continue;
                if (!(p.Mechanics ?? []).Any(m => mechSet.Contains(m.Trim()))) continue;
                var pRef = BrainRef.Card(p.RiftboundId).Format();
                refs.Add(new(pRef, p.Name, CardEntityType(p.Type)));
                promptParts.Add($"[{pRef} — {p.Name}] {p.TextPlain}");
                evidence.Add(new(pRef, p.TextPlain ?? ""));
                ownKeywords[pRef] = await AddKeywordsAsync(p.Mechanics ?? []);
                picked++;
            }
        }

        // Regelsecties als bewijs (#249): alleen secties die ≥2 aangeboden keyword-
        // labels noemen — daar staat een relatie tussen twee keywords officieel
        // beschreven. Ze zijn GEEN offered ref (een RuleSection is geen geldige
        // HAS_ROLE-filler), alleen tekst waarop de lexicale poort mag steunen.
        var keywordLabels = refs.Where(r => r.Type == EntityType.Keyword)
            .Select(r => r.Label).ToList();
        if (keywordLabels.Count >= 2)
        {
            var pickedSections = 0;
            var coveredPairs = new HashSet<(string, string)>();
            foreach (var s in ruleSections)
            {
                if (pickedSections >= MaxRuleSections) break;
                var hits = keywordLabels.Where(l => TextContains(s.Text, l)).ToList();
                if (hits.Count < 2) continue;

                // Begrotings-diversiteit (#249-review): neem een sectie alleen als ze
                // MINSTENS ÉÉN nog niet gedekt label-PAAR toevoegt. Zonder die eis vulden
                // drie vroege secties die toevallig dezelfde twee labels noemen de hele
                // MaxRuleSections-begroting, en werd de sectie die K5↔K6 daadwerkelijk
                // beschrijft nooit geladen — de poort-uitslag hing dan aan de
                // corpusvolgorde (SourceId/ChunkIndex) in plaats van aan het bewijs.
                var pairs = LabelPairs(hits).ToList();
                if (pairs.All(coveredPairs.Contains)) continue;
                coveredPairs.UnionWith(pairs);

                var label = s.SectionCode is { Length: > 0 } code
                    ? $"{s.SourceId} §{code}" : s.SourceId;
                promptParts.Add($"[regels {label}] {s.Text}");
                evidence.Add(new(CardRef: null, s.Text));
                pickedSections++;
            }
        }

        // Prompt-tekst draagt de ref-headers zodat het model refs↔kaarten mapt; de
        // evidence-teksten zijn de RAUWE bron-teksten (per eenheid) — de lexicale poort
        // mag niet triviaal slagen op een label dat we zelf in een header plakten.
        return new(refs, string.Join("\n", promptParts), evidence, ownKeywords);
    }

    /// <summary>Alle ongeordende label-paren uit één regelsectie — de eenheid waarin de
    /// begrotings-diversiteit gemeten wordt (#249-review). Ordinaal genormaliseerd zodat
    /// (K1,K2) en (K2,K1) hetzelfde paar zijn.</summary>
    private static IEnumerable<(string, string)> LabelPairs(IReadOnlyList<string> labels)
    {
        for (var i = 0; i < labels.Count; i++)
            for (var j = i + 1; j < labels.Count; j++)
                yield return string.CompareOrdinal(labels[i], labels[j]) <= 0
                    ? (labels[i], labels[j])
                    : (labels[j], labels[i]);
    }

    /// <summary>Zet het voortgangs-watermark op een verwerkte focus-kaart (#249-review).
    /// Alleen aanroepen wanneer de kaart deterministisch klaar is: extractie geslaagd
    /// (ongeacht de poort-uitslag) of niets zinnigs om aan te bieden. NOOIT bij
    /// rb-ai-uitval of een kapotte envelop — die kaart hoort de volgende run terug te
    /// komen. De SaveChanges gebeurt aan het einde van de run (of eerder, meeliftend op
    /// de promotie-transactie); de kaart is getrackt.</summary>
    private static void MarkMined(Card card, string runId)
    {
        card.InteractionsMinedAt = DateTimeOffset.UtcNow;
        card.InteractionsMinedByRunId = runId;
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
    private static EntityType CardEntityType(string? type) =>
        type is { } t && OntologySchema.ParseEntityType(t) is { } et
            && OntologySchema.IsA(et, EntityType.Card)
            ? et : EntityType.Card;

    /// <summary>Hoe sterk verankert bewijs-eenheid <paramref name="unit"/> rol
    /// <paramref name="role"/> (versla defect 3/4; #249)? <see cref="EvidenceAnchor.Identity"/>
    /// wanneer de eenheid de rol ZÉLF is (een card-rol draagt zijn bewijs in zijn eigen
    /// tekst — de kaartnaam hoeft er niet in te staan), <see cref="EvidenceAnchor.Textual"/>
    /// wanneer het rol-label letterlijk in díe tekst voorkomt (co-occurrence binnen ÉÉN
    /// eenheid, geen cross-card-toeval), anders <see cref="EvidenceAnchor.None"/>.
    /// Het onderscheid identity/textueel is wat #249 nodig had: alleen een textueel
    /// anker kan bewijs voor een RELATIE zijn.</summary>
    private static EvidenceAnchor Anchor(EvidenceUnit unit, OfferedRefRow role) =>
        string.Equals(unit.CardRef, role.Ref, StringComparison.Ordinal)
            ? EvidenceAnchor.Identity
            : TextContains(unit.Text, role.Label)
                ? EvidenceAnchor.Textual
                : EvidenceAnchor.None;

    /// <summary>Woordgrens-bewuste term-treffer (#249-review): een kale substring-match
    /// liet generieke keywords ("Tank", "Hidden", "Equip") op gewoon regelproza vallen en
    /// leverde zo valse lexicale steun voor de promotie-poort. Gebracket ("[Assault 2]")
    /// en meerwoordstermen ("Reaction Window") blijven werken.</summary>
    private static bool TextContains(string text, string term) =>
        TermMatch.ContainsWord(text, term);

    private sealed record OfferedRefRow(string Ref, string Label, EntityType Type);

    /// <summary>Eén bewijs-eenheid voor de lexicale poort: een kaarttekst
    /// (<paramref name="CardRef"/> gezet — die kaart is dan zijn eigen identiteits-
    /// anker) of een regelsectie (<paramref name="CardRef"/> null, alleen tekst).</summary>
    private sealed record EvidenceUnit(string? CardRef, string Text);

    private sealed record CardLite(
        string RiftboundId, string Name, string? Type, string[]? Mechanics, string? TextPlain);
    private sealed record SectionLite(string SourceId, string? SectionCode, string Text);
    private sealed record OfferedSet(
        IReadOnlyList<OfferedRefRow> Refs, string PromptText,
        IReadOnlyList<EvidenceUnit> Evidence,
        IReadOnlyDictionary<string, IReadOnlySet<string>> OwnKeywordRefs);
}
