using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Kleine, procesbrede LRU-cache voor de query-rewrite (#152): een
/// herhaalde of licht andere schrijfwijze van dezelfde vraag kort na elkaar
/// slaat de volledige rewrite-call over (rb-ai — de Agent SDK start per call
/// een verse sessie, dus dat is de traagste voorbereidingsstap). Sleutel is
/// de retrievaltekst (vraag + eventuele doorvraag-historie), genormaliseerd
/// met <see cref="NormalizeKey"/> zodat alleen hoofdletters/rand-witruimte
/// verschillen nog steeds een hit geven — geen fuzzy-matching (YAGNI, klein
/// houden zoals de issue vraagt).
///
/// Nooit een null-uitkomst cachen: rewrite-uitval of onzin-output (#66) mag
/// nooit blijven "hangen" voor de rest van de procesduur — de volgende vraag
/// probeert opnieuw. <see cref="Set"/> accepteert daarom alleen een
/// geslaagde <see cref="QueryRewrite"/>, nooit null.
///
/// Als singleton geregistreerd (Program.cs) en via constructor-DI in
/// AskService geïnjecteerd (optioneel, default null — precies het patroon
/// van <c>IDbContextFactory</c> hierboven): zonder registratie (unit-tests)
/// is caching gewoon uit, geen speciale test-modus nodig.
///
/// Thread-safe via één lock: de cache is klein en de kritieke sectie is
/// O(1), dus lock-contentie is verwaarloosbaar t.o.v. een LLM-call.</summary>
public sealed class RewriteCache(int capacity = 200)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, QueryRewrite Value)>> _map = [];
    private readonly LinkedList<(string Key, QueryRewrite Value)> _order = new();

    /// <summary>Normaliseert de retrievaltekst tot een cachesleutel: rand-
    /// witruimte weg, hoofdletterongevoelig. Geen woord-normalisatie verder —
    /// dat zou de rewrite zelf dupliceren.</summary>
    public static string NormalizeKey(string retrievalText) =>
        retrievalText.Trim().ToLowerInvariant();

    /// <summary>Cache-hit verplaatst de entry naar de meest-recent-gebruikte
    /// positie (LRU-boekhouding); een miss levert null.</summary>
    public QueryRewrite? TryGet(string key)
    {
        lock (gate)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Value;
        }
    }

    /// <summary>Slaat een geslaagde rewrite op; verdringt bij capaciteit de
    /// minst-recent-gebruikte entry. Bestaat de sleutel al (zelfde vraag
    /// nogmaals gerewrite, bv. na een eerdere cache-miss van twee gelijktijdige
    /// aanvragen), dan wint de nieuwste waarde.</summary>
    public void Set(string key, QueryRewrite value)
    {
        lock (gate)
        {
            if (_map.TryGetValue(key, out var existing))
                _order.Remove(existing);
            else if (_map.Count >= capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
            var node = new LinkedListNode<(string, QueryRewrite)>((key, value));
            _order.AddFirst(node);
            _map[key] = node;
        }
    }

    /// <summary>Aantal entries — test-seam, geen productiegebruik.</summary>
    public int Count { get { lock (gate) return _map.Count; } }
}
