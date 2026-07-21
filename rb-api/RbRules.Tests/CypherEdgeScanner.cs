using System.Text;
using RbRules.Domain.Ontology;

namespace RbRules.Tests;

/// <summary>Haalt uit een Cypher-statement élke edge die het statement SCHRIJFT
/// (dus achter <c>MERGE</c>/<c>CREATE</c>, niet achter <c>MATCH</c>), met sinds
/// #289 PR 2 ook de KNOOPLABELS aan weerszijden.
///
/// WAAROM GEEN REGEX (#289-review). De eerste versie van deze guard gebruikte één
/// reguliere expressie, en die liet drie klassen drift ongemerkt door — elk
/// bewezen met een mutatie:
/// <list type="bullet">
/// <item>COMMENTAAR. <c>// MERGE (k)-[:EXPLAINS]-&gt;(r)</c> telde mee als
///   geschreven edge, dus een statement UITZETTEN (de alledaagste manier waarop
///   iemand iets uitschakelt) bleef groen — terwijl dezelfde regel VERWIJDEREN
///   wél rood gaf. Spiegelbeeld: voorbeeld-Cypher in een toelichtend commentaar
///   maakte de guard juist ten onrechte rood.</item>
/// <item>KETENS. <c>MERGE (a)-[:X]-&gt;(b)-[:Y]-&gt;(c)</c> leverde alleen
///   <c>X</c>. Geldige, idiomatische Cypher waarmee elke edge ná de eerste
///   onzichtbaar was.</item>
/// <item>GENESTE HAKEN. <c>[^)]*</c> kan niet over
///   <c>MERGE (child {norm: toLower(p.child)})-[:PART_OF]-&gt;(parent)</c> heen,
///   dus een gewone refactor gaf VALS ALARM ("geen enkele projectie schrijft die
///   edge nog"). Precies het gedrag waardoor een guard binnen een maand wordt
///   uitgezet.</item>
/// </list>
/// Daarnaast kapte <c>[A-Z_]+</c> op het eerste cijfer, waardoor <c>ABOUT</c>
/// hernoemen naar <c>ABOUT2</c> groen bleef: de scanner las <c>ABOUT</c>, en dat
/// stond in de catalogus. Een stille alias op een bestaande entry — de
/// #274-driftklasse zelf, alleen met een cijfer.
///
/// Deze scanner is daarom een echte, kleine tokenizer: eerst commentaar en
/// stringliteralen wegstrippen, dan vanaf elk <c>MERGE</c>/<c>CREATE</c>-keyword
/// de hele patroonketen aflopen met gebalanceerde haakjes. Hij blijft
/// opmaak-ongevoelig (witruimte, regelafbrekingen, aliassen, richting) — dat was
/// en blijft de kern van de guard.
///
/// ALIAS-BINDING (#289 PR 2). Om labels te kunnen melden moet de scanner weten wat
/// <c>c</c> is in <c>MERGE (c)-[:HAS_TAG]-&gt;(t)</c>, en dat staat meestal in een
/// EERDERE clausule van hetzelfde statement
/// (<c>MATCH (c:Card {id: p.id})</c>). Er lopen daarom twee passes over dezelfde
/// gestripte tekst: eerst binden alle patroonketens achter
/// <c>MATCH</c>/<c>MERGE</c>/<c>CREATE</c> hun aliassen aan labels, daarna leest de
/// tweede pass alleen de schrijf-clausules en lost de eindpunten op. Drie regels:
/// <list type="bullet">
/// <item>De binding is STATEMENT-scoped. Elke aanroep krijgt één Cypher-string, dus
///   een alias uit een ander statement lekt per constructie niet — <c>c</c> is
///   <c>:Card</c> in de kaart-projectie en <c>:Condition</c> in de
///   conditie-projectie, en die twee mogen elkaar niet raken.</item>
/// <item>Een alias zonder label is ONBEPAALD, geen fout: <c>MATCH (a {ref: …})</c>
///   levert een lege labellijst. Dat is precies wat <c>DERIVED_FROM</c> doet, en de
///   guard registreert het als "niet te garanderen" in plaats van als schending.</item>
/// <item>Aliassen worden over het statement heen GEUNIEERD, niet overschreven —
///   anders zou de latere, label-loze vermelding in
///   <c>MERGE (c)-[:FROM_SET]-&gt;(s)</c> de eerdere binding wissen.</item>
/// </list>
/// Alleen patronen in KETEN-positie tellen mee voor de patroon-binding. Een
/// haakjes-groep in een predicaat (<c>WHERE (n:Set OR n:Domain)</c>) wordt dus niet
/// gelezen als knooppatroon: de walker begint bij een keyword en volgt van daaraf
/// alleen <c>-[…]-</c>-verbindingen.
///
/// WHERE-LABEL-DISJUNCTIES (#317). Een label-loze ref-match kán wel degelijk labels
/// afdwingen — via het predicaat: <c>MATCH (a {ref: row.from})
/// WHERE (a:Card OR a:Mechanic OR …)</c> garandeert dat <c>a</c> ÉÉN van die
/// soorten is. Dat is een wezenlijk andere bewering dan een multi-label patroon
/// (dat ÁLLE labels garandeert), dus zo'n binding komt als DISJUNCTIEF terug
/// (<see cref="ProjectionEdgeShape.FromDisjunctive"/>). De lezing is bewust strikt,
/// want een te gulle lezing verzint garanties die het statement niet geeft:
/// een WHERE-lichaam wordt op topniveau over <c>AND</c> gesplitst, en een term
/// bindt alléén als hij (na het strippen van omsluitende haakjes, recursief over
/// <c>OR</c>) volledig bestaat uit <c>alias:Label</c>-atomen over ÉÉN alias. Zit er
/// iets anders tussen (een property-vergelijking, een <c>NOT</c>, een tweede alias
/// in dezelfde OR-groep), dan bindt die term niets — de andere termen nog wél.
/// Patroon-labels winnen bij het oplossen van een disjunctie: <c>MATCH (a:Card …)</c>
/// garandeert al meer dan elke disjunctie erbovenop.</summary>
internal static class CypherEdgeScanner
{
    private static readonly string[] WriteKeywords = ["MERGE", "CREATE"];
    private static readonly string[] PatternKeywords = ["MATCH", "MERGE", "CREATE"];
    private static readonly string[] WhereKeyword = ["WHERE"];
    private static readonly string[] AndKeyword = ["AND"];
    private static readonly string[] OrKeyword = ["OR"];

