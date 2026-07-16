using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public enum ReevaluateOutcome { Verified, StillPending, NotFound }

public record ReevaluateResult(ReevaluateOutcome Outcome, string? Reason);

/// <summary>Resultaat van de anker-herstel-pas (#188 increment 3, <see
/// cref="CorrectionReevaluationService.RepairPendingAnchorsAsync"/>).
/// <paramref name="Repaired"/> = auto-geverifieerd (lexicale steun + volle
/// poort); <paramref name="Recommended"/> (review-fix) = anker wél verplaatst
/// maar als AANBEVELING pending gelaten (geen lexicale steun — de beheerder
/// one-click-verifieert via het bestaande /verify-pad); <paramref
/// name="Skipped"/> = overgeslagen/onopgelost (AI-uitval, "none",
/// niet-resolvend, bezet anker of poort-faal). <paramref name="CapHit"/>
/// volgt hetzelfde #190-contract als <see
/// cref="ClarificationMineResult.CapHit"/>: machine-leesbaar of er ná deze
/// run nog vers werk (meer ECHT-eligible kandidaten dan de cap) op de bank
/// lag, zodat het admin-jobpad (JobCatalog.ClarifyAsync) hierop kan
/// draineren.</summary>
public record AnchorRepairResult(
    int Repaired, int Recommended, int Skipped, string Message, bool CapHit = false);

/// <summary>Her-evaluatie van één Correction (#184), op initiatief van de
/// beheerder vanuit de reviewqueue: een opmerking wordt bewaard
/// (<see cref="Correction.ReviewNote"/>, traceerbaar) en triggert meteen een
/// her-toets van de hybride autoriteitspoort (#177/#185/#188) voor dít ene
/// item — ROEPT de bestaande domeinlogica aan
/// (<see cref="ClarificationGrounding.IsGrounded"/>,
/// <see cref="ClaimTopicMapper.Resolve"/>), wijzigt die logica zelf niet.
///
/// <b>Informativiteit (#188):</b> er is hier geen verse extractie (geen
/// <see cref="ExtractedClarification.Operative"/> om op terug te vallen) —
/// deze service her-toetst opgeslagen tekst, dus draait zelf een lichte
/// LLM-classificatie (<see cref="ClarificationInformativeness.JudgeSystemPrompt"/>/
/// <see cref="ClarificationInformativeness.ParseOperative"/>) via <see
/// cref="RbAiClient"/>. AI-uitval of onbruikbare output (null) degradeert
/// naar de deterministische <see cref="ClarificationInformativeness.IsMetaOnly"/>-
/// heuristiek — nooit een harde 500 (docs/CLAUDE.md: "AI-uitval = verwacht
/// pad").
///
/// Een opmerking mag een anker-correctie bevatten
/// (<see cref="ReviewNoteAnchor"/>, bv. "mechanic:Recall") die het
/// oorspronkelijke Scope/Ref overschrijft — zo kan een fout-aangeankerd of
/// onherkend onderwerp alsnog verified worden zonder de LLM-extractie
/// opnieuw te draaien. Grounding en informativiteit toetsen altijd de
/// bestaande Text (het citaat/de verduidelijking veranderen niet mee met een
/// anker-correctie; alleen het onderwerp doet dat).
///
/// Alleen van toepassing op clarify-mining-Corrections (Provenance
/// "<see cref="ClarificationMiningService.ProvenancePrefix"/>{sourceId}"):
/// dat is de enige ontstaanswijze met een op-zichzelf-staande, gate-gehouden
/// "unverified" status én een brontekst om tegen te gronden — een
/// chat-ruling (<see cref="ChatRulingService"/>) of review-notitie-promotie
/// (<see cref="ReviewNoteService"/>) wordt altijd direct verified aangemaakt.
/// Voor een niet-clarify-mining Correction bewaart her-evaluatie alleen de
/// opmerking, zonder de poort te draaien (er is geen brontekst om tegen te
/// gronden). Een afgewezen (rejected) Correction blijft een tombstone
/// (#177-conventie): de opmerking wordt bewaard, maar her-evalueert niet
/// stiekem een menselijke afwijzing — expliciet opnieuw verifiëren kan nog
/// altijd via het bestaande /verify-pad. Een al geverifieerde Correction
/// degradeert nooit (zelfde no-demote-invariant als
/// <see cref="ClarificationMiningService"/>.StoreAsync): her-evaluatie is
/// alleen zinvol op pending items, dus daar bewaart de actie alleen de
/// opmerking.
///
/// <b>Anker-herstel-pas (#188 increment 3):</b> <see
/// cref="RepairPendingAnchorsAsync"/> is de geautomatiseerde tegenhanger van
/// het handmatige <see cref="ReviewNoteAnchor"/>-pad hierboven — voor de
/// grote bestaande achterstand (issue #199: 117 van de 133 pending items
/// falen op anker-resolutie) laat de LLM zelf een anker KIEZEN uit het echte
/// vocabulaire in plaats van dat een beheerder ze één voor één met een
/// opmerking corrigeert. Beide paden delen dezelfde poort-hertoets (<see
/// cref="ApplyGateAsync"/>) — geen dubbele logica.</summary>
public class CorrectionReevaluationService(RbRulesDbContext db, RbAiClient ai)
{
    /// <summary>Cap per herstel-pas-run (#188 increment 3) — zelfde
    /// "niet alles in één keer"-afweging als de andere gecapte mining-jobs
    /// (ClaimMiningService/ClarificationMiningService: #58/#119-stijl).</summary>
    public const int DefaultRepairCap = 40;

