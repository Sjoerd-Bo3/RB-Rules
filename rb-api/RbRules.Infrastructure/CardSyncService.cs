using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record CardSyncResult(int Sets, int Cards, string Source);

/// <summary>Kaart-sync: Riftcodex API → officiële Riot-gallery-fallback (die
/// datacenter-IP's niet blokkeert). Idempotente upsert; nieuwe sets komen
/// automatisch mee.</summary>
public class CardSyncService(RbRulesDbContext db, HttpClient http)
{
    private const string RiftcodexBase = "https://api.riftcodex.com";
    private const string RiotGallery = "https://playriftbound.com/en-us/card-gallery/";

    public async Task<CardSyncResult> SyncAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        // Opruimen: set-facetten die eerdere versies per abuis als kaart
        // importeerden (id zonder '-', bijv. 'VEN'/'OGN'). String-overload:
        // Npgsql vertaalt Contains(char) niet naar SQL.
        await db.Cards.Where(c => !c.RiftboundId.Contains("-")).ExecuteDeleteAsync(ct);

        var mode = Environment.GetEnvironmentVariable("CARD_SOURCE") ?? "auto";
        CardSyncResult result;
        if (mode == "riot")
        {
            result = await SyncFromRiotAsync(progress, ct);
        }
        else
        {
            try
            {
                result = await SyncFromRiftcodexAsync(progress, ct);
            }
            catch when (mode != "riftcodex")
            {
                progress?.Invoke("Riftcodex niet bereikbaar — overschakelen naar officiële Riot-gallery");
                result = await SyncFromRiotAsync(progress, ct);
            }
        }