    /// <summary>De clausule-keywords waarop een WHERE-lichaam eindigt. Ruim
    /// genomen: een woord dat hier ten onrechte in staat kapt het lichaam alleen
    /// maar eerder af (en een afgekapte term bindt niets), terwijl een ontbrekend
    /// woord Cypher uit de VOLGENDE clausule als predicaat zou laten meelezen.</summary>
    private static readonly string[] ClauseKeywords =
    [
        "MATCH", "OPTIONAL", "MERGE", "CREATE", "WITH", "RETURN", "UNWIND", "SET",
        "REMOVE", "DELETE", "DETACH", "FOREACH", "CALL", "UNION", "ORDER", "SKIP",
        "LIMIT", "ON", "WHERE",
    ];

    /// <summary>Elke geschreven edge-naam, in voorkomen-volgorde (duplicaten
    /// behouden — de aanroeper bepaalt zelf of hij als verzameling telt).</summary>
    public static IReadOnlyList<string> WrittenEdges(string? cypher) =>
        [.. WrittenEdgeShapes(cypher).Select(s => s.EdgeName)];

    /// <summary>Elke geschreven edge MÉT de knooplabels aan weerszijden, in
    /// voorkomen-volgorde. Een lege labellijst betekent "de projectie legt op die
    /// kant geen label op" — zie <see cref="ProjectionEdgeShape"/>.</summary>
    public static IReadOnlyList<ProjectionEdgeShape> WrittenEdgeShapes(string? cypher) =>
        Scan(cypher, StripNoise);

