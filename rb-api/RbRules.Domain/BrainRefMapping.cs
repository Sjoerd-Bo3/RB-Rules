using System.Text.RegularExpressions;

namespace RbRules.Domain;

/// <summary>Pure topic→ref-mapper voor claims (docs/BRAIN.md §2.2):
/// topic_type/topic_ref ("card"/"Viktor") → de BrainRef van de knoop waar de
/// claim over gaat, inclusief kaartnaam→canoniek-id (#57). De lookups komen
/// van de aanroeper (GraphSyncService) zodat dit puur en unit-testbaar
/// blijft. Geen match ⇒ null: de claim-knoop komt dan zonder ABOUT-edge in
/// de graph — nooit een crash.</summary>
public sealed partial class ClaimTopicMapper
{
    private readonly Dictionary<string, string> _cardIdByName;
    private readonly Dictionary<string, string> _mechanicByName;
    private readonly Dictionary<string, BrainRef> _sectionByCode;
    private readonly Dictionary<string, string> _conceptKeyByName;

    private ClaimTopicMapper(
        Dictionary<string, string> cardIdByName,
        Dictionary<string, string> mechanicByName,
        Dictionary<string, BrainRef> sectionByCode,
        Dictionary<string, string> conceptKeyByName)
    {
        _cardIdByName = cardIdByName;
        _mechanicByName = mechanicByName;
        _sectionByCode = sectionByCode;
        _conceptKeyByName = conceptKeyByName;
    }

    /// <param name="cards">Alle printings; varianten (VariantOf gezet) mappen
    /// op hun canonieke id, zodat ook een alt-art-naam op de canonieke knoop
    /// uitkomt.</param>
    /// <param name="mechanics">Bekende mechaniek-namen (canonieke schrijfwijze).</param>
    /// <param name="sections">Bekende (bron, §-code)-paren in voorkeursvolgorde:
    /// bij een code die in meerdere bronnen bestaat wint de eerste.</param>
    /// <param name="concepts">Primer-concepten (topic-key + titel).</param>
    public static ClaimTopicMapper Create(
        IEnumerable<(string RiftboundId, string Name, string? VariantOf)> cards,
        IEnumerable<string> mechanics,
        IEnumerable<(string SourceId, string Code)> sections,
        IEnumerable<(string Key, string Title)> concepts)
    {
        var cardIdByName = new Dictionary<string, string>();
        var variants = new List<(string Name, string CanonicalId)>();
        foreach (var (id, name, variantOf) in cards)
        {
            if (variantOf is null) cardIdByName.TryAdd(Norm(name), id);
            else variants.Add((name, variantOf));
        }
        // Canonieke namen winnen; een variant-naam ("… (Alternate Art)") is
        // alleen een extra ingang naar dezelfde canonieke knoop.
        foreach (var (name, canonicalId) in variants)
            cardIdByName.TryAdd(Norm(name), canonicalId);

        var mechanicByName = new Dictionary<string, string>();
        foreach (var m in mechanics)
            if (!string.IsNullOrWhiteSpace(m)) mechanicByName.TryAdd(Norm(m), m);

        var sectionByCode = new Dictionary<string, BrainRef>();
        foreach (var (sourceId, code) in sections)
            sectionByCode.TryAdd(
                Norm(RuleSectionParser.NormalizeCode(code)),
                BrainRef.Section(sourceId, code));

        var conceptKeyByName = new Dictionary<string, string>();
        foreach (var (key, title) in concepts)
        {
            conceptKeyByName.TryAdd(Norm(key), key);
            // Topic-keys zijn slugs ("turn-structure"); een claim zegt
            // eerder "turn structure" of de NL-titel.
            conceptKeyByName.TryAdd(Norm(key.Replace('-', ' ')), key);
            conceptKeyByName.TryAdd(Norm(title), key);
        }

        return new ClaimTopicMapper(cardIdByName, mechanicByName, sectionByCode, conceptKeyByName);
    }

    /// <summary>topic_type/topic_ref → BrainRef, of null als het onderwerp
    /// niet aan een bestaande knoop te koppelen is (onbekende kaartnaam,
    /// vrije-tekst-concept, onbekend topic_type).</summary>
    public BrainRef? Resolve(string? topicType, string? topicRef)
    {
        if (string.IsNullOrWhiteSpace(topicType) || string.IsNullOrWhiteSpace(topicRef))
            return null;

        return topicType.Trim().ToLowerInvariant() switch
        {
            "card" => _cardIdByName.TryGetValue(Norm(topicRef), out var id)
                ? BrainRef.Card(id) : null,
            "mechanic" => _mechanicByName.TryGetValue(Norm(topicRef), out var name)
                ? BrainRef.Mechanic(name) : null,
            "section" => ResolveSection(topicRef),
            "concept" => _conceptKeyByName.TryGetValue(Norm(topicRef), out var key)
                ? BrainRef.Concept(key) : null,
            _ => null,
        };
    }

    /// <summary>§-verwijzing in vrije vorm ("§ 101.2", "101.2d", "rule 101.2")
    /// → de sectie-ref van de voorkeursbron. Ook los bruikbaar voor
    /// KnowledgeDoc.SectionRefs (EXPLAINS-edges).</summary>
    public BrainRef? ResolveSection(string text)
    {
        var match = SectionToken().Match(text);
        if (!match.Success) return null;
        return _sectionByCode.TryGetValue(
            Norm(RuleSectionParser.NormalizeCode(match.Value)), out var section)
            ? section : null;
    }