        progress?.Invoke("varianten groeperen (alt-art/promo/herdruk)");
        await RegroupVariantsAsync(ct);
        return result;
    }

    /// <summary>Groepeert printings van dezelfde kaart: één canonieke kaart,
    /// de rest (alt-art/showcase/promo/herdruk) verwijst ernaar via VariantOf.
    /// Groepering op basisnaam: Riot noemt varianten "Naam (Alternate Art)",
    /// "(Signature)", "(Overnumbered)" — die horen bij dezelfde kaart. Nieuwe
    /// printings van bestaande namen (set 7-scenario, #57) groeperen zo
    /// automatisch; de keuze van de canonieke printing is gepind in
    /// VariantGrouping.ChooseCanonical zodat een herdruk geen churn geeft.</summary>
    private async Task RegroupVariantsAsync(CancellationToken ct)
    {
        var cards = await db.Cards.ToListAsync(ct);
        foreach (var group in cards.GroupBy(c => CardText.BaseName(c.Name)))
        {
            var members = group.ToList();
            var canonical = VariantGrouping.ChooseCanonical(members);
            foreach (var c in members)
            {
                var variantOf = ReferenceEquals(c, canonical) ? null : canonical.RiftboundId;
                if (c.VariantOf != variantOf) c.VariantOf = variantOf;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Alle bestaande kaarten één keer laden (review-fix #43: geen
    /// FindAsync per kaart — dat waren ~1250 losse SELECT's per sync).</summary>
    private async Task<Dictionary<string, Card>> LoadExistingAsync(CancellationToken ct) =>
        await db.Cards.ToDictionaryAsync(c => c.RiftboundId, ct);

    private async Task<CardSyncResult> SyncFromRiftcodexAsync(
        Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("setlijst ophalen bij Riftcodex");
        var existing = await LoadExistingAsync(ct);
        var setsNode = await GetJsonAsync($"{RiftcodexBase}/sets?page=1&size=100", ct);
        var sets = AsList(setsNode);
        var setIds = new List<string>();
        foreach (var s in sets.OfType<JsonObject>())
        {
            var setId = s["set_id"]?.GetValue<string>();
            if (setId is null) continue;
            await UpsertSetAsync(setId.ToUpperInvariant(), s["name"]?.GetValue<string>() ?? setId,
                s["card_count"]?.GetValue<int?>(), ParsePublishedOn(s["published_on"]), ct);
            setIds.Add(setId);
        }

        var total = 0;
        foreach (var setId in setIds)
        {
            for (var page = 1; page <= 200; page++)
            {
                progress?.Invoke($"set {setId}: pagina {page} ophalen ({total} kaarten verwerkt)");
                var node = await GetJsonAsync(
                    $"{RiftcodexBase}/cards?set_id={Uri.EscapeDataString(setId)}&page={page}&size=100", ct);
                var items = AsList(node);
                if (items.Count == 0) break;
                foreach (var c in items.OfType<JsonObject>())
                {
                    var card = MapRiftcodexCard(c, setId);
                    if (card is not null)
                    {
                        await UpsertCardAsync(card, existing, ct);
                        total++;
                    }
                }
                if (items.Count < 100) break;
            }
        }
        await db.SaveChangesAsync(ct);
        return new(setIds.Count, total, "riftcodex");
    }

    private async Task<CardSyncResult> SyncFromRiotAsync(
        Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("officiële card-gallery ophalen (playriftbound.com)");
        var html = await GetStringAsync(RiotGallery, ct);
        var buildId = RiotCardMapper.ExtractBuildId(html)
            ?? throw new InvalidOperationException("Riot build-id niet gevonden");
        var json = await GetJsonAsync(
            $"https://playriftbound.com/_next/data/{buildId}/en-us/card-gallery.json", ct);
        var cards = RiotCardMapper.ParseGallery(json?["pageProps"] ?? new JsonObject());

        progress?.Invoke($"{cards.Count} kaarten verwerken");
        var existing = await LoadExistingAsync(ct);
        var setIds = new HashSet<string>();
        foreach (var card in cards)
        {
            await UpsertCardAsync(card, existing, ct);
            if (card.SetId is not null) setIds.Add(card.SetId);
        }
        // De Riot-gallery levert geen setnamen of releasedatums bij de
        // set-facetten — alleen de set-code is hier bekend.
        foreach (var sid in setIds) await UpsertSetAsync(sid, sid, null, null, ct);
        await db.SaveChangesAsync(ct);
        return new(setIds.Count, cards.Count, "riot");
    }

    private static Card? MapRiftcodexCard(JsonObject c, string fallbackSetId)
    {
        var rid = c["riftbound_id"]?.GetValue<string>() ?? c["id"]?.GetValue<string>();
        if (rid is null) return null;
        return new Card
        {
            RiftboundId = rid,
            Name = c["name"]?.GetValue<string>() ?? rid,
            Type = c["classification"]?["type"]?.GetValue<string>(),
            Supertype = c["classification"]?["supertype"]?.GetValue<string>(),
            Rarity = c["classification"]?["rarity"]?.GetValue<string>(),
            Domains = c["classification"]?["domain"] is JsonArray d
                ? [.. d.Select(x => x?.GetValue<string>()).OfType<string>()]
                : [],
            Energy = c["attributes"]?["energy"]?.GetValue<int?>(),
            Might = c["attributes"]?["might"]?.GetValue<int?>(),
            Power = c["attributes"]?["power"]?.GetValue<int?>(),
            SetId = (c["set"]?["set_id"]?.GetValue<string>() ?? fallbackSetId).ToUpperInvariant(),
            SetLabel = c["set"]?["label"]?.GetValue<string>(),
            CollectorNumber = c["collector_number"]?.GetValue<int?>(),
            TextPlain = c["text"]?["plain"]?.GetValue<string>(),
            ImageUrl = c["media"]?["image_url"]?.GetValue<string>(),
            Tags = c["tags"] is JsonArray t
                ? [.. t.Select(x => x?.GetValue<string>()).OfType<string>()]
                : [],
        };
    }

    private async Task UpsertCardAsync(
        Card card, Dictionary<string, Card> known, CancellationToken ct)
    {
        if (!known.TryGetValue(card.RiftboundId, out var existing))
        {
            db.Cards.Add(card);
            known[card.RiftboundId] = card;
            return;
        }
        existing.Name = card.Name;
        existing.Type = card.Type;
        existing.Supertype = card.Supertype;
        existing.Rarity = card.Rarity;
        existing.Domains = card.Domains;
        existing.Energy = card.Energy;
        existing.Might = card.Might;
        existing.Power = card.Power;
        existing.SetId = card.SetId;
        existing.SetLabel = card.SetLabel;
        existing.CollectorNumber = card.CollectorNumber;
        // Tekstwijziging (errata!) invalideert de embedding → de embed-pijplijn
        // pakt de kaart automatisch opnieuw op. Gecachete gelijkenis-uitleg
        // over deze kaart is dan ook achterhaald.
        if (existing.TextPlain != card.TextPlain)
        {
            existing.Embedding = null;
            existing.EmbeddingModel = null;
            await db.SimilarityExplanations
                .Where(e => e.CardAId == card.RiftboundId || e.CardBId == card.RiftboundId)
                .ExecuteDeleteAsync(ct);
        }
        existing.TextPlain = card.TextPlain;
        existing.ImageUrl = card.ImageUrl;
        existing.Tags = card.Tags;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Riftcodex levert published_on als ISO-datetime
    /// ("2026-07-31T00:00:00") — basis voor de set-legaliteit (#22).</summary>
    private static DateOnly? ParsePublishedOn(JsonNode? node) =>
        node?.GetValue<string>() is { } s &&
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateOnly.FromDateTime(dt)
            : null;

    private async Task UpsertSetAsync(
        string setId, string name, int? cardCount, DateOnly? publishedOn, CancellationToken ct)
    {
        var existing = await db.CardSets.FindAsync([setId], ct);
        if (existing is null)
        {
            db.CardSets.Add(new CardSet
            {
                SetId = setId, Name = name, CardCount = cardCount, PublishedOn = publishedOn,
            });
            return;
        }
        // De Riot-fallback kent alleen de set-code (name == setId, geen datum):
        // eerder gesyncte échte namen/releasedatums nooit overschrijven met "onbekend".
        if (name != setId) existing.Name = name;
        existing.CardCount = cardCount ?? existing.CardCount;
        existing.PublishedOn = publishedOn ?? existing.PublishedOn;
        existing.SyncedAt = DateTimeOffset.UtcNow;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", IngestService.BrowserUserAgent);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct) =>
        JsonNode.Parse(await GetStringAsync(url, ct));

    private static List<JsonNode?> AsList(JsonNode? node)
    {
        if (node is JsonArray arr) return [.. arr];
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "data", "items", "results", "cards", "sets" })
                if (obj[key] is JsonArray a) return [.. a];
        }
        return [];
    }
}
