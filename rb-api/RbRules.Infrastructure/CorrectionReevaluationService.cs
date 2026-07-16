using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public enum ReevaluateOutcome { Verified, StillPending, NotFound }

public record ReevaluateResult(ReevaluateOutcome Outcome, string? Reason);

/// <summary>Resultaat van de anker-herstel-pas (#188 increment 3, <see
/// cref="CorrectionReevaluationService.RepairPendingAnchorsAsync"/>).
/// <paramref name="CapHit"/> volgt hetzelfde #190-contract als <see
/// cref="ClarificationMineResult.CapHit"/>: machine-leesbaar of er ná deze
/// run nog vers werk (meer kandidaten dan de cap) op de bank lag, zodat het
/// admin-jobpad (JobCatalog.ClarifyAsync) hierop kan draineren.</summary>
public record AnchorRepairResult(
    int Repaired, int Skipped, string Message, bool CapHit = false);

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
    /// <b>Spookduplicaat-vangrail:</b> als <paramref name="anchorOverride"/>
    /// resolvet naar een anker waar een ANDERE Correction van dezelfde bron al
    /// op staat (zelfde Provenance+Scope+Ref), wordt de verplaatsing NIET
    /// doorgevoerd — anders zou het herstel-pad twee verified rulings over
    /// hetzelfde onderwerp kunnen opleveren (zie
    /// <see cref="RepairPendingAnchorsAsync"/>'s klasse-doc voor het volledige
    /// scenario). De poort behandelt dat als "niet anchored", met een
    /// zichtbare reden in StatusReason.</summary>
    private async Task<ReevaluateResult> ApplyGateAsync(
        Correction c, (string TopicType, string TopicRef)? anchorOverride,
        ClaimTopicMapper anchors, CancellationToken ct)
    {
        var topicType = anchorOverride?.TopicType ?? TopicTypeFor(c.Scope);
        var topicRef = anchorOverride?.TopicRef ?? c.Ref;
        var anchored = anchors.Resolve(topicType, topicRef) is not null;

        string? duplicateNote = null;
        if (anchorOverride is not null && anchored)
        {
            var targetScope = ClarificationMiningService.ScopeFor(anchorOverride.Value.TopicType);
            var targetRefLower = anchorOverride.Value.TopicRef.ToLowerInvariant();
            var collides = await db.Corrections.AsNoTracking().AnyAsync(x =>
                x.Id != c.Id && x.Provenance == c.Provenance && x.Scope == targetScope
                && x.Ref.ToLower() == targetRefLower, ct);
            if (collides)
            {
                anchored = false;
                duplicateNote = $"voorgesteld anker '{anchorOverride.Value.TopicRef}' "
                    + $"({anchorOverride.Value.TopicType}) wijst al naar een bestaande "
                    + "correction van deze bron — niet automatisch samengevoegd";
            }
        }

        var docContent = await LoadDocumentContentAsync(c.Provenance!, ct);
        var quote = ClarificationMiningService.ExtractQuote(c.Text);
        var grounded = ClarificationGrounding.IsGrounded(quote, docContent);
        var informative = await JudgeInformativeAsync(
            ClarificationMiningService.ClarificationOf(c.Text), ct);
        var verifies = grounded && anchored && informative;

        // Anker-correctie toepassen zodra hij ook echt resolvet (en niet
        // botst met een bestaande correction) — een niet-herkend of botsend
        // voorgesteld anker verandert niets (blijft het oorspronkelijke
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
        if (duplicateNote is not null) c.StatusReason += $"; {duplicateNote}";
        c.VerifiedAt = null;
        await db.SaveChangesAsync(ct);
        return new(ReevaluateOutcome.StillPending, c.StatusReason);
    }

    /// <summary>Anker-herstel-pas (#188 increment 3): productiedata (issue
    /// #199, comment 2026-07-16) toont dat 117 van de 133 pending clarify-
    /// corrections falen op anker-resolutie — de extractie koos een
    /// vrije-vorm-onderwerp ("battlefield control without units") dat niet in
    /// het vocabulaire voorkomt, terwijl de inhoud zelf vaak gegrond en
    /// informatief is. In plaats van de beheerder elk item met de hand te
    /// laten corrigeren (<see cref="ReviewNoteAnchor"/>), doet deze pas één
    /// LLM-KEUZE per item uit het echte vocabulaire (<see
    /// cref="ClarificationAnchorRepair"/>) en her-toetst dan de volledige
    /// poort via <see cref="ApplyGateAsync"/> — dezelfde logica als een
    /// handmatige her-evaluatie, alleen automatisch aangestuurd.
    ///
    /// Kandidaten: pending clarify-mining-Corrections met StatusReason
    /// "onderwerp … niet herkend" (ongeacht of citaat/informativiteit óók
    /// faalden — de #199-cijfers laten zien dat "anker + citaat gecombineerd"
    /// ook een grote klasse is, en de herstelde ankernaam is ook dan nuttig
    /// voor een volgende triage) en ZONDER <see cref="Correction.ReviewNote"/>
    /// (#184: beheerder-eigendom — deze pas raakt niets aan waar een mens al
    /// naar gekeken heeft). Cap per run (<see cref="DefaultRepairCap"/>) +
    /// <see cref="AnchorRepairResult.CapHit"/> zodat het #190-drain-pad
    /// (JobCatalog.ClarifyAsync) de rest oppakt. AI-uitval op de anker-keuze
    /// zelf ⇒ item overslaan (nooit een 500, nooit gokken op een verzonnen
    /// anker).
    ///
    /// <b>Spookduplicaat-afweging:</b> deze pas zet BEWUST geen <see
    /// cref="Correction.ReviewNote"/> op het verplaatste item (in
    /// tegenstelling tot een handmatige anker-correctie) — een ReviewNote
    /// betekent in de rest van de codebase "een beheerder heeft hiernaar
    /// gekeken" (het bepaalt bv. of <see
    /// cref="ClarificationMiningService.StoreAsync"/> Status/Question mag
    /// bijwerken), en een geautomatiseerde keuze als zodanig labelen zou die
    /// betekenis vervuilen. Dat betekent wel dat de bestaande cross-bucket-
    /// redding in StoreAsync (die alleen <c>ReviewNote != null</c>-siblings
    /// meeneemt) een door déze pas verplaatst item niet ziet: een latere
    /// her-mine van dezelfde bron die het oorspronkelijke vrije-vorm-onderwerp
    /// wéér extraheert, zou anders een tweede, apart pending item op de OUDE
    /// (Scope,Ref)-bucket kunnen maken — en als een volgende herstel-pas-run
    /// óók dát item naar hetzelfde ECHTE anker stuurt, zouden er twee verified
    /// rulings over hetzelfde onderwerp ontstaan. In plaats van StoreAsync
    /// (bewust niet aangeraakt, zie #188-increment-scope) of ReviewNote te
    /// misbruiken, bewaakt <see cref="ApplyGateAsync"/> dit zelf: vóór het
    /// toepassen van een anker-override checkt het of een ANDERE Correction
    /// van dezelfde bron al op dat (Scope,Ref) staat — zo ja, dan blijft dit
    /// item onaangeraakt (pending, met een zichtbare reden) in plaats van een
    /// duplicaat te worden. De tests bij deze klasse dekken het scenario
    /// expliciet (spookduplicaat na bucket-verplaatsing).</summary>
    public async Task<AnchorRepairResult> RepairPendingAnchorsAsync(
        int maxItems = DefaultRepairCap, Action<string>? progress = null, CancellationToken ct = default)
    {
        maxItems = Math.Clamp(maxItems, 1, 300);

        var query = db.Corrections.Where(c =>
            c.Status == "unverified"
            && c.ReviewNote == null
            && c.Provenance != null && c.Provenance.StartsWith(ClarificationMiningService.ProvenancePrefix)
            && c.StatusReason != null && c.StatusReason.Contains("niet herkend"));

        var totalEligible = await query.CountAsync(ct);
        if (totalEligible == 0) return new(0, 0, "geen anker-herstel nodig", CapHit: false);

        var candidates = await query.OrderBy(c => c.Id).Take(maxItems).ToListAsync(ct);
        var capHit = totalEligible > candidates.Count;

        var (anchors, mechanics, concepts) = await AnchorResolverFactory.BuildWithVocabularyAsync(db, ct);
        var systemPrompt = ClarificationAnchorRepair.GetSystemPrompt(mechanics, concepts);

        var repaired = 0;
        var skipped = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            progress?.Invoke($"anker-herstel {i + 1}/{candidates.Count}");

            string? raw;
            try
            {
                raw = await ai.AskAsync(
                    ClarificationAnchorRepair.BuildPrompt(ClarificationMiningService.ClarificationOf(c.Text)),
                    systemPrompt, ct: ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Zelfde scout-timeoutpatroon als ClarificationMiningService.
                // AskSafeAsync/JudgeInformativeAsync: een HttpClient-timeout
                // telt als AI-uitval, niet als crash van de herstel-pas.
                raw = null;
            }

            var choice = raw is null ? null : ClarificationAnchorRepair.ParseAnchorChoice(raw);
            if (choice is null) { skipped++; continue; } // AI-uitval, "none" of onbruikbaar antwoord: item blijft staan

            var result = await ApplyGateAsync(c, choice, anchors, ct);
            if (result.Outcome == ReevaluateOutcome.Verified) repaired++;
            else skipped++;
        }

        var message = $"{repaired} anker(s) hersteld, {skipped} overgeslagen/onopgelost"
            + (capHit ? $" — cap van {maxItems} bereikt, rest volgt bij de volgende run" : "");
        return new(repaired, skipped, message, capHit);
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
