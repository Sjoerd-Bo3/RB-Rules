using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record DeckIngestResult(
    int Shards, int FailedShards, int InSitemap, int Fetched, int Saved, int Failed,
    int UnknownCards, bool CapHit, string Message);

/// <summary>Deck-ingest van Piltover Archive (#15, Piltover-first): sitemap
/// → publieke deck-pagina's → deck/deck_card, met attributie (bron-URL per
/// deck). Robots-afspraak is hard: alléén /sitemap* en /decks/view/{uuid} —
/// hun /api/ is disallowed en wordt nooit aangeraakt (BaseUrl + vaste paden,
/// geen URL's uit pagina-inhoud). Netjes crawlen: browser-UA (403 zonder),
/// ~1,5 s tussen requests en een cap per run; het run_log-grootboek (kind
/// "deckingest", ref "deck:{uuid}", "ok" pas ná parse+opslag — #93-patroon)
/// laat de volgende run verdergaan waar deze stopte, dus de ~10k-backfill
/// verloopt bewust over meerdere runs. Versheid is gericht: her-fetch alleen
/// als de sitemap-lastmod nieuwer is dan onze FetchedAt (geen blinde
/// verversing van 10k pagina's), met een 7-dagenregel als vangnet wanneer
/// een lastmod ontbreekt. Fouten per deck zijn data (run_log), de run gaat
/// door.</summary>
public class DeckIngestService(RbRulesDbContext db, HttpClient http)
{
    public const string LedgerKind = "deckingest";
    private const string BaseUrl = "https://piltoverarchive.com";
    private const int DefaultMaxPages = 400;
    private static readonly TimeSpan RefreshFallback = TimeSpan.FromDays(7);

    /// <summary>Pauze tussen requests. Test-seam: de servicetests zetten hem
    /// op nul — productie houdt de default (netiquette richting PA).</summary>
    public TimeSpan Throttle { get; set; } = TimeSpan.FromSeconds(1.5);

    private bool _firstRequest = true;

