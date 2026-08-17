using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Totalen over één venster. <paramref name="Usd"/> is de som van de
/// REPRODUCEERBARE bedragen (rij × gestempeld tarief) — een ondergrens:
/// <paramref name="UnpricedCalls"/> telt de calls zonder tokens of zonder
/// tarief, die bewust géén verzonnen bedrag bijdragen.</summary>
public record CostTotals(
    int Calls, long InputTokens, long OutputTokens, int UnpricedCalls, decimal Usd);

public record CostModelRow(
    string Model, int Calls, long InputTokens, long OutputTokens,
    int UnpricedCalls, decimal Usd);

/// <summary>Platform-verbruik per job-soort (mining/audit/primer/…).</summary>
public record CostKindRow(
    string Kind, string Model, int Calls, long InputTokens, long OutputTokens,
    int UnpricedCalls, decimal Usd);

public record CostUserRow(
    long UserId, string Email, int Calls, long InputTokens, long OutputTokens,
    int UnpricedCalls, decimal Usd);

public record TariffRow(
    long Id, string Model, decimal InputUsdPerMTok, decimal OutputUsdPerMTok,
    DateTimeOffset EffectiveFrom);

/// <summary>Het paneel-antwoord (#328). Bedragen zijn overal SCHADUWKOSTEN
/// (tokens × API-tarief van dat moment), geen factuur — we betalen abonnement.
/// Geen vraaginhoud: uitsluitend maten, aantallen en bedragen.</summary>
public record CostsOverview(
    string Period,
    CostTotals Today, CostTotals Days7, CostTotals Days30,
    CostTotals UserCaused, CostTotals PlatformCaused,
    IReadOnlyList<CostModelRow> PerModel,
    IReadOnlyList<CostKindRow> PlatformPerKind,
    IReadOnlyList<CostUserRow> TopUsers,
    IReadOnlyList<TariffRow> Tariffs,
    string EmbeddingsNote,
    string MeteredNote);

/// <summary>Leest het kosten-grootboek voor het live beheer-paneel (#328).
/// Bedragen worden op LEESmoment berekend uit rij × gestempeld tarief
/// (<see cref="ShadowCost.ComputeUsd"/>) — de rij draagt de tariefversie, dus
/// een latere prijswijziging verandert historische bedragen niet.</summary>
public class AiUsageReportService(RbRulesDbContext db)
{
    /// <summary>Embeddings draaien lokaal (Ollama, bge-m3) en veroorzaken dus
    /// géén API-kosten — apart benoemd, niet stiekem als $0 in de tabellen.</summary>
    public const string EmbeddingsNote =
        "Embeddings draaien lokaal (Ollama, bge-m3) op de VM en kosten geen "
        + "API-tegoed; ze staan daarom bewust niet als bedrag in dit overzicht.";

    /// <summary>Het paneel moet ZELF zeggen wat het niet ziet (review #328):
    /// er zijn meer platform-services die LLM-calls doen dan er boeken —
    /// afwezig is afwezig, geen verzonnen nul, maar dat hoort de beheerder
    /// in het paneel te lezen, niet in een PR-body.</summary>
    public const string MeteredNote =
        "Gemeterd worden: vragen (ask), kaart-interactie-uitleg (resolve), "
        + "similarity-uitleg (explain), brein-mining, interactie-audit en "
        + "primer-generatie. Andere platform-jobs (o.a. claim-/relatie-/"
        + "mechanic-mining, triage, scout, classificatie) boeken nog geen "
        + "grootboekrijen — elk totaal hier is dus een ondergrens.";

    private const int TopUserCount = 15;

    /// <summary>In-geheugen-projectie van één grootboekrij (bewust ná de
    /// query gemapt — anonieme select blijft EF-vertaalbaar).</summary>
    private sealed record Row(
        string Origin, string Kind, string Model, long? UserId,
        long? InputTokens, long? OutputTokens, long? TariffVersion,
        DateTimeOffset CreatedAt);

