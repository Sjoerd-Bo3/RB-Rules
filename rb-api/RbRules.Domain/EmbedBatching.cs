namespace RbRules.Domain;

/// <summary>Hoe een lijst teksten over embed-verzoeken verdeeld wordt (#282).
///
/// WAAROM DIT BESTAAT: de piek in <c>rb-v2-ollama</c> zit niet in het geladen model
/// (idle ~69 MiB) maar in het VERZOEK — llama.cpp houdt de activaties van álle
/// sequenties in één batch tegelijk vast, en die kosten schalen met het aantal
/// teksten én met hun lengte. Een vaste telling van 16 zegt daarom weinig: 16
/// kaartteksten (~300 tekens) is een fractie van 16 regel-secties (tot
/// <c>RuleSectionParser.MaxSectionLength</c> = 2400 tekens). Vandaar TWEE grenzen —
/// het aantal én het tekenbudget — waarbij de eerste die vol raakt de batch sluit.
///
/// GEEN INPUT VERDWIJNT: een enkele tekst die op zichzelf al boven het tekenbudget
/// zit gaat alléén mee in zijn eigen verzoek. Weglaten zou een kaart stil zonder
/// embedding laten, en dat is precies de stille degradatie die #282 opheft.</summary>
public static class EmbedBatching
{
    /// <summary>Deel <paramref name="texts"/> op in verzoeken van hoogstens
    /// <paramref name="maxCount"/> teksten en ongeveer <paramref name="maxChars"/>
    /// tekens. Volgorde blijft behouden — de aanroeper koppelt de vectoren op index
    /// terug aan zijn entiteiten.</summary>
    public static List<Range> Split(
        IReadOnlyList<string> texts, int maxCount, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);

        var batches = new List<Range>();
        var start = 0;
        var chars = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            var len = texts[i]?.Length ?? 0;
            // Sluit de lopende batch als deze tekst hem over een van beide grenzen
            // duwt. Een batch is nooit leeg: is i het begin, dan gaat de tekst mee
            // hoe lang hij ook is (zie klasse-toelichting).
            if (i > start && (i - start >= maxCount || chars + len > maxChars))
            {
                batches.Add(new Range(start, i));
                start = i;
                chars = 0;
            }
            chars += len;
        }
        if (start < texts.Count) batches.Add(new Range(start, texts.Count));
        return batches;
    }
}
