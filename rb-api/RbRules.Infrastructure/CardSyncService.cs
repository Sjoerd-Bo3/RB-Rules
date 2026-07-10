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
    private const string RiotGallery = "https://riftbound.leagueoflegends.com/en-us/card-gallery/";

    public async Task<CardSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var mode = Environment.GetEnvironmentVariable("CARD_SOURCE") ?? "auto";
        if (mode == "riot") return await SyncFromRiotAsync(ct);
        try
        {
            return await SyncFromRiftcodexAsync(ct);
        }
        catch when (mode != "riftcodex")
        {
            return await SyncFromRiotAsync(ct);
        }
    }

    private async Task<CardSyncResult> SyncFromRiftcodexAsync(CancellationToken ct)
    {
        var setsNode = await GetJsonAsync($"{RiftcodexBase}/sets?page=1&size=100", ct);
        var sets = AsList(setsNode);
        var setIds = new List<string>();
        foreach (var s in sets.OfType<JsonObject>())
        {
            var setId = s["set_id"]?.GetValue<string>();
            if (setId is null) continue;
            await UpsertSetAsync(setId.ToUpperInvariant(), s["name"]?.GetValue<string>() ?? setId,
                s["card_count"]?.GetValue<int?>(), ct);
            setIds.Add(setId);
        }

        var total = 0;
        foreach (var setId in setIds)
        {
            for (var page = 1; page <= 200; page++)
            {
                var node = await GetJsonAsync(
                    $"{RiftcodexBase}/cards?set_id={Uri.EscapeDataString(setId)}&page={page}&size=100", ct);
                var items = AsList(node);
                if (items.Count == 0) break;
                foreach (var c in items.OfType<JsonObject>())
                {
                    var card = MapRiftcodexCard(c, setId);
                    if (card is not null)
                    {
                        await UpsertCardAsync(card, ct);
                        total++;
                    }
                }
                if (items.Count < 100) break;
            }
        }
        await db.SaveChangesAsync(ct);
        return new(setIds.Count, total, "riftcodex");
    }

    private async Task<CardSyncResult> SyncFromRiotAsync(CancellationToken ct)
    {
        var html = await GetStringAsync(RiotGallery, ct);
        var buildId = RiotCardMapper.ExtractBuildId(html)
            ?? throw new InvalidOperationException("Riot build-id niet gevonden");
        var json = await GetJsonAsync(
            $"https://riftbound.leagueoflegends.com/_next/data/{buildId}/en-us/card-gallery.json", ct);
        var cards = RiotCardMapper.ParseGallery(json?["pageProps"] ?? new JsonObject());

        var setIds = new HashSet<string>();
        foreach (var card in cards)
        {
            await UpsertCardAsync(card, ct);
            if (card.SetId is not null) setIds.Add(card.SetId);
        }
        foreach (var sid in setIds) await UpsertSetAsync(sid, sid, null, ct);
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

    private async Task UpsertCardAsync(Card card, CancellationToken ct)
    {
        var existing = await db.Cards.FindAsync([card.RiftboundId], ct);
        if (existing is null)
        {
            db.Cards.Add(card);
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
        // pakt de kaart automatisch opnieuw op.
        if (existing.TextPlain != card.TextPlain)
        {
            existing.Embedding = null;
            existing.EmbeddingModel = null;
        }
        existing.TextPlain = card.TextPlain;
        existing.ImageUrl = card.ImageUrl;
        existing.Tags = card.Tags;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task UpsertSetAsync(string setId, string name, int? cardCount, CancellationToken ct)
    {
        var existing = await db.CardSets.FindAsync([setId], ct);
        if (existing is null)
        {
            db.CardSets.Add(new CardSet { SetId = setId, Name = name, CardCount = cardCount });
            return;
        }
        existing.Name = name;
        existing.CardCount = cardCount ?? existing.CardCount;
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