    public async Task<CostsOverview> OverviewAsync(string? period, CancellationToken ct = default)
    {
        var normalized = period is "vandaag" or "30d" ? period : "7d";
        var now = DateTimeOffset.UtcNow;
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var d7 = now.AddDays(-7);
        var d30 = now.AddDays(-30);
        var since = normalized switch { "vandaag" => today, "30d" => d30, _ => d7 };

        // Eén window-query (30d is het ruimste venster dat het paneel toont);
        // de volumes zijn klein (één rij per vraag/jobrun), dus aggregatie in
        // geheugen met het tarief-woordenboek is de simpelste correcte vorm.
        var events = (await db.AiUsageEvents.AsNoTracking()
            .Where(e => e.CreatedAt >= d30)
            .Select(e => new
            {
                e.Origin, e.Kind, e.Model, e.UserId,
                e.InputTokens, e.OutputTokens, e.TariffVersion, e.CreatedAt,
            })
            .ToListAsync(ct))
            .Select(e => new Row(
                e.Origin, e.Kind, e.Model, e.UserId,
                e.InputTokens, e.OutputTokens, e.TariffVersion, e.CreatedAt))
            .ToList();
        var tariffs = await db.AiTariffs.AsNoTracking()
            .OrderBy(t => t.Model).ThenByDescending(t => t.EffectiveFrom)
            .ToListAsync(ct);
        var tariffById = tariffs.ToDictionary(t => t.Id);

        decimal? UsdOf(long? inTok, long? outTok, long? tariffId) =>
            tariffId is { } id && tariffById.TryGetValue(id, out var t)
                ? ShadowCost.ComputeUsd(inTok, outTok, t.InputUsdPerMTok, t.OutputUsdPerMTok)
                : null;

        CostTotals Totals(IEnumerable<Row> rows)
        {
            int calls = 0, unpriced = 0;
            long inTok = 0, outTok = 0;
            decimal usd = 0m;
            foreach (var e in rows)
            {
                calls++;
                inTok += e.InputTokens ?? 0;
                outTok += e.OutputTokens ?? 0;
                var amount = UsdOf(e.InputTokens, e.OutputTokens, e.TariffVersion);
                if (amount is null) unpriced++; else usd += amount.Value;
            }
            return new(calls, inTok, outTok, unpriced, decimal.Round(usd, 4));
        }

        var window = events.Where(e => e.CreatedAt >= since).ToList();

        var perModel = window
            .GroupBy(e => e.Model)
            .Select(g => { var t = Totals(g); return new CostModelRow(
                g.Key, t.Calls, t.InputTokens, t.OutputTokens, t.UnpricedCalls, t.Usd); })
            .OrderByDescending(r => r.Usd).ThenByDescending(r => r.Calls)
            .ToList();

        var perKind = window
            .Where(e => e.Origin == AiUsageEvent.OriginPlatform)
            .GroupBy(e => (e.Kind, e.Model))
            .Select(g => { var t = Totals(g); return new CostKindRow(
                g.Key.Kind, g.Key.Model, t.Calls, t.InputTokens, t.OutputTokens,
                t.UnpricedCalls, t.Usd); })
            .OrderByDescending(r => r.Usd).ThenByDescending(r => r.Calls)
            .ToList();

        var userRows = window
            .Where(e => e.Origin == AiUsageEvent.OriginUser && e.UserId != null)
            .GroupBy(e => e.UserId!.Value)
            .Select(g => (UserId: g.Key, Totals: Totals(g)))
            .OrderByDescending(x => x.Totals.Usd).ThenByDescending(x => x.Totals.Calls)
            .Take(TopUserCount)
            .ToList();
        var ids = userRows.Select(u => u.UserId).ToList();
        var emails = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Email })
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        var topUsers = userRows
            .Select(u => new CostUserRow(
                u.UserId, emails.GetValueOrDefault(u.UserId, $"account {u.UserId}"),
                u.Totals.Calls, u.Totals.InputTokens, u.Totals.OutputTokens,
                u.Totals.UnpricedCalls, u.Totals.Usd))
            .ToList();

        return new(
            normalized,
            Totals(events.Where(e => e.CreatedAt >= today)),
            Totals(events.Where(e => e.CreatedAt >= d7)),
            Totals(events),
            Totals(window.Where(e => e.Origin == AiUsageEvent.OriginUser)),
            Totals(window.Where(e => e.Origin == AiUsageEvent.OriginPlatform)),
            perModel, perKind, topUsers,
            [.. tariffs.Select(t => new TariffRow(
                t.Id, t.Model, t.InputUsdPerMTok, t.OutputUsdPerMTok, t.EffectiveFrom))],
            EmbeddingsNote,
            MeteredNote);
    }
}