    /// <summary>Terminaliteits-marker (review-fix, findings 2+6): een
    /// DEFINITIEVE herstel-pas-uitkomst ({"none": true} of een keuze die niet
    /// resolvet) plakt deze marker aan de bestaande StatusReason; het
    /// selectie-predicaat sluit gemarkeerde items uit, anders bleef élk
    /// onopgelost item eeuwig her-eligible en verbrandde het elke run
    /// cap-ruimte (window-starvation) mét telkens een nieuwe
    /// niet-deterministische kans op een fout anker (ratchet). Een TRANSIËNTE
    /// fout (AI-uitval, onbruikbare output) markeert bewust NIET — de
    /// volgende run mag het opnieuw proberen. Een latere her-mine die het
    /// item bijwerkt overschrijft StatusReason via de normale poort
    /// (<see cref="ClarificationMiningService.GateReason"/>, zonder marker)
    /// en maakt het item vanzelf weer eligible — dát is het beoogde
    /// herstel-na-nieuwe-informatie-pad.</summary>
    public const string TerminalMarker = "anker-herstel geprobeerd";

    public async Task<ReevaluateResult> ReevaluateAsync(
        long id, string? note, CancellationToken ct = default)
    {
        var c = await db.Corrections.FindAsync([id], ct);
        if (c is null) return new(ReevaluateOutcome.NotFound, null);

        var cleanNote = Clean(note);
        if (cleanNote is not null) c.ReviewNote = cleanNote;

        if (c.Status == "rejected")
        {
            await db.SaveChangesAsync(ct);
            return new(ReevaluateOutcome.StillPending,
                "afgewezen — opmerking bewaard, geen her-evaluatie op een afgewezen item");
        }

        if (c.Status == "verified")
        {
            // Nooit degraderen (zelfde invariant als ClarificationMiningService.
            // StoreAsync): een al geverifieerde ruling blijft verified — her-
            // evaluatie is hier alleen relevant voor pending items, de
            // opmerking wordt wel bewaard.
            await db.SaveChangesAsync(ct);
            return new(ReevaluateOutcome.Verified, null);
        }

        if (c.Provenance is null || !c.Provenance.StartsWith(ClarificationMiningService.ProvenancePrefix))
        {
            // Geen clarify-mining-oorsprong: geen brontekst om de hybride
            // poort tegen te draaien. De opmerking is bewaard; status blijft
            // ongemoeid (handmatig/chat-ruling-correcties zijn al verified).
            await db.SaveChangesAsync(ct);
            return new(ReevaluateOutcome.StillPending,
                "geen clarify-mining-oorsprong — alleen opmerking bewaard, poort niet van toepassing");
        }

        var anchorOverride = ReviewNoteAnchor.TryParse(c.ReviewNote);
        var anchors = await AnchorResolverFactory.BuildAsync(db, ct);
        return await ApplyGateAsync(c, anchorOverride, anchors, ct);
    }