    /// <summary>Elke edge-naam die LETTERLIJK in C#-broncode staat, met alleen de
    /// C#-commentaren weggestript (stringliteralen blijven juist staan — daar zit de
    /// Cypher in).
    ///
    /// Bedoeld voor één gerichte vraag (#289-review, F1): staat er Cypher in de
    /// broncode die tijdens de probe NOOIT is uitgevoerd? Een runtime-opname kan een
    /// tak die hij niet neemt per constructie niet waarnemen, dus een statement achter
    /// een env-vlag of een <c>ManagedSettings</c>-toggle (#254 — juist de manier die
    /// CLAUDE.md voorschrijft) zou anders volledig onzichtbaar blijven: niet in het
    /// corpus, dus ook niet in G1.
    ///
    /// De toets is bewust ÉÉNRICHTING (bron ⊆ uitgevoerd). Een geïnterpoleerde
    /// edge-naam (<c>-[:{relation.EdgeName}]-&gt;</c>) is hier per definitie niet
    /// zichtbaar en wordt dus niet getoetst — die kant dekt G1/G2 op de runtime-opname
    /// af. Verhuist een statement naar een ander bestand, dan verdwijnt het hier maar
    /// blijft het uitgevoerd: geen vals alarm.
    ///
    /// LET OP: alleen NAMEN. De labels die deze scan zou opleveren zijn onbruikbaar,
    /// want een heel bronbestand is géén statement: de aliassen van álle statements
    /// zouden op één hoop komen (<c>c</c> is in <c>GraphSyncService</c> zowel
    /// <c>:Card</c> als <c>:Condition</c>), en losse Cypher-fragmenten die pas bij het
    /// aanroepen worden samengesteld (<c>RunPairsAsync</c> plakt de
    /// <c>MATCH (c:Card …)</c>-prefix er zelf voor) zouden als label-loos binnenkomen.
    /// Zie ARCHITECTURE §6.3 voor het restrisico dat daarmee blijft staan.</summary>
    public static IReadOnlyList<string> WrittenEdgesInSource(string? csharpSource) =>
        [.. Scan(csharpSource, StripComments).Select(s => s.EdgeName)];

