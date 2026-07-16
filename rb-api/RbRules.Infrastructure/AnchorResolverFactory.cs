using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Gedeelde opbouw van de anker-resolver (#177), oorspronkelijk
/// privé in <see cref="ClarificationMiningService"/> en uitgetrokken (#184) zodat
/// <see cref="CorrectionReevaluationService"/> exact dezelfde ankers ziet bij
/// een her-evaluatie van één item: alle printings (varianten → canoniek), het
/// volledige mechaniek-vocabulaire (seed + geaccepteerde keywords ∪ gemínede
/// kaartmechanieken), bestaande §-codes en primer-concepten. Puur resultaat
/// (<see cref="ClaimTopicMapper"/>); onbekend onderwerp resolvet naar
/// null — logica zelf ongewijzigd, alleen verplaatst.</summary>
internal static class AnchorResolverFactory
{
    /// <summary>Ongewijzigd bestaand koppelvlak: alleen de opaque resolver,
    /// voor aanroepers die geen prompt-vocabulaire nodig hebben (<see
    /// cref="CorrectionReevaluationService"/>'s handmatige-opmerking-pad).</summary>
    public static async Task<ClaimTopicMapper> BuildAsync(RbRulesDbContext db, CancellationToken ct) =>
        (await BuildWithVocabularyAsync(db, ct)).Mapper;

    /// <summary>#188 increment 3: naast de resolver ook de leesbare
    /// mechaniek-/concept-vocabulaire voor de extractie-/herstel-pas-prompt
    /// (<see cref="ClarificationMiner.BuildVocabularyBlock"/>) — dezelfde
    /// query's, één keer uitgevoerd, zodat de prompt en de deterministische
    /// validatie (<see cref="ClaimTopicMapper.Resolve"/>) gegarandeerd
    /// hetzelfde vocabulaire zien. Cards/§-codes zijn te talrijk voor een
    /// prompt-opsomming en blijven daarom alleen in de resolver — de prompt
    /// instrueert daarvoor "gebruik de exacte naam/code".</summary>
    public static async Task<(ClaimTopicMapper Mapper, IReadOnlyList<string> Mechanics, IReadOnlyList<(string Key, string Title)> Concepts)>
        BuildWithVocabularyAsync(RbRulesDbContext db, CancellationToken ct)
    {
        var cards = await db.Cards.AsNoTracking()
            .Select(c => new { c.RiftboundId, c.Name, c.VariantOf })
            .ToListAsync(ct);
        var accepted = await db.MechanicKeywords.AsNoTracking()
            .Where(k => k.Status == "accepted").Select(k => k.Term).ToListAsync(ct);
        var minedMechanics = (await db.Cards.AsNoTracking()
                .Where(c => c.Mechanics != null).Select(c => c.Mechanics!).ToListAsync(ct))
            .SelectMany(m => m);
        var sections = await db.RuleChunks.AsNoTracking()
            .Where(r => r.SectionCode != null && r.SectionCode != "")
            .Select(r => new { r.SourceId, Code = r.SectionCode! })
            .Distinct().ToListAsync(ct);
        var concepts = (await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Kind == "primer")
                .Select(k => new { k.Topic, k.Title })
                .ToListAsync(ct))
            .GroupBy(k => k.Topic).Select(g => g.First()).ToList();

        var mechanicVocabulary = MechanicMiner.Vocabulary(accepted).Concat(minedMechanics)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var conceptVocabulary = concepts.Select(k => (k.Topic, k.Title)).ToList();

        var mapper = ClaimTopicMapper.Create(
            cards.Select(c => (c.RiftboundId, c.Name, c.VariantOf)),
            mechanicVocabulary,
            sections.Select(s => (s.SourceId, s.Code)),
            conceptVocabulary);

        return (mapper, mechanicVocabulary, conceptVocabulary);
    }
}