    /// <summary>Gedeelde poort-hertoets (#177/#185/#188), geëxtraheerd uit
    /// <see cref="ReevaluateAsync"/> (#188 increment 3) zodat <see
    /// cref="RepairPendingAnchorsAsync"/> exact dezelfde logica hergebruikt
    /// in plaats van te dupliceren — alleen de HERKOMST van
    /// <paramref name="anchorOverride"/> verschilt (een handmatige
    /// <see cref="ReviewNoteAnchor"/>-opmerking vs. een LLM-anker-keuze uit
    /// <see cref="ClarificationAnchorRepair"/>).
    ///
    /// Review-fix (findings 4+7): dit gedeelde pad doet bewust GEEN
    /// duplicaat-/collision-check — een handmatige #184-anker-correctie is
    /// een bewuste menselijke verplaatsing die altijd mag (het
    /// #184-spookduplicaat is daar al gedekt door de cross-bucket-redding op
    /// ReviewNote in <see cref="ClarificationMiningService.StoreAsync"/>).
    /// Alleen de geautomatiseerde herstel-pas bewaakt duplicaten, canoniek
    /// (BrainRef-vergelijking), vóór hij dit pad aanroept.</summary>
    private async Task<ReevaluateResult> ApplyGateAsync(
        Correction c, (string TopicType, string TopicRef)? anchorOverride,
        ClaimTopicMapper anchors, CancellationToken ct)
    {
        var topicType = anchorOverride?.TopicType ?? TopicTypeFor(c.Scope);
        var topicRef = anchorOverride?.TopicRef ?? c.Ref;
        var anchored = anchors.Resolve(topicType, topicRef) is not null;

        var docContent = await LoadDocumentContentAsync(c.Provenance!, ct);
        var quote = ClarificationMiningService.ExtractQuote(c.Text);
        var grounded = ClarificationGrounding.IsGrounded(quote, docContent);
        var informative = await JudgeInformativeAsync(
            ClarificationMiningService.ClarificationOf(c.Text), ct);
        var verifies = grounded && anchored && informative;

        // Anker-correctie toepassen zodra hij ook echt resolvet — een niet-
        // herkend voorgesteld anker verandert niets (blijft het oorspronkelijke
        // onderwerp, "anchored" is dan al false en de reden zegt waarom).
        if (anchorOverride is not null && anchored)
        {
            c.Scope = ClarificationMiningService.ScopeFor(anchorOverride.Value.TopicType);
            c.Ref = anchorOverride.Value.TopicRef;
        }

        if (verifies)
        {
            c.Status = "verified";
            c.StatusReason = null;
            c.VerifiedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return new(ReevaluateOutcome.Verified, null);
        }

        c.Status = "unverified";
        c.StatusReason = ClarificationMiningService.GateReason(
            grounded, anchored, informative, quote, topicType, topicRef);
        c.VerifiedAt = null;
        await db.SaveChangesAsync(ct);
        return new(ReevaluateOutcome.StillPending, c.StatusReason);
    }

