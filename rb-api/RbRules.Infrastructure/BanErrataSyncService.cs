using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record BanErrataResult(int Bans, int Errata);

/// <summary>Structureert de banlijst en errata uit de nieuwste officiële
/// documenten via LLM-extractie (rb-ai). Vervangt de tekst-heuristiek uit de
/// PoP ("Bandle" matchte "ban"). Herbouwt per run: bans volledig vanaf de hub,
/// errata per officiële per-set-pagina (#94) — de bron van waarheid is de
/// officiële pagina, niet de vorige extractie.</summary>
public class BanErrataSyncService(RbRulesDbContext db, RbAiClient ai)
{
    public async Task<BanErrataResult> SyncAsync(CancellationToken ct = default)
    {
        var cards = await db.Cards
            .Select(c => new { c.RiftboundId, c.Name })
            .ToListAsync(ct);
        string? MatchCard(string name) => cards
            .FirstOrDefault(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?.RiftboundId;

        var bans = 0;
        var hub = await LatestDocAsync(SourceSeed.RulesHubId, ct);
        if (hub is not null)
        {
            var raw = await ai.AskAsync(
                hub.Value.Content[..Math.Min(hub.Value.Content.Length, BanErrataExtractor.MaxPageTextLength)],
                BanErrataExtractor.BanSystemPrompt, ct: ct);
            if (raw is not null)
            {
                var extracted = BanErrataExtractor.ParseBans(raw);
                if (extracted.Count > 0)
                {
                    // Rebuild in één transactie: nooit een venster met lege
                    // banlijst als het middenin misgaat (review-fix).
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    await db.BanEntries.ExecuteDeleteAsync(ct);
                    foreach (var b in extracted)
                    {
                        db.BanEntries.Add(new BanEntry
                        {
                            Name = b.Name,
                            Kind = b.Kind,
                            CardRiftboundId = MatchCard(b.Name),
                            SourceUrl = hub.Value.Url,
                        });
                        bans++;
                    }
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
            }
        }

        // Errata (#94): alle officiële per-set-errata-pagina's structureren.
        // Bron-selectie op naamconventie (Id bevat "errata") binnen trust 1,
        // zodat een nieuwe set-pagina meteen meedoet zodra de beheerder haar
        // (via een hub-voorstel) als bron opneemt. De community-mirror
        // (card-errata, trust 3) structureert bewust niet meer mee: de
        // officiële pagina's dekken alle sets en officieel wint altijd.
        var errata = 0;
        var errataSources = await db.Sources.AsNoTracking()
            .Where(s => s.Enabled && s.TrustTier == 1 && s.Id.Contains("errata"))
            .OrderByDescending(s => s.Rank)
            .Select(s => new { s.Id, s.Name, s.Url, s.PublishedAt, s.UpdatedAt })
            .ToListAsync(ct);

        // Per bron extraheren, buiten de transactie (LLM-calls zijn traag en
        // fallibel). Best-effort per pagina: één haperende call laat de
        // andere pagina's — én de bestaande rijen van déze bron — met rust.
        var perSource = new List<(string Url, List<Erratum> Rows)>();
        foreach (var s in errataSources)
        {
            var doc = await LatestDocAsync(s.Id, ct);
            if (doc is null) continue;
            var raw = await ai.AskAsync(
                BanErrataExtractor.BuildErrataInput(s.Name, doc.Value.Content),
                BanErrataExtractor.ErrataSystemPrompt, ct: ct);
            if (raw is null) continue;
            var extracted = BanErrataExtractor.ParseErrata(raw);
            if (extracted.Count == 0) continue;
            // Temporele precedentie (#168): de errata gelden vanaf de laatste
            // wérkelijke wijziging van hun bronpagina (UpdatedAt), of anders
            // haar publicatiedatum — nooit geraden (null als de bron geen van
            // beide draagt, bijvoorbeeld een legacy/handmatig toegevoegde bron).
            var effective = s.UpdatedAt ?? s.PublishedAt;
            DateOnly? effectiveFrom = effective is null
                ? null : DateOnly.FromDateTime(effective.Value.UtcDateTime);
            perSource.Add((doc.Value.Url, [.. extracted.Select(e => new Erratum
            {
                CardName = e.CardName,
                NewText = e.NewText,
                CardRiftboundId = MatchCard(e.CardName),
                SourceUrl = doc.Value.Url,
                EffectiveFrom = effectiveFrom,
            })]));
        }

        if (perSource.Count > 0)
        {
            // Reconcilieer in één transactie (nooit een venster zonder errata).
            // Bewust GEEN blinde delete+rebuild meer (#168, review-fix): dat gaf
            // elke rij bij elke "Alles bijwerken" een nieuwe Id én een nieuwe
            // DetectedAt (= UtcNow), waardoor DetectedAt "laatste sync" i.p.v.
            // "voor het eerst gezien" betekende en de precedentie-tie-break kon
            // churnen. Nu matchen we op (CardName, NewText): ongewijzigde errata
            // behouden hun rij (Id + DetectedAt), alleen echt nieuwe komen erbij
            // en echt verdwenen gaan weg. Volledig getrackt (geen ExecuteDelete
            // gemengd met tracking, CONVENTIONS): errata is klein (tientallen-
            // honderden rijen), dus een load-en-reconcilieer is ruim goedkoop.
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Wees-errata (bronnen die niet meer meedoen). activeUrls dekt ALLE
            // geconfigureerde errata-bronnen, niet alleen de geslaagde: een bron
            // die deze run faalde (geen doc/AI-uitval) staat niet in perSource,
            // maar zijn bestaande rijen mogen niet als wees sneuvelen.
            var activeUrls = errataSources.Select(s => s.Url).ToList();
            var orphans = await db.Errata.Where(e => !activeUrls.Contains(e.SourceUrl)).ToListAsync(ct);
            db.Errata.RemoveRange(orphans);

            foreach (var (url, rows) in perSource)
            {
                var existing = await db.Errata.Where(e => e.SourceUrl == url).ToListAsync(ct);
                var byKey = existing
                    .GroupBy(e => (e.CardName, e.NewText))
                    .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id).First());
                var seen = new HashSet<(string, string)>();
                foreach (var r in rows)
                {
                    var key = (r.CardName, r.NewText);
                    if (!seen.Add(key)) continue; // duplicaat binnen dezelfde extractie
                    if (byKey.TryGetValue(key, out var keep))
                    {
                        // Ongewijzigde tekst ⇒ rij behouden (Id + DetectedAt),
                        // alleen afgeleiden bijwerken (kaartmatch/brondatum kan
                        // sinds de vorige sync veranderd zijn — churnt DetectedAt
                        // niet).
                        keep.CardRiftboundId = r.CardRiftboundId;
                        keep.EffectiveFrom = r.EffectiveFrom;
                    }
                    else
                    {
                        db.Errata.Add(r); // nieuw ⇒ DetectedAt = UtcNow (nu voor het eerst gezien)
                    }
                }
                // Rijen waarvan de (CardName, NewText) niet meer voorkomt: weg.
                db.Errata.RemoveRange(existing.Where(e => !seen.Contains((e.CardName, e.NewText))));
                errata += seen.Count;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        return new(bans, errata);
    }

    private async Task<(string Content, string Url)?> LatestDocAsync(string sourceId, CancellationToken ct)
    {
        var result = await db.Documents
            .Where(d => d.SourceId == sourceId)
            .OrderByDescending(d => d.RetrievedAt)
            .Join(db.Sources, d => d.SourceId, s => s.Id, (d, s) => new { d.Content, s.Url })
            .FirstOrDefaultAsync(ct);
        return result is null ? null : (result.Content, result.Url);
    }
}
