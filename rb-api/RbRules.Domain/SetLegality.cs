namespace RbRules.Domain;

/// <summary>Legaliteitsstatus van een set — en dus van zijn kaarten — in
/// constructed (#22).</summary>
public enum SetLegalityStatus
{
    /// <summary>Set is verschenen: kaarten zijn legaal.</summary>
    Legal,
    /// <summary>Releasedatum bekend en in de toekomst: nog niet legaal.</summary>
    Upcoming,
    /// <summary>Geen releasedatum bekend. Let op: dit onderscheidt een écht
    /// aangekondigde set niet van een allang verschenen set waarvan de bron
    /// geen datum leverde (de Riot-gallery kent geen releasedatums) — verbind
    /// hier dus nooit een harde "niet legaal"-claim aan.</summary>
    Announced,
}

/// <summary>Set-legaliteit afgeleid van de releasedatum (#22). Aanname,
/// bewust simpel: de officiële bronnen publiceren geen aparte "legal
/// vanaf"-datum per set, dus PublishedOn (de releasedatum) is de grens —
/// op de releasedag zelf is de set legaal. 'Vandaag' is een parameter,
/// zodat de logica puur en unit-testbaar blijft.</summary>
public static class SetLegality
{
    public static SetLegalityStatus StatusFor(DateOnly? publishedOn, DateOnly today) =>
        publishedOn is null ? SetLegalityStatus.Announced
        : publishedOn.Value <= today ? SetLegalityStatus.Legal
        : SetLegalityStatus.Upcoming;

    /// <summary>Stabiele sleutel voor API-responses: "legal" | "upcoming" |
    /// "announced".</summary>
    public static string Key(SetLegalityStatus status) => status switch
    {
        SetLegalityStatus.Upcoming => "upcoming",
        SetLegalityStatus.Announced => "announced",
        _ => "legal",
    };

    /// <summary>Kaartfeit-suffix voor LLM-prompts (#68). Alleen bij een
    /// bekende toekomstige releasedatum doen we een expliciete claim; bij
    /// Announced zwijgen we — een onbekende datum kan net zo goed een allang
    /// verschenen set zijn, en een onterecht "nog niet legaal" is precies het
    /// soort misleiding dat dit feit moet voorkomen.</summary>
    public static string? PromptFact(DateOnly? publishedOn, DateOnly today, string? setName) =>
        StatusFor(publishedOn, today) is SetLegalityStatus.Upcoming
            ? $"NOG NIET LEGAAL — komt in set {setName ?? "?"} op {publishedOn!.Value:yyyy-MM-dd}."
            : null;
}