    /// <summary>Anker-herstel-pas (#188 increment 3, herzien na de
    /// adversariële review): productiedata (issue #199, comment 2026-07-16)
    /// toont dat 117 van de 133 pending clarify-corrections falen op
    /// anker-resolutie — de extractie koos een vrije-vorm-onderwerp
    /// ("battlefield control without units") dat niet in het vocabulaire
    /// voorkomt, terwijl de inhoud zelf vaak gegrond en informatief is. Per
    /// item doet één LLM-KEUZE uit het echte vocabulaire (<see
    /// cref="ClarificationAnchorRepair"/>, met citaat + oorspronkelijk
    /// onderwerp als context) een voorstel; wat er daarna gebeurt is
    /// volledig deterministisch:
    ///
    /// <b>Autoriteitsmodel (review-fix, kernbevinding):</b> auto-verificatie
    /// is alleen verdedigbaar als het anker AANTOONBAAR het onderwerp is —
    /// een resolvend anker bewijst alleen dat de term bestaat, niet dat hij
    /// bij deze tekst hoort. Daarom promoot deze pas alleen bij LEXICALE
    /// STEUN (<see cref="ClarificationAnchorRepair.HasLexicalSupport"/>: de
    /// ankerterm komt voor in verduidelijking + citaat + het oorspronkelijke
    /// vrije-vorm-onderwerp), en dan nog via de volledige poort (<see
    /// cref="ApplyGateAsync"/>: grounded + informative). Zonder lexicale
    /// steun wordt het item een AANBEVELING (het #199-principe: machine
    /// sorteert voor, mens klikt): Scope/Ref verhuizen wél naar het
    /// resolvende anker — de queue toont het item dan bij het juiste
    /// onderwerp — maar de status blijft pending met de reden "wacht op
    /// review"; de beheerder one-click-verifieert via het bestaande
    /// /verify-pad.
    ///
    /// <b>Terminaliteit (review-fix, findings 2+6):</b> een definitieve
    /// uitkomst ({"none": true} of een niet-resolvende keuze) plakt <see
    /// cref="TerminalMarker"/> aan de StatusReason zodat het item niet elke
    /// run opnieuw cap-ruimte verbrandt; AI-uitval en onbruikbare output
    /// zijn transiënt (geen marker, volgende run opnieuw). De aanbevelings-
    /// en poort-faal-uitkomsten zijn vanzelf terminaal: hun nieuwe
    /// StatusReason bevat "niet herkend" niet meer.
    ///
    /// <b>Duplicaat-bewaking (review-fix, findings 3+5, alléén dit
    /// geautomatiseerde pad — de handmatige #184-route mag altijd
    /// verplaatsen):</b> vóór elke verplaatsing wordt CANONIEK (BrainRef-
    /// vergelijking via <see cref="ClaimTopicMapper.Resolve"/>, zodat
    /// aliassen — kaartvarianten, concept-key vs. -titel — niet langs elkaar
    /// heen matchen) gecheckt of een ándere Correction van dezelfde bron dat
    /// anker al bezet. Zo ja: het item is een DUPLICAAT-KANDIDAAT en krijgt
    /// die terminale reden — niet verplaatst, niet geverifieerd, beoordeel
    /// handmatig. Achtergrond: deze pas zet bewust geen <see
    /// cref="Correction.ReviewNote"/> (dat zou een geautomatiseerde keuze als
    /// mens-beoordeeld labelen), dus de ReviewNote-gebaseerde cross-bucket-
    /// redding in <see cref="ClarificationMiningService.StoreAsync"/> ziet
    /// een door deze pas verplaatst item niet — zonder deze check zou een
    /// her-mine die het oude vrije-vorm-onderwerp opnieuw extraheert via een
    /// volgende herstel-run een tweede ruling op hetzelfde anker opleveren.
    ///
    /// Kandidaten: pending clarify-mining-Corrections met StatusReason
    /// "onderwerp … niet herkend", zonder <see cref="TerminalMarker"/> en
    /// zonder <see cref="Correction.ReviewNote"/> (#184: beheerder-eigendom
    /// blijft onaangeraakt). Cap per run (<see cref="DefaultRepairCap"/>) +
    /// <see cref="AnchorRepairResult.CapHit"/> over de ECHT-eligible teller,
    /// zodat het #190-drain-pad (JobCatalog.ClarifyAsync) de rest oppakt en
    /// terminale items geen vals "vers werk" melden.</summary>
    public async Task<AnchorRepairResult> RepairPendingAnchorsAsync(
        int maxItems = DefaultRepairCap, Action<string>? progress = null, CancellationToken ct = default)
    {
        maxItems = Math.Clamp(maxItems, 1, 300);

        var query = db.Corrections.Where(c =>
            c.Status == "unverified"
            && c.ReviewNote == null
            && c.Provenance != null && c.Provenance.StartsWith(ClarificationMiningService.ProvenancePrefix)
            && c.StatusReason != null && c.StatusReason.Contains("niet herkend")
            && !c.StatusReason.Contains(TerminalMarker));

        var totalEligible = await query.CountAsync(ct);
        if (totalEligible == 0) return new(0, 0, 0, "geen anker-herstel nodig", CapHit: false);

        var candidates = await query.OrderBy(c => c.Id).Take(maxItems).ToListAsync(ct);
        var capHit = totalEligible > candidates.Count;

        var (anchors, mechanics, concepts) = await AnchorResolverFactory.BuildWithVocabularyAsync(db, ct);
        var systemPrompt = ClarificationAnchorRepair.GetSystemPrompt(mechanics, concepts);

        var repaired = 0;
        var recommended = 0;
        var skipped = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            progress?.Invoke($"anker-herstel {i + 1}/{candidates.Count}");

            var clarification = ClarificationMiningService.ClarificationOf(c.Text);
            var quote = ClarificationMiningService.ExtractQuote(c.Text);

            string? raw;
            try
            {
                raw = await ai.AskAsync(
                    ClarificationAnchorRepair.BuildPrompt(clarification, quote, c.Ref),
                    systemPrompt, ct: ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Zelfde scout-timeoutpatroon als ClarificationMiningService.
                // AskSafeAsync/JudgeInformativeAsync: een HttpClient-timeout
                // telt als AI-uitval, niet als crash van de herstel-pas.
                raw = null;
            }

            var choice = raw is null
                ? new AnchorChoice(AnchorChoiceKind.Unusable)
                : ClarificationAnchorRepair.ParseAnchorChoice(raw);

            if (choice.Kind == AnchorChoiceKind.Unusable)
            {
                // Transiënt (AI-uitval of flaky output): item blijft
                // ongemarkeerd staan — de volgende run probeert opnieuw.
                skipped++;
                continue;
            }

            var resolved = choice.Kind == AnchorChoiceKind.Choice
                ? anchors.Resolve(choice.TopicType, choice.TopicRef)
                : null;
            if (resolved is null)
            {
                // Definitief: expliciete {"none": true} of een keuze die het
                // vocabulaire toch niet haalt — terminaal markeren zodat het
                // item niet elke run opnieuw cap-ruimte verbrandt. Een
                // her-mine die het item bijwerkt, schrijft een verse
                // StatusReason (zonder marker) en her-opent de eligibility.
                c.StatusReason += $" — {TerminalMarker}, geen vocabulaire-match (#188)";
                await db.SaveChangesAsync(ct);
                skipped++;
                continue;
            }

            // Canonieke duplicaat-check (findings 3+5): resolve óók de
            // bezetters en vergelijk BrainRef.Format() — een alias (variant-
            // kaartnaam, concept-key vs. -titel) matcht letterlijk niet maar
            // wijst wel naar dezelfde knoop.
            var chosenFormat = resolved.Value.Format();
            var siblings = await db.Corrections.AsNoTracking()
                .Where(x => x.Id != c.Id && x.Provenance == c.Provenance)
                .Select(x => new { x.Scope, x.Ref })
                .ToListAsync(ct);
            var occupied = siblings.Any(s =>
                anchors.Resolve(TopicTypeFor(s.Scope), s.Ref) is { } sib
                && sib.Format() == chosenFormat);
            if (occupied)
            {
                c.StatusReason = $"anker '{choice.TopicRef}' is al bezet door een bestaande "
                    + "ruling van deze bron — mogelijk duplicaat, beoordeel handmatig (#188)";
                await db.SaveChangesAsync(ct);
                skipped++;
                continue;
            }

            // Lexicale-steun-poort (kernbevinding): promoot alleen als de
            // ankerterm aantoonbaar in de itemtekst voorkomt; voor een
            // concept tellen ook de canonieke key en titel als term.
            var supportTerms = new List<string> { choice.TopicRef! };
            if (choice.TopicType == "concept")
            {
                var key = resolved.Value.Key;
                supportTerms.Add(key);
                foreach (var (conceptKey, title) in concepts)
                    if (conceptKey == key) { supportTerms.Add(title); break; }
            }
            var haystack = string.Join('\n', clarification, quote ?? "", c.Ref);
            if (!ClarificationAnchorRepair.HasLexicalSupport(choice.TopicType!, supportTerms, haystack))
            {
                // Aanbeveling (geen promotie): Scope/Ref wél verplaatsen zodat
                // de reviewqueue het item bij het juiste onderwerp toont; de
                // beheerder verifieert via het bestaande /verify-pad.
                c.Scope = ClarificationMiningService.ScopeFor(choice.TopicType!);
                c.Ref = choice.TopicRef!;
                c.Status = "unverified";
                c.StatusReason = $"anker hersteld via LLM-suggestie ('{choice.TopicType}:{choice.TopicRef}') "
                    + "— wacht op review (#188)";
                c.VerifiedAt = null;
                await db.SaveChangesAsync(ct);
                recommended++;
                continue;
            }

            var result = await ApplyGateAsync(
                c, (choice.TopicType!, choice.TopicRef!), anchors, ct);
            if (result.Outcome == ReevaluateOutcome.Verified) repaired++;
            else skipped++;
        }

        var message = $"{repaired} anker(s) hersteld, {recommended} aanbeveling(en) ter review, "
            + $"{skipped} overgeslagen/onopgelost"
            + (capHit ? $" — cap van {maxItems} bereikt, rest volgt bij de volgende run" : "");
        return new(repaired, recommended, skipped, message, capHit);
    }

