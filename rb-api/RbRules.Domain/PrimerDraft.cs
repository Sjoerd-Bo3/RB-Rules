namespace RbRules.Domain;

/// <summary>Hoe een (her)generatie een primer-doc beschrijft (#266). Puur en
/// op één plek, omdat hier de koppeling ligt die twee waarheden voorkomt: de
/// canonieke Engelse tekst en de Nederlandse weergave worden ALTIJD samen
/// vervangen, en het doc gaat daarbij terug naar draft — de beheerder keurt
/// dus altijd het paar goed dat de bezoeker straks ziet.</summary>
public static class PrimerDraft
{
    /// <summary>Schrijft een vers gegenereerd concept in het doc. Een
    /// ontbrekende vertaling (<paramref name="bodyNl"/> = null, bijvoorbeeld
    /// bij AI-uitval of een afgekeurde vertaling) WIST een eerdere Nederlandse
    /// tekst: die hoorde bij de oude Engelse body en zou er anders als tweede
    /// waarheid naast blijven staan.</summary>
    public static void Apply(
        KnowledgeDoc doc, string title, string body, string? bodyNl,
        string sectionRefs, DateTimeOffset now)
    {
        doc.Title = title;
        doc.Body = body.Trim();
        doc.BodyNl = string.IsNullOrWhiteSpace(bodyNl) ? null : bodyNl.Trim();
        doc.SectionRefs = sectionRefs;
        doc.Status = "draft"; // her-generatie vraagt opnieuw om review
        doc.UpdatedAt = now;
    }
}
