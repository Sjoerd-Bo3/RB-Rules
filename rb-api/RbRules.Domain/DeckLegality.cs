namespace RbRules.Domain;

/// <summary>Uitkomst van een legaliteitscheck (#15 fase 3, spoor A): drie
/// elkaar uitsluitende toestanden. "Illegal" wint van "Incomplete" — een
/// hard gevonden probleem (geband, of een aantoonbaar nog niet legale set)
/// is een sterkere uitspraak dan "we weten het niet zeker".</summary>
public enum DeckLegalityStatus
{
    Legal,
    Illegal,
    /// <summary>Onbekend/onvolledig: geen aangetoonde overtreding, maar niet
    /// alle kaarten zijn te beoordelen (niet-gekoppeld, of een set zonder
    /// bekende releasedatum — <see cref="SetLegalityStatus.Announced"/> mag
    /// nooit als "niet legaal" gelden, zie SetLegality).</summary>
    Incomplete,
}

/// <summary>Eén kaart die de legaliteit van het deck kapotmaakt.
/// <paramref name="Reason"/> is "not-yet-legal" (de set van de kaart is nog
/// niet verschenen) of "banned" (de kaart staat op de banlijst voor het
/// gevraagde format).</summary>
public record DeckLegalityIssue(string CardCode, string? CardName, string Reason)
{
    public const string NotYetLegal = "not-yet-legal";
    public const string Banned = "banned";
}

/// <summary>Eén kaartregel uit een deck met de platte feiten die de
/// legaliteit bepalen — losgekoppeld van EF-entiteiten zodat deze puur en
/// zonder database te testen is. De aanroeper (DeckBrowserService) doet de
/// joins naar Card/CardSet/BanEntry (per format) en geeft hier alleen platte
/// waarden door.</summary>
public record DeckLegalityCard(
    string CardCode, string? CardName,
    string? CanonicalRiftboundId, DateOnly? SetPublishedOn, bool Banned);

/// <summary>Gestructureerde legaliteitsuitkomst van een deck: status plus
/// (bij Illegal) de precieze overtredende kaart(en), en altijd het aantal
/// kaarten dat niet te beoordelen viel (onbekend of niet-gekoppeld).</summary>
public record DeckLegalityResult(
    DeckLegalityStatus Status, IReadOnlyList<DeckLegalityIssue> Issues, int UnknownCount)
{
    public static string Key(DeckLegalityStatus status) => status switch
    {
        DeckLegalityStatus.Illegal => "illegal",
        DeckLegalityStatus.Incomplete => "incomplete",
        _ => "legal",
    };
}

/// <summary>Deck-legaliteit (#15 fase 3, spoor A): een deck is legaal als al
/// zijn (gekoppelde) kaarten in een legale set zitten én geen enkele kaart op
/// de banlijst staat. Onbekende/niet-gekoppelde kaarten (CanonicalRiftboundId
/// null) maken een deck nooit "illegaal" — dat zou een claim zijn die de data
/// niet onderbouwt — ze tellen mee in UnknownCount en duwen de uitkomst naar
/// "onvolledig" zolang er geen harde overtreding is. Hetzelfde geldt voor een
/// set zonder bekende releasedatum (<see cref="SetLegalityStatus.Announced"/>):
/// dat is evenmin een harde "niet legaal"-uitspraak (zie SetLegality), dus
/// zo'n kaart telt als onbeoordeelbaar, niet als overtreding. Pure functie,
/// "vandaag" is een parameter voor testbaarheid (zelfde patroon als
/// SetLegality).</summary>
public static class DeckLegality
{
    public static DeckLegalityResult Evaluate(IReadOnlyList<DeckLegalityCard> cards, DateOnly today)
    {
        var issues = new List<DeckLegalityIssue>();
        var unknown = 0;
        foreach (var card in cards)
        {
            if (card.Banned)
            {
                issues.Add(new(card.CardCode, card.CardName, DeckLegalityIssue.Banned));
                continue;
            }
            if (card.CanonicalRiftboundId is null)
            {
                unknown++;
                continue;
            }
            switch (SetLegality.StatusFor(card.SetPublishedOn, today))
            {
                case SetLegalityStatus.Upcoming:
                    issues.Add(new(card.CardCode, card.CardName, DeckLegalityIssue.NotYetLegal));
                    break;
                case SetLegalityStatus.Announced:
                    // Onbekende releasedatum: kan een allang verschenen set zijn
                    // waarvan de bron geen datum leverde — geen harde claim.
                    unknown++;
                    break;
                case SetLegalityStatus.Legal:
                default:
                    break;
            }
        }

        if (issues.Count > 0) return new(DeckLegalityStatus.Illegal, issues, unknown);
        if (unknown > 0) return new(DeckLegalityStatus.Incomplete, [], unknown);
        return new(DeckLegalityStatus.Legal, [], 0);
    }
}
