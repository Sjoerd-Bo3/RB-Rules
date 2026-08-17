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
/// embedding laten, en dat is precies de stille degradatie die #282 opheft.
///
/// MAAR ALLEEN GEEN INPUT VERDWIJNEN IS NIET GENOEG (#293): zo'n uitschieter in zijn
/// eentje versturen redt hem niet als hij zélf boven de klip ligt — dan valt
/// llama-server om op precies dat ene verzoek, elke run opnieuw. Vandaar
/// <see cref="CapItems"/>: vóór het batchen wordt elke tekst tot het budget
/// teruggebracht, zodat élk verzoek (ook dat van één te lange tekst) binnen het
/// gemeten veilige bereik valt. Afkappen is hier beter dan overslaan, omdat een
/// overgeslagen tekst in beide pijplijnen permanent blijft terugkomen: de
/// regel-pijplijn is alles-of-niets per bron (één te lange chunk zou de hele
/// regelindex van die bron voor altijd blokkeren) en de kaart-pijplijn pakt kaarten
/// zonder embedding elke run opnieuw op (dus elke run dezelfde OOM-kill). En afkappen
/// gebeurt NOOIT stil — de pijplijnen melden hoeveel teksten en op welke lengte,
/// zelfde afspraak als #282/#284.</summary>
public static class EmbedBatching
{
    /// <summary>Het resultaat van <see cref="CapItems"/>.</summary>
    /// <param name="Texts">De teksten, elk hoogstens <c>maxChars</c> lang.</param>
    /// <param name="CappedCount">Hoeveel teksten daadwerkelijk zijn ingekort. 0 = de
    /// normale toestand; alles daarboven hoort in de run-melding.</param>
    /// <param name="LongestOriginal">De lengte van de langste ORIGINELE tekst — óók als
    /// er niets gekapt is. Bedraad in de run-melding sinds #302: "12 chunk(s) afgekapt
    /// op 6000 tekens (langste invoer 20000)" zegt hoe ver eroverheen we zaten en dus of
    /// het budget knelt of ruim zit; alleen de kaplengte zegt dat niet. Let op de
    /// samenhang die dat getal bruikbaar maakt: zodra er íets gekapt is, is de langste
    /// originele tekst per definitie zélf een gekapte — hij ligt immers boven het
    /// budget. De melding noemt hem dus alleen bij <see cref="CappedCount"/> &gt; 0, waar
    /// hij ook echt over de kapping gaat.</param>
    public sealed record CappedItems(
        IReadOnlyList<string> Texts, int CappedCount, int LongestOriginal);

    /// <summary>Kap elke tekst op <paramref name="maxChars"/>. Nodig omdat geen enkele
    /// bovenliggende laag een harde lengtegarantie geeft:
    /// <c>RuleSectionParser.MaxSectionLength</c> (2400) is een streefwaarde — die
    /// splitst op zinsgrens en laat één zin die zelf langer is héél, en Card Errata
    /// heeft in de praktijk al een chunk van 3908 tekens.
    ///
    /// Afkappen op tekens, niet op woorden: dit is een noodrem voor een pathologisch
    /// lange tekst (een tabeldump zonder punten), geen tekstverwerking. Wel netjes op
    /// een codepoint-grens, zodat er geen halve surrogate pair in de JSON belandt.</summary>
    public static CappedItems CapItems(IReadOnlyList<string> texts, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);

        var longest = 0;
        var capped = 0;
        var result = new List<string>(texts.Count);
        foreach (var raw in texts)
        {
            var text = raw ?? "";
            if (text.Length > longest) longest = text.Length;
            if (text.Length <= maxChars) { result.Add(text); continue; }
            capped++;
            result.Add(text[..CutAt(text, maxChars)]);
        }
        return new(result, capped, longest);
    }

    /// <summary>Knip nooit tussen een high- en low-surrogate in.</summary>
    private static int CutAt(string text, int maxChars) =>
        char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;

    /// <summary>Deel <paramref name="texts"/> op in verzoeken van hoogstens
    /// <paramref name="maxCount"/> teksten en ongeveer <paramref name="maxChars"/>
    /// tekens. Volgorde blijft behouden — de aanroeper koppelt de vectoren op index
    /// terug aan zijn entiteiten.
    ///
    /// "Ongeveer" slaat op de uitschieter: een tekst die op zichzelf al boven het budget
    /// zit gaat alléén mee, en dan is die ene batch groter dan <paramref name="maxChars"/>.
    /// Draai <see cref="CapItems"/> er met hetzelfde budget vóór (wat beide pijplijnen
    /// doen sinds #293) en die uitzondering bestaat niet meer: dan is élke batch
    /// gegarandeerd ≤ <paramref name="maxChars"/> tekens.</summary>
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