    /// <summary>#188: lichte LLM-informativiteitstoets voor de her-evaluatie —
    /// er is hier geen verse extractie (dus geen ExtractedClarification.
    /// Operative om op terug te vallen), dus classificeert deze service de
    /// opgeslagen verduidelijking zelf met één kleine rb-ai-call
    /// (<see cref="ClarificationInformativeness.JudgeSystemPrompt"/>).
    /// Degradeert naar de deterministische <see
    /// cref="ClarificationInformativeness.IsMetaOnly"/>-heuristiek bij
    /// AI-uitval (rb-ai onbereikbaar/timeout) of onbruikbare output (geen
    /// parseerbaar "operative"-veld) — nooit een harde 500.</summary>
    private async Task<bool> JudgeInformativeAsync(string clarification, CancellationToken ct)
    {
        string? raw;
        try
        {
            raw = await ai.AskAsync(
                ClarificationInformativeness.BuildJudgePrompt(clarification),
                ClarificationInformativeness.JudgeSystemPrompt, ct: ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Zelfde scout-timeoutpatroon als ClarificationMiningService.
            // AskSafeAsync: een HttpClient-timeout telt als AI-uitval, niet
            // als crash van de her-evaluatie.
            raw = null;
        }

        var operative = raw is null ? null : ClarificationInformativeness.ParseOperative(raw);
        return operative ?? !ClarificationInformativeness.IsMetaOnly(clarification);
    }

    private async Task<string?> LoadDocumentContentAsync(string provenance, CancellationToken ct)
    {
        var sourceId = provenance[ClarificationMiningService.ProvenancePrefix.Length..];
        return await db.Documents.AsNoTracking()
            .Where(d => d.SourceId == sourceId)
            .OrderByDescending(d => d.RetrievedAt)
            .Select(d => d.Content)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Correction.Scope → ClaimTopicMapper-topicType, het omgekeerde
    /// van ClarificationMiningService.ScopeFor.</summary>
    private static string TopicTypeFor(string scope) => scope switch
    {
        "card" => "card",
        "mechanic" => "mechanic",
        "rule_section" => "section",
        _ => "concept",
    };

    /// <summary>Notities zijn tekst of niets: witruimte telt niet als notitie
    /// (zelfde regel als ReviewNoteService.Clean).</summary>
    private static string? Clean(string? note)
    {
        var trimmed = note?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
