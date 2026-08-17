namespace RbRules.Domain;

/// <summary>Wat er bij een upsert daadwerkelijk veranderde. De sync gebruikt
/// dit om de embedding en de gecachete gelijkenis-uitleg alléén bij een échte
/// wijziging te invalideren — anders churnt elke sync de hele kaartenset.</summary>
public readonly record struct CardChanges(bool NameChanged, bool TextChanged, bool Any);

/// <summary>De voorrangsregel bij de kaart-upsert (#150, doorgetrokken naar de
/// presentatievelden in #270), op één plek en zonder per-veld-
/// herkomstadministratie:
///
/// <list type="bullet">
/// <item><b>Leidend</b> (de officiële Riot-gallery) schrijft ONVOORWAARDELIJK
/// — ook een lege waarde. Ontbreekt een veld in Riots payload, dan hééft de
/// kaart het niet; dat is informatie, geen gat.</item>
/// <item><b>Aanvullend</b> (riftcodex) vult ALLEEN lege velden. Een gevulde
/// waarde blijft altijd staan, dus een aanvulling kan Riot-data nooit
/// beschadigen — de naamschade van vóór #150 kan zo niet terugkomen.</item>
/// </list>
///
/// Zodra Riot een waarde levert, overschrijft die de aanvulling dus vanzelf.
/// Let op: "aanvullend" gaat over de ROL in deze run, niet over de bron. Valt
/// Riot uit en is riftcodex de enige bron, dan is riftcodex leidend — anders
/// zou de kaartenset bevriezen zolang Riot plat ligt.</summary>
public static class CardMerge
{
    /// <summary>Neemt de bronvelden van <paramref name="incoming"/> over in
    /// <paramref name="target"/> volgens de voorrangsregel. Raakt uitsluitend
    /// bronvelden aan: onze eigen afgeleiden (embedding, mechanics/triggers/
    /// effects, variantgroepering, UpdatedAt) blijven van de pijplijnen en de
    /// aanroeper die ze beheren.</summary>
    public static CardChanges Apply(Card target, Card incoming, bool leading)
    {
        bool nameChanged = false, textChanged = false, any = false;

        target.Name = Text(target.Name, incoming.Name, leading, ref nameChanged) ?? target.Name;
        target.TextPlain = Text(target.TextPlain, incoming.TextPlain, leading, ref textChanged);

        target.Type = Text(target.Type, incoming.Type, leading, ref any);
        target.Supertype = Text(target.Supertype, incoming.Supertype, leading, ref any);
        target.Rarity = Text(target.Rarity, incoming.Rarity, leading, ref any);
        target.SetId = Text(target.SetId, incoming.SetId, leading, ref any);
        target.SetLabel = Text(target.SetLabel, incoming.SetLabel, leading, ref any);
        target.ImageUrl = Text(target.ImageUrl, incoming.ImageUrl, leading, ref any);
        target.Domains = Arr(target.Domains, incoming.Domains, leading, ref any);
        target.Tags = Arr(target.Tags, incoming.Tags, leading, ref any);
        target.Energy = Num(target.Energy, incoming.Energy, leading, ref any);
        target.Might = Num(target.Might, incoming.Might, leading, ref any);
        target.Power = Num(target.Power, incoming.Power, leading, ref any);
        target.CollectorNumber =
            Num(target.CollectorNumber, incoming.CollectorNumber, leading, ref any);

        // Presentatievelden (#270).
        target.PublicCode = Text(target.PublicCode, incoming.PublicCode, leading, ref any);
        target.Illustrator = Text(target.Illustrator, incoming.Illustrator, leading, ref any);
        target.EffectPlain = Text(target.EffectPlain, incoming.EffectPlain, leading, ref any);
        target.ImageColorPrimary =
            Text(target.ImageColorPrimary, incoming.ImageColorPrimary, leading, ref any);
        target.ImageColorSecondary =
            Text(target.ImageColorSecondary, incoming.ImageColorSecondary, leading, ref any);
        target.ImageAltText = Text(target.ImageAltText, incoming.ImageAltText, leading, ref any);
        target.MightBonus = Num(target.MightBonus, incoming.MightBonus, leading, ref any);
        target.ImageWidth = Num(target.ImageWidth, incoming.ImageWidth, leading, ref any);
        target.ImageHeight = Num(target.ImageHeight, incoming.ImageHeight, leading, ref any);
        target.Flags = Arr(target.Flags, incoming.Flags, leading, ref any);

        return new(nameChanged, textChanged, any || nameChanged || textChanged);
    }

    /// <summary>Leeg = null of alleen witruimte. Een aanvulling vult een gat
    /// alleen met een échte waarde: "" of witruimte laat het gat staan, zodat
    /// een latere bron het alsnog kan vullen.</summary>
    private static string? Text(string? current, string? next, bool leading, ref bool changed)
    {
        string? value;
        if (leading) value = next;
        else if (!string.IsNullOrWhiteSpace(current)) value = current;
        else value = string.IsNullOrWhiteSpace(next) ? current : next;
        if (!string.Equals(value, current, StringComparison.Ordinal)) changed = true;
        return value;
    }

    private static T? Num<T>(T? current, T? next, bool leading, ref bool changed)
        where T : struct
    {
        var value = leading || current is null ? next : current;
        if (!Nullable.Equals(value, current)) changed = true;
        return value;
    }

    private static string[] Arr(string[] current, string[] next, bool leading, ref bool changed)
    {
        var value = leading || current.Length == 0 ? next : current;
        if (!value.SequenceEqual(current, StringComparer.Ordinal)) changed = true;
        return value;
    }
}
