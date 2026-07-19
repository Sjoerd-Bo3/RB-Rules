namespace RbRules.Domain.GraphRag;

/// <summary>Eén gazetteer-ingang: een canonieke knoop met zijn oppervlaktevormen
/// (§4-EntityLinking). Hergebruikt de fase-1-canonicals: <see cref="Ref"/> is de
/// BrainRef van de <c>CanonicalEntity</c>/kaart/sectie, <see cref="Aliases"/> het
/// alias-lexicon (<c>CanonicalEntity.AltLabels</c>). <see cref="Prior"/> is een
/// statische voorkeur (0..1) — bv. genormaliseerde frequentie of kaart-populariteit
/// — die homoniemen breekt als geen enkel ander signaal dat doet.</summary>
public sealed record GazetteerEntry(
    BrainRef Ref, string CanonicalLabel, IReadOnlyList<string> Aliases, double Prior = 0.0);

/// <summary>De gazetteer: een genormaliseerde index van oppervlaktevorm →
/// ingang(en) (§4). Eén genormaliseerde sleutel kan MEER ingangen dragen — dat
/// zijn de homoniemen (dezelfde tekst, verschillende knopen) die de disambiguatie
/// moet breken. Bouw met <see cref="Build"/>; het opzoeken normaliseert via
/// <see cref="AliasNormalizer"/> zodat casing/koppeltekens/underscores niet uitmaken.</summary>
public sealed class Gazetteer
{
    private readonly Dictionary<string, List<GazetteerEntry>> _byNorm;

    private Gazetteer(Dictionary<string, List<GazetteerEntry>> byNorm) => _byNorm = byNorm;

    public IReadOnlyCollection<GazetteerEntry> AllEntries =>
        [.. _byNorm.Values.SelectMany(v => v).DistinctBy(e => e.Ref.Format())];

    public static Gazetteer Build(IEnumerable<GazetteerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var byNorm = new Dictionary<string, List<GazetteerEntry>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            AddKey(byNorm, e.CanonicalLabel, e);
            foreach (var alias in e.Aliases)
                AddKey(byNorm, alias, e);
        }
        return new Gazetteer(byNorm);
    }

    private static void AddKey(Dictionary<string, List<GazetteerEntry>> byNorm, string surface, GazetteerEntry e)
    {
        var norm = AliasNormalizer.Normalize(surface);
        if (norm.Length == 0) return;
        if (!byNorm.TryGetValue(norm, out var list)) byNorm[norm] = list = [];
        // Homoniem-lijst: dezelfde sleutel mag meerdere ingangen dragen, maar
        // niet dezelfde ingang twee keer (label==alias, of dubbele alias).
        if (!list.Any(x => x.Ref == e.Ref)) list.Add(e);
    }

    /// <summary>Exacte (genormaliseerde) treffers voor een oppervlaktevorm; leeg als
    /// er geen zijn.</summary>
    public IReadOnlyList<GazetteerEntry> Exact(string surface) =>
        _byNorm.TryGetValue(AliasNormalizer.Normalize(surface), out var list) ? list : [];
}

/// <summary>Eén kandidaat-knoop voor een mention: de gazetteer-ingang plus het
/// lexicale signaal (1.0 bij een exacte match, anders de trigram-similarity van de
/// fuzzy-match).</summary>
public readonly record struct CandidateNode(GazetteerEntry Entry, double LexicalScore)
{
    public BrainRef Ref => Entry.Ref;
}

/// <summary>Een gedetecteerde mention: het oppervlakte-fragment, zijn token-span in
/// de vraag, en de kandidaat-knopen (exact + fuzzy).</summary>
public sealed record EntityMention(
    string Surface, int TokenStart, int TokenLength, IReadOnlyList<CandidateNode> Candidates);

/// <summary>Detecteert mentions door de vraag te scannen tegen de gazetteer
/// (§4: gazetteer/Aho-Corasick → fuzzy/trigram voor spelfouten). Bewust een
/// deterministische longest-match n-gram-scan i.p.v. een echte Aho-Corasick-
/// automaat: bij fase-1-cardinaliteit (tientallen–honderden canonicals) is dit
/// ruim snel genoeg, volledig puur en test-transparant. Geen dekking → géén LLM-NER
/// hier (dat is een integratie-follow-up in de orchestrator; uitval=null).</summary>
public static class MentionDetector
{
    public const double DefaultFuzzyThreshold = 0.55;
    public const int DefaultMaxSpan = 4;

