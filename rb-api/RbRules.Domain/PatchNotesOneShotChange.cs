namespace RbRules.Domain;

/// <summary>One-shot patch-notes-Change (#205): een per-set patch-notes-
/// artikel (bv. "Core Rules: Vendetta Patch Notes") verandert na publicatie
/// nooit meer inhoudelijk — het is één gepubliceerde momentopname, geen
/// terugkerende pagina zoals de algemene "Core Rules Patch Notes"-hub. #185
/// liet de eerste scan van élke patch-notes-bron bewust zonder Change (de
/// duiding komt normaal via de tweede-scan-diff), maar voor zo'n one-shot-
/// artikel komt die tweede scan er nooit: de regelwijzigingen die de set
/// introduceert vielen daardoor structureel tussen wal (changes-feed) en
/// schip (clarify-mining, die patch-notes sinds #185 bewust overslaat).
///
/// Fix: bij een scan van een patch-notes-bron die nog GEEN niet-editoriale
/// Change heeft, is de volledige inhoud zelf de delta (lege "voor"-versie) —
/// dezelfde AI-classificatie/samenvatting als een echte diff. De guard is
/// bewust "nog geen niet-editoriale Change" in plaats van "eerste scan"
/// (<see cref="RbRules.Infrastructure.IngestService"/> checkt <c>LastHash is
/// null</c> voor dat laatste): dat dekt zowel een gloednieuwe bron als de
/// BACKFILL van een bron die vóór deze fix al (zonder Change) gescand is —
/// zoals de bestaande Vendetta-bron, die al een Document had maar geen
/// inhoudelijke Change. Een terugkerende patch-notes-pagina (core-rules-
/// patch-notes) verandert niet van gedrag: die heeft door haar normale
/// diff-gedrag allang niet-editoriale Changes, dus deze guard vuurt daar
/// nooit (opnieuw).
///
/// <b>Gesloten onder eigen output (#205-review, findings 4/5/9):</b> de
/// Change-check alleen is niet genoeg — wordt de geminte one-shot-Change
/// (meteen of later, via de #58-naclassificatie) als "editorial"
/// geclassificeerd, dan ziet de guard "geen niet-editoriale Change" en zou
/// hij élke volgende scan opnieuw minten. Daarom schrijft het minten óók
/// een run_log-memo (kind <see cref="LedgerKind"/>, Ref = sourceId —
/// hetzelfde grootboek-idioom als SetReleaseService) en eist de guard dat
/// dat memo ontbreekt: één one-shot-poging per bron, ongeacht hoe de
/// classifier de uitkomst labelt.</summary>
public static class PatchNotesOneShotChange
{
    /// <summary>run_log-grootboek voor het één-keer-per-bron-memo: kind
    /// "oneshot-patchnotes", Ref = sourceId, geschreven op het moment van
    /// minten (zelfde transactie/SaveChanges als de Change zelf).</summary>
    public const string LedgerKind = "oneshot-patchnotes";

    /// <param name="trustTier">Alleen officiële (trust-1) bronnen — zelfde
    /// autoriteitspoort als de Faq-sjabloon-Change en de content-kind-
    /// classificatie zelf.</param>
    /// <param name="effectiveKind">De effectieve bron-kind (<see
    /// cref="SourceContentKind.Resolve"/>).</param>
    /// <param name="hasNonEditorialChange">Heeft deze bron al minstens één
    /// Change met een ander ChangeType dan "editorial"? Zo ja, dan draaide de
    /// echte duiding al eerder (normale diff of een eerdere one-shot) —
    /// nooit opnieuw.</param>
    /// <param name="hasOneShotMemo">Staat er al een <see cref="LedgerKind"/>-
    /// memo voor deze bron in run_log? Zo ja, dan is de one-shot al één keer
    /// gemint — ook als de classifier die Change (later) "editorial"
    /// labelde. Nooit een tweede poging.</param>
    public static bool IsCandidate(
        short trustTier, string effectiveKind, bool hasNonEditorialChange, bool hasOneShotMemo) =>
        trustTier == 1 && effectiveKind == SourceContentKind.PatchNotes
        && !hasNonEditorialChange && !hasOneShotMemo;
}
