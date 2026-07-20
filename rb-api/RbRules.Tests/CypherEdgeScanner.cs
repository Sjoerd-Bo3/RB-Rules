using System.Text;

namespace RbRules.Tests;

/// <summary>Haalt uit een Cypher-statement élke edge-naam die het statement
/// SCHRIJFT (dus achter <c>MERGE</c>/<c>CREATE</c>, niet achter <c>MATCH</c>).
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
/// en blijft de kern van de guard.</summary>
internal static class CypherEdgeScanner
{
    /// <summary>Elke geschreven edge-naam, in voorkomen-volgorde (duplicaten
    /// behouden — de aanroeper bepaalt zelf of hij als verzameling telt).</summary>
    public static IReadOnlyList<string> WrittenEdges(string? cypher)
    {
        var edges = new List<string>();
        if (string.IsNullOrEmpty(cypher)) return edges;

        var s = StripNoise(cypher);
        var i = 0;
        while (i < s.Length)
        {
            if (!IsWriteKeywordAt(s, i, out var afterKeyword)) { i++; continue; }

            var start = SkipWs(s, afterKeyword);
            // Een keyword zonder patroon erachter (bv. de CREATE in "ON CREATE SET")
            // is geen schrijf-clausule over edges.
            if (start >= s.Length || s[start] != '(') { i = afterKeyword; continue; }

            i = ScanPatternChain(s, start, edges);
        }
        return edges;
    }

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
    /// blijft het uitgevoerd: geen vals alarm.</summary>
    public static IReadOnlyList<string> WrittenEdgesInSource(string? csharpSource)
    {
        var edges = new List<string>();
        if (string.IsNullOrEmpty(csharpSource)) return edges;

        var s = StripComments(csharpSource);
        var i = 0;
        while (i < s.Length)
        {
            if (!IsWriteKeywordAt(s, i, out var afterKeyword)) { i++; continue; }
            var start = SkipWs(s, afterKeyword);
            if (start >= s.Length || s[start] != '(') { i = afterKeyword; continue; }
            i = ScanPatternChain(s, start, edges);
        }
        return edges;
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

    private static bool IsWriteKeywordAt(string s, int i, out int afterKeyword)
    {
        afterKeyword = i;
        foreach (var kw in (string[])["MERGE", "CREATE"])
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

    /// <summary>Loopt vanaf een knooppatroon de hele keten af
    /// (<c>(a)-[:X]-&gt;(b)&lt;-[:Y]-(c)--(d)</c>) en verzamelt élk relatietype
    /// onderweg. Retourneert de index net ná het laatst geconsumeerde
    /// knooppatroon.</summary>
    private static int ScanPatternChain(string s, int pos, List<string> edges)
    {
        while (true)
        {
            if (pos >= s.Length || s[pos] != '(') return pos;
            var afterNode = SkipBalanced(s, pos, '(', ')');

            var k = SkipWs(s, afterNode);
            if (k < s.Length && s[k] == '<') k = SkipWs(s, k + 1);
            if (k >= s.Length || s[k] != '-') return afterNode;
            k = SkipWs(s, k + 1);

            if (k < s.Length && s[k] == '[')
            {
                var afterBracket = SkipBalanced(s, k, '[', ']');
                ExtractTypes(s, k + 1, afterBracket - 1, edges);
                k = SkipWs(s, afterBracket);
            }

            if (k >= s.Length || s[k] != '-') return afterNode;
            k++;
            if (k < s.Length && s[k] == '>') k++;
            k = SkipWs(s, k);

            if (k >= s.Length || s[k] != '(') return afterNode;
            pos = k;
        }
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
