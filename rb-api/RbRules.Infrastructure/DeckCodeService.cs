using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Een gedecodeerde deck-code (#264): dezelfde vorm als een
/// deck-detail (secties met kaartregels + legaliteitsoordeel), maar zonder
/// PA-identiteit — dit deck staat nergens opgeslagen. <paramref
/// name="CardCount"/> is de som van de aantallen, <paramref
/// name="UnknownCount"/> het aantal regels dat wij niet aan een kaart konden
/// koppelen (zichtbaar signaal, geen fout).</summary>
public record DeckCodeDeck(
    IReadOnlyList<DeckSectionView> Sections, int CardCount, int UnknownCount,
    DeckLegalityView Legality);

/// <summary>Uitkomst van een decodeer-poging. Precies één van beide velden is
/// gevuld: een ongeldige code is data, geen exceptie die door het endpoint
/// heen mag lekken (dat zou een 500 opleveren waar een 400 hoort).</summary>
public record DeckCodeResult(DeckCodeDeck? Deck, string? Error);

/// <summary>Deck-code-import (#264): plak een deelbare deck-code en zie welk
/// deck erin zit, met hetzelfde legaliteitsoordeel als de deck-browser.
/// Sluit de al bestaande, maar tot nu toe niet-aangeroepen <see
/// cref="DeckCode"/>-port aan op het product. Bewust alleen import: de
/// mapping van onze zeven PA-secties naar de secties van het codeformaat is
/// nergens vastgelegd en de PA-payload bevat geen deck-code, dus een
/// gegenereerde code is niet tegen eigen data te toetsen (zie #264).</summary>
public class DeckCodeService(RbRulesDbContext db)
{
    /// <summary>De secties zoals het codeformaat ze kent — bewust níet de
    /// PA-sectienamen (legend/champions/battlefields/runes/bench): de code
    /// draagt die indeling niet, en doen alsof van wel zou een indeling
    /// suggereren die er niet in staat.</summary>
    public const string MainDeckSection = "maindeck";
    public const string SideboardSection = "sideboard";
    public const string ChosenChampionSection = "chosen-champion";

    /// <summary>Bovengrens op de invoer. De langste realistische code (main
    /// deck + sideboard + champion) blijft ruim onder de 200 tekens; deze
    /// grens houdt een geplakte megastring uit de decoder-lus.</summary>
    public const int MaxCodeLength = 2048;

    private static readonly string[] SectionOrder =
        [MainDeckSection, SideboardSection, ChosenChampionSection];

    /// <summary>Eén regel uit de gedecodeerde code, met de canonieke kaart
    /// erbij zodra de linker hem gevonden heeft (null = wij kennen hem niet).</summary>
    private sealed record DecodedEntry(
        string Section, string CardCode, int Count, string? Canonical = null);

    public async Task<DeckCodeResult> DecodeAsync(
        string? code, string format = DeckBrowserService.DefaultFormat,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new(null, "Plak eerst een deck-code.");
        if (code.Length > MaxCodeLength)
            return new(null, $"Deze invoer is te lang voor een deck-code (max {MaxCodeLength} tekens).");

        DeckList decoded;
        try
        {
            decoded = DeckCode.Decode(code);
        }
        catch (DeckCodeException ex)
        {
            // Waar DeckCodeException voor bedoeld is: elke ongeldige invoer
            // komt hier als uitlegbare boodschap terug, nooit als 500.
            return new(null, ex.Message);
        }

        List<DecodedEntry> entries =
        [
            .. decoded.MainDeck.Select(e => new DecodedEntry(MainDeckSection, e.CardCode, e.Count)),
            .. decoded.Sideboard.Select(e => new DecodedEntry(SideboardSection, e.CardCode, e.Count)),
            .. decoded.ChosenChampion is { } champion
                ? new DecodedEntry[] { new(ChosenChampionSection, champion, 1) }
                : [],
        ];

        // Kaartkoppeling: exact dezelfde weg als de PA-ingest (#15) — het
        // variantnummer uit de code is de printing-code van ons riftbound_id,
        // en het resultaat is altijd de canonieke kaart (variantgroepering).
        var linker = new DeckCardLinker(await db.Cards.AsNoTracking()
            .Select(c => new Card { RiftboundId = c.RiftboundId, Name = c.Name, VariantOf = c.VariantOf })
            .ToListAsync(ct));
        var linked = entries
            .Select(e => e with { Canonical = linker.ResolveCanonical(e.CardCode, null) })
            .ToList();

        var canonicalIds = linked.Select(l => l.Canonical).OfType<string>().Distinct().ToList();
        var cardFacts = await db.Cards.AsNoTracking()
            .Where(c => canonicalIds.Contains(c.RiftboundId))
            .Select(c => new { c.RiftboundId, c.Name, c.ImageUrl })
            .ToDictionaryAsync(c => c.RiftboundId, ct);

        var context = await DeckLegalityContext.LoadAsync(db, format, ct);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);
        var legality = DeckLegality.Evaluate(
            [.. linked.Select(l => context.ToLegalityCard(
                l.CardCode,
                l.Canonical is { } id ? cardFacts.GetValueOrDefault(id)?.Name : null,
                l.Canonical))],
            today);

        var sections = SectionOrder
            .Select(section => new DeckSectionView(
                section,
                [.. linked.Where(l => l.Section == section)
                    .Select(l =>
                    {
                        var fact = l.Canonical is { } id ? cardFacts.GetValueOrDefault(id) : null;
                        return new DeckCardView(l.CardCode, l.Count, l.Canonical, fact?.Name, fact?.ImageUrl);
                    })
                    .OrderBy(c => c.CardName ?? c.CardCode, StringComparer.Ordinal)]))
            .Where(s => s.Cards.Count > 0)
            .ToList();

        return new(
            new DeckCodeDeck(
                sections,
                linked.Sum(l => l.Count),
                linked.Count(l => l.Canonical is null),
                DeckLegalityView.From(legality)),
            null);
    }
}