    public async Task<DeckIngestResult> RunAsync(
        int maxPages = DefaultMaxPages,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        maxPages = Math.Clamp(maxPages, 1, 2000);

        // 1. Sitemap-index → shards → deck-uuids met lastmod.
        progress?.Invoke("sitemap-index ophalen");
        var (index, indexError) = await FetchAsync($"{BaseUrl}/sitemap.xml", ct);
        if (index is null)
            return await FailRunAsync($"sitemap-index niet opgehaald: {indexError}", ct);
        var shardUrls = PiltoverSitemap.ShardUrls(index);
        if (shardUrls.Count == 0)
            return await FailRunAsync("sitemap-index bevat geen shards (formaat gewijzigd?)", ct);

        var entries = new Dictionary<string, DeckSitemapEntry>();
        var failedShards = 0;
        for (var i = 0; i < shardUrls.Count; i++)
        {
            progress?.Invoke($"sitemap-shard {i + 1}/{shardUrls.Count} ophalen ({entries.Count} decks gevonden)");
            var (shard, shardError) = await FetchAsync(shardUrls[i], ct);
            if (shard is null)
            {
                // Eén kapotte shard kost alleen zijn eigen decks — die komen
                // bij de volgende run vanzelf weer langs. Direct persisteren
                // (review-fix #15): zonder eigen SaveChanges verdween deze
                // regel spoorloos wanneer de queue daarna leeg bleek
                // (steady-state ná de backfill — precies dán valt hij op).
                failedShards++;
                db.RunLogs.Add(new RunLog
                {
                    Kind = LedgerKind, Ref = shardUrls[i], Status = "error",
                    Detail = $"sitemap-shard niet opgehaald: {shardError}",
                });
                await db.SaveChangesAsync(ct);
                continue;
            }
            foreach (var entry in PiltoverSitemap.DeckEntries(shard))
                entries.TryAdd(entry.Uuid, entry);
        }

        // 2. Werkvoorraad uit het grootboek + de versheid: nieuw, nooit
        // succesvol verwerkt, of op PA gewijzigd sinds onze fetch.
        var known = await db.Decks.AsNoTracking()
            .Select(d => new { d.PaId, d.FetchedAt })
            .ToDictionaryAsync(d => d.PaId, d => d.FetchedAt, ct);
        var okRefs = (await db.RunLogs.AsNoTracking()
                .Where(l => l.Kind == LedgerKind && l.Status == "ok"
                            && l.Ref != null && l.Ref.StartsWith("deck:"))
                .Select(l => l.Ref!)
                .Distinct()
                .ToListAsync(ct))
            .Select(r => r["deck:".Length..])
            .ToHashSet();

        var now = DateTimeOffset.UtcNow;
        var due = entries.Values.Where(e =>
                !known.TryGetValue(e.Uuid, out var fetchedAt)
                || !okRefs.Contains(e.Uuid)
                || (e.LastModified is { } mod
                    ? mod > fetchedAt
                    : fetchedAt < now - RefreshFallback))
            .ToList();
        var capHit = due.Count > maxPages;
        var queue = capHit ? due[..maxPages] : due;

        // 3. Kaartkoppeling: één snapshot per run (variantgroepering, #54).
        var linker = new DeckCardLinker(await db.Cards.AsNoTracking()
            .Select(c => new Card { RiftboundId = c.RiftboundId, Name = c.Name, VariantOf = c.VariantOf })
            .ToListAsync(ct));

        // 4. Deck-pagina's, throttled en per deck best-effort.
        var saved = 0;
        var failed = 0;
        var unknownCards = 0;
        for (var i = 0; i < queue.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"deck {i + 1}/{queue.Count} ophalen — {saved} opgeslagen, {failed} mislukt");
            var uuid = queue[i].Uuid;
            var url = $"{BaseUrl}/decks/view/{uuid}";
            var (html, fetchError) = await FetchAsync(url, ct);
            if (html is null)
            {
                failed++;
                db.RunLogs.Add(new RunLog
                {
                    Kind = LedgerKind, Ref = $"deck:{uuid}", Status = "error",
                    Detail = $"pagina niet opgehaald: {fetchError}",
                });
                await db.SaveChangesAsync(ct);
                continue;
            }

            var parsed = PiltoverDeckPage.Parse(html);
            if (parsed is null)
            {
                failed++;
                db.RunLogs.Add(new RunLog
                {
                    Kind = LedgerKind, Ref = $"deck:{uuid}", Status = "error",
                    Detail = "geen deck-object in de pagina gevonden (formaat gewijzigd?)",
                });
                await db.SaveChangesAsync(ct);
                continue;
            }

            try
            {
                var unknown = await SaveDeckAsync(parsed, url, linker, ct);
                unknownCards += unknown;
                saved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ook het opslaan zelf is per deck gecontaineerd (review-fix
                // #15): een DbUpdateException (bv. door Postgres geweigerde
                // userdata) zou anders de hele run aborteren én — omdat het
                // grootboek-"ok" mee terugrolt — elke volgende run
                // deterministisch op ditzelfde deck laten stranden.
                failed++;
                // De vergiftigde entiteiten (Added/Modified/Deleted uit het
                // upsert-pad) mogen niet in de gedeelde context blijven
                // hangen, anders faalt elke volgende SaveChanges mee
                // (RecordMetric-patroon uit AskService, hier volledig omdat
                // het update-pad ook Modified/Deleted rijen draagt).
                db.ChangeTracker.Clear();
                db.RunLogs.Add(new RunLog
                {
                    Kind = LedgerKind, Ref = $"deck:{uuid}", Status = "error",
                    Detail = $"opslaan mislukt: {ex.Message}",
                });
                await db.SaveChangesAsync(ct);
            }
        }

        var message =
            $"{shardUrls.Count} shards"
            + (failedShards > 0 ? $" ({failedShards} mislukt)" : "")
            + $", {entries.Count} decks in de sitemap; "
            + $"{saved} opgeslagen, {failed} mislukt"
            + (failed > 0 || failedShards > 0 ? " (details in run_log)" : "")
            + $", {unknownCards} onbekende kaartverwijzingen"
            + (capHit
                ? $" — cap van {maxPages} pagina's bereikt, de volgende run gaat verder ({due.Count - queue.Count} resterend)"
                : "");
        return new(shardUrls.Count, failedShards, entries.Count, queue.Count, saved, failed,
            unknownCards, capHit, message);
    }

