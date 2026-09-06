namespace RbRules.Domain;

/// <summary>Contextuele embed-invoer voor regelchunks (#385). Een chunk ging als
/// kale tekst de embedding in — "The defending player may…" weet dan niet dat
/// hij onder § 809 Deflect valt, en de vraag "hoe werkt Deflect" matcht op
/// woordniveau slechter dan nodig. Sinds #385 gaat er een korte context vóór de
/// tekst: de sectiecode, de bronnaam en de eerste zin van de bovenliggende
/// secties (Anthropic's contextual-retrieval-meting: −35% gemiste retrievals
/// met alleen deze stap). De opgeslagen <c>RuleChunk.Text</c> blijft
/// ongewijzigd — invoer ≠ opslag, dezelfde scheiding als bij de kap (#293).</summary>
public static class RuleChunkEmbedText
{
    /// <summary>Versie van de embed-invoervorm. Staat op de rij
    /// (<c>RuleChunk.EmbeddingVariant</c>) zodat de her-embed-job (#382) rijen
    /// met een oudere vorm — of zonder (null = kale tekst van vóór #385) —
    /// terugvindt. Bump bij élke wijziging van <see cref="Build"/>: anders
    /// staan twee invoervormen in één vectorruimte zonder dat iets rood wordt.</summary>
    public const int Variant = 1;

    /// <summary>Maximale lengte van één ouder-zin in de context; langer dan dit
    /// verdringt de eigen tekst uit het (gekapte) embed-venster.</summary>
    public const int MaxParentSentence = 160;

    /// <summary>Bouw de embed-invoer. <paramref name="parents"/> is de ouderketen
    /// van hoog naar laag (zoals <c>RuleSectionParser.ParentCodes</c>), elk met
    /// zijn tekst; ontbrekende ouders zijn al weggelaten door de aanroeper.
    /// Alleen de hoogste en de dichtstbijzijnde ouder gaan mee (bij één ouder één
    /// keer), als eerste zin: "§ 466.2.c (Core Rules) — Combat. — Blocking. — {tekst}".
    /// Zonder sectiecode (intro-chunk) alleen de bronnaam.</summary>
    public static string Build(
        string? sectionCode, string sourceName,
        IReadOnlyList<(string Code, string Text)> parents, string text)
    {
        var parts = new List<string>(4);
        var head = string.IsNullOrWhiteSpace(sectionCode)
            ? $"({sourceName})"
            : $"§ {sectionCode} ({sourceName})";
        parts.Add(head);
        if (parents.Count > 0)
        {
            var top = FirstSentence(parents[0].Text);
            if (top.Length > 0) parts.Add(top);
            if (parents.Count > 1)
            {
                var near = FirstSentence(parents[^1].Text);
                if (near.Length > 0 && !near.Equals(top, StringComparison.Ordinal)) parts.Add(near);
            }
        }
        parts.Add(text);
        return string.Join(" — ", parts);
    }

    /// <summary>De eerste zin van een sectietekst, begrensd op
    /// <see cref="MaxParentSentence"/> tekens (op woordgrens, met beletselteken).
    /// Een zin eindigt op . ! ? of een regeleinde.</summary>
    public static string FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        var end = -1;
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c is '\n' or '\r') { end = i; break; }
            if (c is '.' or '!' or '?')
            {
                // Een punt binnen een sectienummer ("601.2") is geen zinseinde.
                var next = i + 1 < t.Length ? t[i + 1] : ' ';
                if (char.IsWhiteSpace(next)) { end = i + 1; break; }
            }
        }
        var sentence = (end < 0 ? t : t[..end]).Trim();
        if (sentence.Length <= MaxParentSentence) return sentence;
        var cut = sentence.LastIndexOf(' ', MaxParentSentence);
        return (cut > 0 ? sentence[..cut] : sentence[..MaxParentSentence]).TrimEnd() + "…";
    }

    /// <summary>De ouderketen (hoog → laag) van een code uit een lookup van
    /// bestaande secties van dezelfde bron; ontbrekende tussenniveaus worden
    /// overgeslagen (een subregel onder een niet-gechunkte kop houdt dan alleen
    /// de ouders die er wél zijn).</summary>
    public static IReadOnlyList<(string Code, string Text)> ParentsFor(
        string? sectionCode, IReadOnlyDictionary<string, string> textByCode)
    {
        if (string.IsNullOrWhiteSpace(sectionCode)) return [];
        return [.. RuleSectionParser.ParentCodes(sectionCode)
            .Where(textByCode.ContainsKey)
            .Select(code => (code, textByCode[code]))];
    }
}