    private static IReadOnlyList<ProjectionEdgeShape> Scan(string? text, Func<string, string> strip)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var s = strip(text);
        return ScanWrites(s, BindAliases(s));
    }

    /// <summary>Alleen commentaar weg, stringliteralen ongemoeid. Een uitgezet
    /// statement (<c>// await tx.RunAsync(…)</c>) verdwijnt zo uit de bron-kant, net
    /// zoals het uit de uitgevoerde kant verdwijnt — beide kanten blijven consistent
    /// en G2 doet daar zijn werk.</summary>
    private static string StripComments(string s)
    {
        var b = new StringBuilder(s);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
                while (i < s.Length && s[i] != '\n') b[i++] = ' ';
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                b[i++] = ' '; b[i++] = ' ';
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                {
                    if (s[i] != '\n') b[i] = ' ';
                    i++;
                }
                if (i < s.Length) b[i++] = ' ';
                if (i < s.Length) b[i++] = ' ';
            }
            else i++;
        }
        return b.ToString();
    }

    /// <summary>Vervangt commentaar (<c>//</c>, <c>/* */</c>) en stringliteralen
    /// (<c>'…'</c>, <c>"…"</c>, backtick-identifiers) door spaties, met behoud van
    /// lengte zodat posities blijven kloppen. Zo kan noch uitgecommentarieerde
    /// Cypher meetellen, noch een haakje/aanhalingsteken in een literal de
    /// haakjes-balans verstoren.</summary>
    private static string StripNoise(string s)
    {
        var b = new StringBuilder(s);
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') b[i++] = ' ';
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                b[i++] = ' '; b[i++] = ' ';
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/'))
                {
                    if (s[i] != '\n') b[i] = ' ';
                    i++;
                }
                if (i < s.Length) b[i++] = ' ';
                if (i < s.Length) b[i++] = ' ';
            }
            else if (c is '\'' or '"' or '`')
            {
                var quote = c;
                b[i++] = ' ';
                while (i < s.Length && s[i] != quote)
                {
                    // Backslash-escape: het volgende teken hoort nog bij de literal.
                    if (s[i] == '\\' && i + 1 < s.Length) { b[i] = ' '; i++; }
                    if (i < s.Length) { if (s[i] != '\n') b[i] = ' '; i++; }
                }
                if (i < s.Length) b[i++] = ' ';
            }
            else i++;
        }
        return b.ToString();
    }

    // ── Pass 1: alias → labels ────────────────────────────────────────────────

    /// <summary>De twee soorten binding die één statement kan geven:
    /// <see cref="Certain"/> uit knooppatronen (de knoop DRAAGT die labels) en
    /// <see cref="Disjunctive"/> uit een WHERE-label-disjunctie (de knoop is ÉÉN
    /// van die labels, #317). Ze mogen niet op één hoop: de guard toetst ze
    /// verschillend (één-past vs. álle-passen).</summary>
    private sealed record AliasBindings(
        Dictionary<string, List<string>> Certain,
        Dictionary<string, List<string>> Disjunctive);

    /// <summary>Bindt élke alias in het statement aan de labels waarmee hij érgens
    /// in datzelfde statement voorkomt. Bewust een UNIE: dezelfde alias komt in een
    /// schrijf-clausule vaak label-loos terug
    /// (<c>MERGE (c:Card …) … MERGE (c)-[:FROM_SET]-&gt;(s)</c>), en die tweede
    /// vermelding mag de eerste niet uitwissen. Daarnaast de WHERE-disjuncties;
    /// twee disjuncties op dezelfde alias uniëren óók — een unie is een ruimere
    /// "één van deze"-bewering, dus de toets wordt er hooguit strenger van, nooit
    /// milder.</summary>
    private static AliasBindings BindAliases(string s)
    {
        var certain = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (nodes, _) in Chains(s, PatternKeywords))
            foreach (var node in nodes)
            {
                if (node.Alias is not { } alias || node.Labels.Count == 0) continue;
                Union(certain, alias, node.Labels);
            }

        var disjunctive = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (alias, labels) in WhereDisjunctions(s))
            Union(disjunctive, alias, labels);

        return new AliasBindings(certain, disjunctive);
    }

    private static void Union(
        Dictionary<string, List<string>> bound, string alias, IReadOnlyList<string> add)
    {
        if (!bound.TryGetValue(alias, out var labels))
            bound[alias] = labels = [];
        foreach (var label in add)
            if (!labels.Contains(label, StringComparer.Ordinal)) labels.Add(label);
    }

    // ── Pass 2: de geschreven edges met hun opgeloste eindpunten ──────────────

    private static List<ProjectionEdgeShape> ScanWrites(string s, AliasBindings aliases)
    {
        var shapes = new List<ProjectionEdgeShape>();
        foreach (var (nodes, links) in Chains(s, WriteKeywords))
            for (var n = 0; n < links.Count; n++)
            {
                var left = Resolve(nodes[n], aliases);
                var right = Resolve(nodes[n + 1], aliases);
                var (from, to) = links[n].Reversed ? (right, left) : (left, right);
                foreach (var type in links[n].Types)
                    shapes.Add(new ProjectionEdgeShape(type, from.Labels, to.Labels)
                    {
                        FromDisjunctive = from.Disjunctive,
                        ToDisjunctive = to.Disjunctive,
                    });
            }
        return shapes;
    }

    /// <summary>Patroon-labels (eigen + gebonden) winnen: die zijn een hardere
    /// garantie dan elke disjunctie erbovenop. Pas als een knoop nérgens een
    /// patroon-label heeft, telt zijn WHERE-disjunctie als eindpunt-binding.</summary>
    private static (IReadOnlyList<string> Labels, bool Disjunctive) Resolve(
        ChainNode node, AliasBindings aliases)
    {
        var labels = new List<string>(node.Labels);
        if (node.Alias is { } alias && aliases.Certain.TryGetValue(alias, out var bound))
            foreach (var label in bound)
                if (!labels.Contains(label, StringComparer.Ordinal)) labels.Add(label);
        if (labels.Count > 0) return (labels, false);

        if (node.Alias is { } a && aliases.Disjunctive.TryGetValue(a, out var options)
            && options.Count > 0)
            return (new List<string>(options), true);

        return (labels, false);
    }

    // ── WHERE-label-disjuncties (#317) ────────────────────────────────────────

    /// <summary>Elke (alias, labels)-binding die een WHERE-predicaat in dit
    /// statement afdwingt. Eén binding per AND-term die volledig uit
    /// <c>alias:Label</c>-atomen over één alias bestaat; alle andere termen binden
    /// niets (strikt, want een verzonnen garantie is erger dan een gemiste — een
    /// gemiste komt als "niet te garanderen" terug, luidruchtig genoeg).</summary>
    private static IEnumerable<(string Alias, IReadOnlyList<string> Labels)> WhereDisjunctions(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            if (!IsKeywordAt(s, i, WhereKeyword, out var afterKeyword)) { i++; continue; }

            var end = WhereBodyEnd(s, afterKeyword);
            foreach (var term in SplitTopLevel(s[afterKeyword..end], AndKeyword))
            {
                var atoms = new List<(string Alias, string Label)>();
                if (!TryLabelAtoms(term, atoms) || atoms.Count == 0) continue;

                // Eén disjunctie over TWEE aliassen ((a:Card OR b:Tag)) dwingt
                // geen van beide af — de knoop mag immers de "andere" tak zijn.
                var alias = atoms[0].Alias;
                if (atoms.Any(a => !string.Equals(a.Alias, alias, StringComparison.Ordinal)))
                    continue;

                yield return (alias,
                    atoms.Select(a => a.Label).Distinct(StringComparer.Ordinal).ToList());
            }
            i = end;
        }
    }

    /// <summary>Het einde van een WHERE-lichaam: het eerstvolgende clausule-keyword
    /// op haakjes-diepte 0, of de omsluitende sluithaak (een WHERE ín een
    /// <c>FOREACH (…)</c> eindigt bij diens haak).</summary>
    private static int WhereBodyEnd(string s, int afterKeyword)
    {
        var i = SkipWs(s, afterKeyword);
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (depth == 0 && !InLabelOfPropertyPositie(s, i)
                     && IsKeywordAt(s, i, ClauseKeywords, out _))
                break;
            i++;
        }
        return i;
    }

    /// <summary>Splitst een expressie op topniveau (buiten élke haak) op het
    /// gegeven keyword. Levert de segmenten ertussen, in volgorde.</summary>
    private static List<string> SplitTopLevel(string expr, string[] keyword)
    {
        var parts = new List<string>();
        var depth = 0;
        var segStart = 0;
        var i = 0;
        while (i < expr.Length)
        {
            var c = expr[i];
            if (c is '(' or '[' or '{') { depth++; i++; }
            else if (c is ')' or ']' or '}') { depth--; i++; }
            else if (depth == 0 && !InLabelOfPropertyPositie(expr, i)
                     && IsKeywordAt(expr, i, keyword, out var after))
            {
                parts.Add(expr[segStart..i]);
                segStart = after;
                i = after;
            }
            else i++;
        }
        parts.Add(expr[segStart..]);
        return parts;
    }

    /// <summary>Een woord dat direct na <c>:</c> (label) of <c>.</c> (property)
    /// staat is een NAAM, geen keyword — <c>n:Set</c> bevat het label Set, niet de
    /// SET-clausule, en <c>a.limit</c> is een property. Zonder deze uitzondering
    /// zou het WHERE-lichaam midden in <c>n:Set OR …</c> afkappen en de hele
    /// disjunctie stil laten vallen (gevonden doordat de Set-knopen van de
    /// kaart-projectie precies zo heten).</summary>
    private static bool InLabelOfPropertyPositie(string s, int i)
    {
        var j = i - 1;
        while (j >= 0 && char.IsWhiteSpace(s[j])) j--;
        return j >= 0 && (s[j] == ':' || s[j] == '.');
    }

    /// <summary>Strip omsluitende haakjes zolang ze de HELE (getrimde) term
    /// omvatten: <c>((a:Card OR a:Tag))</c> → <c>a:Card OR a:Tag</c>. Een haak die
    /// niet alles omsluit ((a:Card) OR x.y) blijft staan — die is structuur, geen
    /// verpakking.</summary>
    private static string TrimOuterParens(string term)
    {
        var t = term.Trim();
        while (t.Length >= 2 && t[0] == '(' && SkipBalanced(t, 0, '(', ')') == t.Length)
            t = t[1..^1].Trim();
        return t;
    }

    /// <summary>Recursief over de OR-boom: waar is dit een zuivere
    /// label-disjunctie? Elke tak moet zelf weer een zuivere disjunctie of een
    /// kaal <c>alias:Label</c>-atoom zijn; één vreemde tak (property-vergelijking,
    /// NOT, functie-aanroep) maakt de hele term onbruikbaar als garantie.</summary>
    private static bool TryLabelAtoms(string term, List<(string Alias, string Label)> atoms)
    {
        var t = TrimOuterParens(term);
        if (t.Length == 0) return false;

        var parts = SplitTopLevel(t, OrKeyword);
        if (parts.Count > 1)
        {
            foreach (var part in parts)
                if (!TryLabelAtoms(part, atoms)) return false;
            return true;
        }

        return TryParseLabelAtom(t, atoms);
    }

    /// <summary>Exact <c>alias:Label</c>, niets ervoor of erna. Bewust ook GEEN
    /// tweede label (<c>a:Card:Token</c> is een conjunctie bínnen een disjunctie —
    /// dat kan de platte labellijst niet dragen, dus die term bindt niets).</summary>
    private static bool TryParseLabelAtom(string t, List<(string Alias, string Label)> atoms)
    {
        var i = SkipWs(t, 0);
        var aliasStart = i;
        while (i < t.Length && IsWordChar(t[i])) i++;
        if (i == aliasStart) return false;
        var alias = t[aliasStart..i];

        i = SkipWs(t, i);
        if (i >= t.Length || t[i] != ':') return false;
        i = SkipWs(t, i + 1);

        var labelStart = i;
        while (i < t.Length && IsWordChar(t[i])) i++;
        if (i == labelStart) return false;
        var label = t[labelStart..i];

        if (SkipWs(t, i) != t.Length) return false;
        atoms.Add((alias, label));
        return true;
    }

    // ── De tokenizer ──────────────────────────────────────────────────────────

    /// <summary>Eén knooppatroon: <c>(alias:Label1:Label2 {props})</c>. Alias en
    /// labels zijn allebei optioneel — <c>()</c>, <c>(:Card)</c> en <c>(a)</c>
    /// bestaan alle drie.</summary>
    private sealed record ChainNode(string? Alias, IReadOnlyList<string> Labels);

    /// <summary>Eén verbinding tussen twee opeenvolgende knopen.
    /// <paramref name="Reversed"/> markeert <c>&lt;-[…]-</c>: dan is de RECHTER knoop
    /// de bron. Een ongerichte verbinding (<c>--</c>) wordt als links→rechts gelezen;
    /// geen van beide projecties schrijft die vorm.</summary>
    private sealed record ChainLink(IReadOnlyList<string> Types, bool Reversed);

    /// <summary>Loopt de tekst af en levert elke patroonketen achter één van
    /// <paramref name="keywords"/> op. Een keten heeft altijd één knoop meer dan
    /// verbindingen.</summary>
    private static IEnumerable<(IReadOnlyList<ChainNode> Nodes, IReadOnlyList<ChainLink> Links)>
        Chains(string s, string[] keywords)
    {
        var i = 0;
        while (i < s.Length)
        {
            if (!IsKeywordAt(s, i, keywords, out var afterKeyword)) { i++; continue; }

            var start = SkipWs(s, afterKeyword);
            // Een keyword zonder patroon erachter (bv. de CREATE in "ON CREATE SET")
            // is geen clausule over knopen of edges.
            if (start >= s.Length || s[start] != '(') { i = afterKeyword; continue; }

            var nodes = new List<ChainNode>();
            var links = new List<ChainLink>();
            i = ParseChain(s, start, nodes, links);
            if (nodes.Count > 0) yield return (nodes, links);
        }
    }

    /// <summary>Loopt vanaf een knooppatroon de hele keten af
    /// (<c>(a)-[:X]-&gt;(b)&lt;-[:Y]-(c)--(d)</c>) en verzamelt de knopen én de
    /// verbindingen ertussen. Retourneert de index net ná het laatst geconsumeerde
    /// knooppatroon.</summary>
    private static int ParseChain(string s, int pos, List<ChainNode> nodes, List<ChainLink> links)
    {
        if (!TryParseNode(s, pos, out var first, out var afterNode)) return pos;
        nodes.Add(first);

        while (true)
        {
            var k = SkipWs(s, afterNode);
            var reversed = false;
            if (k < s.Length && s[k] == '<') { reversed = true; k = SkipWs(s, k + 1); }
            if (k >= s.Length || s[k] != '-') return afterNode;
            k = SkipWs(s, k + 1);

            var types = new List<string>();
            if (k < s.Length && s[k] == '[')
            {
                var afterBracket = SkipBalanced(s, k, '[', ']');
                ExtractTypes(s, k + 1, afterBracket - 1, types);
                k = SkipWs(s, afterBracket);
            }

            if (k >= s.Length || s[k] != '-') return afterNode;
            k++;
            if (k < s.Length && s[k] == '>') k++;
            k = SkipWs(s, k);

            if (!TryParseNode(s, k, out var next, out var afterNext)) return afterNode;
            links.Add(new ChainLink(types, reversed));
            nodes.Add(next);
            afterNode = afterNext;
        }
    }

    /// <summary>Parseert één knooppatroon. Bewust STRIKT: na de optionele alias mag
    /// alleen <c>:</c> (labels), <c>{</c> (properties) of het sluithaakje volgen.
    /// Zo wordt een functie-aanroep of expressie tussen haakjes
    /// (<c>toLower(p.child)</c>, <c>FOREACH (x IN $rows | …)</c>,
    /// <c>WHERE (a AND b)</c>) niet als knoop gelezen — anders zouden er nep-aliassen
    /// gebonden raken en zou de labeltoets ruis gaan melden.</summary>
    private static bool TryParseNode(string s, int pos, out ChainNode node, out int after)
    {
        node = new ChainNode(null, []);
        after = pos;
        if (pos >= s.Length || s[pos] != '(') return false;

        var end = SkipBalanced(s, pos, '(', ')');
        if (end > s.Length || end <= pos + 1) return false;
        var close = end - 1;                      // index van het sluithaakje

        var i = SkipWs(s, pos + 1);
        var aliasStart = i;
        while (i < close && IsWordChar(s[i])) i++;
        var alias = i > aliasStart ? s[aliasStart..i] : null;

        var j = SkipWs(s, i);
        if (j < close && s[j] != ':' && s[j] != '{') return false;

        var labels = new List<string>();
        while (j < close && s[j] == ':')
        {
            j = SkipWs(s, j + 1);
            var from = j;
            while (j < close && IsWordChar(s[j])) j++;
            if (j == from) break;
            labels.Add(s[from..j]);
            j = SkipWs(s, j);
        }

        // Na de labels mag alleen nog een property-map of het sluithaakje komen.
        // Staat er iets anders — een label-EXPRESSIE als (a:Card|Mechanic), of een
        // inline WHERE — dan garandeert het patroon die labels NIET conjunctief;
        // lees ze dan als onbepaald in plaats van als valse garantie. Onbepaald is
        // luidruchtig (de guard meldt "niet te garanderen"), een valse garantie is
        // stil — en stil is precies wat deze scanner moet voorkomen.
        if (j < close && s[j] != '{') labels.Clear();

        node = new ChainNode(alias, labels);
        after = end;
        return true;
    }

    private static bool IsKeywordAt(string s, int i, string[] keywords, out int afterKeyword)
    {
        afterKeyword = i;
        foreach (var kw in keywords)
        {
            if (i + kw.Length > s.Length) continue;
            if (string.Compare(s, i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
            if (i > 0 && IsWordChar(s[i - 1])) continue;
            var end = i + kw.Length;
            if (end < s.Length && IsWordChar(s[end])) continue;
            afterKeyword = end;
            return true;
        }
        return false;
    }

    /// <summary>Relatietypes uit de binnenkant van <c>[…]</c>. Alleen het deel
    /// vóór een eventuele property-map telt: in <c>[r:HAS_ROLE {role: row.role}]</c>
    /// is <c>role: row.role</c> een property, geen tweede type. Meerdere types
    /// (<c>[:X|Y]</c>) worden allemaal meegenomen.</summary>
    private static void ExtractTypes(string s, int start, int end, List<string> edges)
    {
        var brace = s.IndexOf('{', start);
        if (brace >= 0 && brace < end) end = brace;

        var i = start;
        while (i < end)
        {
            if (s[i] is ':' or '|')
            {
                i = SkipWs(s, i + 1);
                var from = i;
                if (i < end && (char.IsLetter(s[i]) || s[i] == '_'))
                {
                    while (i < end && IsWordChar(s[i])) i++;
                    edges.Add(s[from..i]);
                    continue;
                }
            }
            else i++;
        }
    }

    private static int SkipBalanced(string s, int pos, char open, char close)
    {
        var depth = 0;
        while (pos < s.Length)
        {
            if (s[pos] == open) depth++;
            else if (s[pos] == close)
            {
                depth--;
                if (depth == 0) return pos + 1;
            }
            pos++;
        }
        return pos;
    }

    private static int SkipWs(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