    /// <summary>Upsert op PaId; de kaartregels worden integraal vervangen en
    /// het grootboek-"ok" zit in dezelfde SaveChanges — één transactie, dus
    /// "ok" bestaat alleen mét de opgeslagen data (#93-patroon).</summary>
    private async Task<int> SaveDeckAsync(
        ParsedDeck parsed, string url, DeckCardLinker linker, CancellationToken ct)
    {
        var deck = await db.Decks.FirstOrDefaultAsync(d => d.PaId == parsed.Id, ct);
        if (deck is null)
        {
            deck = new Deck { PaId = parsed.Id, SourceUrl = url };
            db.Decks.Add(deck);
        }
        deck.Name = parsed.Name;
        deck.SourceUrl = url;
        deck.Domains = parsed.Domains;
        deck.PaCreatedAt = parsed.CreatedAt;
        deck.PaUpdatedAt = parsed.UpdatedAt;
        deck.Views = parsed.Views;
        deck.Likes = parsed.Likes;
        deck.FetchedAt = DateTimeOffset.UtcNow;

        // Vervangen via de tracker (geen ExecuteDelete naast getrackte
        // entiteiten, docs/CONVENTIONS.md); een deck telt tientallen regels.
        var existing = await db.DeckCards.Where(c => c.DeckId == deck.Id).ToListAsync(ct);
        db.DeckCards.RemoveRange(existing);

        var unknown = 0;
        foreach (var card in parsed.Cards)
        {
            if (card.VariantNumber is null && card.CardName is null) continue;
            var canonical = linker.ResolveCanonical(card.VariantNumber, card.CardName);
            if (canonical is null) unknown++;
            db.DeckCards.Add(new DeckCard
            {
                Deck = deck,
                Section = card.Section,
                CardCode = card.VariantNumber ?? card.CardName!,
                CanonicalRiftboundId = canonical,
                Quantity = Math.Max(1, card.Quantity),
            });
        }

        db.RunLogs.Add(new RunLog
        {
            Kind = LedgerKind, Ref = $"deck:{parsed.Id}", Status = "ok",
            Detail = $"\"{parsed.Name ?? "(naamloos)"}\" — {parsed.Cards.Count} kaartregels"
                     + (unknown > 0 ? $", {unknown} onbekende kaart(en)" : ""),
        });
        await db.SaveChangesAsync(ct);
        return unknown;
    }

    /// <summary>Externe fetch met browser-UA (PA geeft 403 op kale clients) en
    /// throttle vóór elk vervolg-request. Fouten komen als tekst terug — de
    /// aanroeper beslist wat een fout betekent (fouten zijn data).</summary>
    private async Task<(string? Body, string? Error)> FetchAsync(string url, CancellationToken ct)
    {
        if (!_firstRequest && Throttle > TimeSpan.Zero)
            await Task.Delay(Throttle, ct);
        _firstRequest = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", IngestService.BrowserUserAgent);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return (null, $"HTTP {(int)res.StatusCode}");
            return (await res.Content.ReadAsStringAsync(ct), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Run die al bij de sitemap strandt: herleidbaar in run_log en
    /// als nette uitkomst terug (de job toont het detail).</summary>
    private async Task<DeckIngestResult> FailRunAsync(string reason, CancellationToken ct)
    {
        db.RunLogs.Add(new RunLog
        {
            Kind = LedgerKind, Ref = "sitemap", Status = "error", Detail = reason,
        });
        await db.SaveChangesAsync(ct);
        return new(0, 0, 0, 0, 0, 1, 0, false, reason);
    }
}