    public static IReadOnlyList<EntityMention> Detect(
        string question, Gazetteer gazetteer,
        double fuzzyThreshold = DefaultFuzzyThreshold, int maxSpan = DefaultMaxSpan)
    {
        ArgumentNullException.ThrowIfNull(gazetteer);
        if (string.IsNullOrWhiteSpace(question)) return [];

        var tokens = Tokenize(question);
        var mentions = new List<EntityMention>();
        var i = 0;
        while (i < tokens.Count)
        {
            var matched = false;
            var maxLen = Math.Min(maxSpan, tokens.Count - i);
            // Longest-match eerst: een langere naam ("Death Knell") wint van zijn
            // losse woord ("Death").
            for (var len = maxLen; len >= 1; len--)
            {
                var surface = string.Join(' ', tokens.GetRange(i, len));
                var exact = gazetteer.Exact(surface);
                if (exact.Count > 0)
                {
                    mentions.Add(new(surface, i, len, [.. exact.Select(e => new CandidateNode(e, 1.0))]));
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (matched) continue;

            // Geen exacte match: fuzzy op het losse token (spelfout-tolerantie).
            var single = tokens[i];
            var fuzzy = FuzzyCandidates(single, gazetteer, fuzzyThreshold);
            if (fuzzy.Count > 0)
                mentions.Add(new(single, i, 1, fuzzy));
            i++;
        }
        return mentions;
    }

    private static IReadOnlyList<CandidateNode> FuzzyCandidates(
        string surface, Gazetteer gazetteer, double threshold)
    {
        var normSurface = AliasNormalizer.Normalize(surface);
        if (normSurface.Length < 3) return [];
        var best = new List<CandidateNode>();
        foreach (var entry in gazetteer.AllEntries)
        {
            var score = Trigrams.Similarity(normSurface, AliasNormalizer.Normalize(entry.CanonicalLabel));
            foreach (var alias in entry.Aliases)
                score = Math.Max(score, Trigrams.Similarity(normSurface, AliasNormalizer.Normalize(alias)));
            if (score >= threshold)
                best.Add(new(entry, score));
        }
        return [.. best.OrderByDescending(c => c.LexicalScore).Take(5)];
    }

    private static List<string> Tokenize(string question)
    {
        var tokens = new List<string>();
        foreach (var raw in question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = raw.Trim('.', ',', '?', '!', ';', ':', '"', '\'', '(', ')', '[', ']');
            if (trimmed.Length > 0) tokens.Add(trimmed);
        }
        return tokens;
    }
}

/// <summary>Gewichten van de disambiguatie-scoring (§4:
/// <c>α·lexicaal + β·cos + γ·co-mention-coherentie + δ·prior</c>). De co-mention-
/// coherentie (γ) is de graaf-truc; hij weegt bewust zwaar zodat een via een edge
/// verbonden kandidaat een homonieme kaartnaam verslaat.</summary>
public sealed record LinkWeights(double Lexical, double Cosine, double Coherence, double Prior)
{
    public static readonly LinkWeights Default = new(Lexical: 0.30, Cosine: 0.25, Coherence: 0.35, Prior: 0.10);
}

/// <summary>Score-uitsplitsing van één kandidaat — de provenance van de keuze.</summary>
public sealed record CandidateScore(
    BrainRef Ref, double Total, double Lexical, double Cosine, double Coherence, double Prior);

/// <summary>De <c>LinkDecision</c> (§4, provenance): welke mention naar welke knoop
/// is gelinkt, met de volledige score-rangschikking en een memo. <see cref="Chosen"/>
/// is null wanneer geen kandidaat de <see cref="EntityLinker.MinScore"/>-drempel
/// haalt (liever niet linken dan fout linken — inzicht #236: geen onzichtbare
/// gok).</summary>
public sealed record LinkDecision(
    string Surface, int TokenStart, int TokenLength,
    BrainRef? Chosen, double Score, IReadOnlyList<CandidateScore> Ranked, string Memo);

/// <summary>De disambiguerende entity-linker (§4). Puur: de embedding-cosine per
/// kandidaat en de graaf-edge-connectiviteit komen als functies binnen (in productie
/// uit pgvector resp. Neo4j; in tests uit fixtures), de scoring en de keuze zijn
/// deterministisch en volledig getest.</summary>
public static class EntityLinker
{
    /// <summary>Minimale totaalscore om te linken; eronder blijft de mention
    /// ongelinkt (Chosen=null). Voorkomt dat een zwakke fuzzy-match zonder cosine/
    /// coherentie een fout anker forceert.</summary>
    public const double MinScore = 0.20;

    /// <param name="cosine">Context-embedding-similarity (0..1) per kandidaat-ref;
    /// ontbrekend → 0 (embedding-uitval degradeert netjes, §4). </param>
    /// <param name="edgeConnected">Zijn twee kandidaat-knopen via een getypeerde
    /// edge verbonden? De graaf-truc: co-mention-coherentie beloont een kandidaat
    /// die met een kandidaat van een ándere mention verbonden is.</param>
    public static IReadOnlyList<LinkDecision> Link(
        IReadOnlyList<EntityMention> mentions,
        Func<BrainRef, double>? cosine = null,
        Func<BrainRef, BrainRef, bool>? edgeConnected = null,
        LinkWeights? weights = null,
        double minScore = MinScore)
    {
        ArgumentNullException.ThrowIfNull(mentions);
        var w = weights ?? LinkWeights.Default;
        var cos = cosine ?? (_ => 0.0);
        var connected = edgeConnected ?? ((_, _) => false);

        var decisions = new List<LinkDecision>(mentions.Count);
        foreach (var mention in mentions)
        {
            var ranked = new List<CandidateScore>(mention.Candidates.Count);
            foreach (var cand in mention.Candidates)
            {
                var lex = Math.Clamp(cand.LexicalScore, 0, 1);
                var cosScore = Math.Clamp(cos(cand.Ref), 0, 1);
                var coherence = Coherence(cand.Ref, mention, mentions, connected);
                var prior = Math.Clamp(cand.Entry.Prior, 0, 1);
                var total = w.Lexical * lex + w.Cosine * cosScore + w.Coherence * coherence + w.Prior * prior;
                ranked.Add(new(cand.Ref, total, lex, cosScore, coherence, prior));
            }

            // Deterministische, totale orde: score, dan een stabiele ref-sleutel.
            ranked.Sort((a, b) =>
            {
                var byScore = b.Total.CompareTo(a.Total);
                return byScore != 0 ? byScore : string.CompareOrdinal(a.Ref.Format(), b.Ref.Format());
            });

            var top = ranked.Count > 0 ? ranked[0] : null;
            if (top is not null && top.Total >= minScore)
                decisions.Add(new(mention.Surface, mention.TokenStart, mention.TokenLength,
                    top.Ref, top.Total, ranked, LinkMemo(top, ranked)));
            else
                decisions.Add(new(mention.Surface, mention.TokenStart, mention.TokenLength,
                    null, top?.Total ?? 0, ranked,
                    $"Geen kandidaat haalt de drempel {minScore:0.00} — mention '{mention.Surface}' ongelinkt."));
        }
        return decisions;
    }

    /// <summary>De co-mention-coherentie van een kandidaat: het aandeel ándere
    /// mentions dat minstens één kandidaat heeft die via een edge met déze kandidaat
    /// verbonden is. 0 als er geen andere mentions zijn.</summary>
    private static double Coherence(
        BrainRef cand, EntityMention self, IReadOnlyList<EntityMention> all,
        Func<BrainRef, BrainRef, bool> connected)
    {
        var others = all.Where(m => !ReferenceEquals(m, self) && m.Candidates.Count > 0).ToList();
        if (others.Count == 0) return 0;
        var hits = 0;
        foreach (var other in others)
            if (other.Candidates.Any(c2 => c2.Ref != cand && connected(cand, c2.Ref)))
                hits++;
        return hits / (double)others.Count;
    }

    private static string LinkMemo(CandidateScore top, IReadOnlyList<CandidateScore> ranked)
    {
        var runnerUp = ranked.Count > 1 ? ranked[1] : null;
        var basis = $"lex={top.Lexical:0.00} cos={top.Cosine:0.00} coher={top.Coherence:0.00} prior={top.Prior:0.00}";
        return runnerUp is null
            ? $"{top.Ref.Format()} gekozen ({basis}); enige kandidaat."
            : $"{top.Ref.Format()} gekozen ({basis}); versloeg {runnerUp.Ref.Format()} " +
              $"({top.Total:0.00} vs {runnerUp.Total:0.00}).";
    }

    /// <summary>De gelinkte ankers (de gekozen, niet-null refs) — de subgraaf-seeds
    /// voor Local/DRIFT en de entity-teller voor de β-router en modus-selector.</summary>
    public static IReadOnlyList<BrainRef> Anchors(IReadOnlyList<LinkDecision> decisions) =>
        [.. decisions.Where(d => d.Chosen is not null).Select(d => d.Chosen!.Value)];
}