    /// <summary>Vergelijkingsvorm: kleine letters, samengevouwen witruimte.</summary>
    private static string Norm(string s) =>
        Whitespace().Replace(s, " ").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\d{1,4}(?:\.\d+)*(?:\.?[a-z])?", RegexOptions.CultureInvariant)]
    private static partial Regex SectionToken();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>AFFECTS-doelen van een change (docs/BRAIN.md §2.2): de
/// classificatie bepaalt de doelsoort (ban/errata → kaarten,
/// core-rule/tournament-rule → regelsecties), naam-/§-match het doel.
/// Best-effort en puur: geen match is een Change-knoop zonder AFFECTS-edge,
/// nooit een crash.</summary>
public sealed partial class ChangeAffectsMapper
{
    /// <summary>Plafond per change: een volledige-documentdiff mag de graph
    /// niet met honderden edges vollopen — de sterkste matches volstaan.</summary>
    public const int MaxTargets = 25;

    private readonly Regex? _cardNames;
    private readonly Dictionary<string, string> _cardIdByName;
    private readonly Dictionary<string, BrainRef> _sectionByCode;

    private ChangeAffectsMapper(
        Regex? cardNames,
        Dictionary<string, string> cardIdByName,
        Dictionary<string, BrainRef> sectionByCode)
    {
        _cardNames = cardNames;
        _cardIdByName = cardIdByName;
        _sectionByCode = sectionByCode;
    }

    /// <param name="canonicalCards">Alleen canonieke printings (#57) — de
    /// graph kent geen variant-knopen.</param>
    /// <param name="sections">Bekende (bron, §-code)-paren in
    /// voorkeursvolgorde (zelfde afspraak als ClaimTopicMapper).</param>
    public static ChangeAffectsMapper Create(
        IEnumerable<(string RiftboundId, string Name)> canonicalCards,
        IEnumerable<(string SourceId, string Code)> sections)
    {
        var cardIdByName = new Dictionary<string, string>();
        foreach (var (id, name) in canonicalCards)
            if (!string.IsNullOrWhiteSpace(name)) cardIdByName.TryAdd(Norm(name), id);

        // Eén alternatie-regex, langste naam eerst: "Viktor, Machine Herald"
        // wint van "Viktor" op dezelfde positie. Lookarounds i.p.v. \b zodat
        // namen met leestekens aan de rand ook exact matchen.
        Regex? cardNames = null;
        if (cardIdByName.Count > 0)
        {
            var alternation = string.Join('|', cardIdByName.Keys
                .OrderByDescending(n => n.Length)
                .Select(Regex.Escape));
            cardNames = new Regex($@"(?<!\w)(?:{alternation})(?!\w)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var sectionByCode = new Dictionary<string, BrainRef>();
        foreach (var (sourceId, code) in sections)
            sectionByCode.TryAdd(
                Norm(RuleSectionParser.NormalizeCode(code)),
                BrainRef.Section(sourceId, code));

        return new ChangeAffectsMapper(cardNames, cardIdByName, sectionByCode);
    }

    public IReadOnlyList<BrainRef> Resolve(string? changeType, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return changeType?.Trim().ToLowerInvariant() switch
        {
            "ban" or "errata" => CardTargets(text),
            "core-rule" or "tournament-rule" => SectionTargets(text),
            // set-release/editorial/unknown: geen aanwijsbaar doel — de
            // Change-knoop zelf blijft bestaan, zonder edges.
            _ => [],
        };
    }

    private List<BrainRef> CardTargets(string text)
    {
        if (_cardNames is null) return [];
        var targets = new List<BrainRef>();
        var seen = new HashSet<string>();
        foreach (Match m in _cardNames.Matches(text))
        {
            if (targets.Count >= MaxTargets) break;
            if (_cardIdByName.TryGetValue(Norm(m.Value), out var id) && seen.Add(id))
                targets.Add(BrainRef.Card(id));
        }
        return targets;
    }

    private List<BrainRef> SectionTargets(string text)
    {
        var targets = new List<BrainRef>();
        var seen = new HashSet<string>();
        foreach (Match m in SectionCandidate().Matches(text))
        {
            if (targets.Count >= MaxTargets) break;
            var code = Norm(RuleSectionParser.NormalizeCode(m.Value));
            if (_sectionByCode.TryGetValue(code, out var section) && seen.Add(code))
                targets.Add(section);
        }
        return targets;
    }

    private static string Norm(string s) =>
        Whitespace().Replace(s, " ").Trim().ToLowerInvariant();

    // §-achtige tokens: gepunte codes ("101.2", "1.3.b") of losse 3-4-cijferige
    // nummers (Core Rules-stijl "101"). Bewust géén losse 1-2-cijferige
    // nummers — "40 cards" mag nooit §40 raken; de bekende-codes-filter
    // vangt de rest.
    [GeneratedRegex(@"(?<![\w.])(?:\d{1,4}(?:\.\d+)+(?:\.?[a-z])?|\d{3,4})(?!\w)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SectionCandidate();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
