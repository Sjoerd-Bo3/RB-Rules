using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record ScoutResult(int Proposals, string Message);

public record ProposalDecision(string Status, string? SourceId, string Message);

/// <summary>Bronnenjacht (#63): laat rb-ai met task "research" (#64) het web
/// afzoeken naar nieuwe Riftbound-bronnen én beheert de reviewqueue eromheen.
/// Vondsten worden als <see cref="SourceProposal"/> opgeslagen (status
/// "proposed") — nooit automatisch aan het bronnenregister toegevoegd.
/// Accepteren zet de bron uitgeschakeld in het register met veilige defaults;
/// trust-toekenning en activeren blijven een beheerdersbeslissing
/// (docs/KNOWLEDGE.md: bron-trust is heilig).</summary>
public class SourceScoutService(RbRulesDbContext db, RbAiClient ai)
{
    public async Task<ScoutResult> RunAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        // Migreer-stap: voorstellen van vóór de reviewqueue leefden alleen
        // als run_log-regels — die eerst (idempotent) omzetten, zodat de
        // uitsluitlijst hieronder compleet is en de beheerder ze ziet.
        var backfilled = await BackfillFromRunLogAsync(ct);
        if (backfilled > 0)
            progress?.Invoke($"{backfilled} eerdere run_log-voorstellen naar de reviewqueue overgezet");

        // Uitsluitlijst: alles wat al in het register staat (ook uitgezette
        // bronnen) plus elk bestaand voorstel, ongeacht status — hetzelfde
        // nogmaals voorstellen is ruis voor de beheerder.
        var registered = await db.Sources.AsNoTracking()
            .Select(s => s.Url).ToListAsync(ct);
        var proposedEarlier = await db.SourceProposals.AsNoTracking()
            .Select(p => p.Url).ToListAsync(ct);
        var known = registered.Concat(proposedEarlier).ToList();

        progress?.Invoke("web doorzoeken via rb-ai (research) — kan enkele minuten duren");
        string? raw;
        try
        {
            raw = await ai.AskAsync(
                SourceScout.BuildPrompt(known), SourceScout.SystemPrompt,
                task: "research", ct: ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient-timeout (niet de aanroeper die annuleert): de
            // research-call duurde te lang — zelfde degradatiepad als uitval.
            raw = null;
        }
        if (raw is null)
        {
            return await DegradeAsync(
                "rb-ai niet beschikbaar of de webzoektocht is mislukt — geen voorstellen; probeer het later opnieuw",
                ct);
        }

        progress?.Invoke("vondsten parsen en in de reviewqueue zetten");
        var proposals = SourceScout.Parse(raw, known);
        if (proposals is null)
        {
            // Diagnose (#63): de afgekapte rauwe respons meeloggen, zodat de
            // beheerder in Recente activiteit ziet wát het model antwoordde.
            // De respons is LLM-output uit rb-ai en bevat geen secrets
            // (rb-api kent geen API-keys; prompts bevatten alleen bron-URL's).
            return await DegradeAsync(
                "LLM-antwoord onbruikbaar — geen parseerbare bronvoorstellen (blijft staan voor een volgende run). "
                + $"Respons (afgekapt): {LlmJson.Snippet(raw, SnippetLength)}",
                ct);
        }

        foreach (var p in proposals)
        {
            db.SourceProposals.Add(p);
            // De run_log-regel blijft als activiteitenlog (wanneer vond de
            // scout wat); de reviewqueue zelf leeft in source_proposal.
            db.RunLogs.Add(new RunLog
            {
                Kind = "scout", Ref = p.Url, Status = "info",
                Detail = $"{p.Url} — {p.Name} ({p.Type}): {p.Motivation}",
            });
        }
        await db.SaveChangesAsync(ct);

        return new(proposals.Count, proposals.Count == 0
            ? "geen nieuwe bronnen gevonden buiten het register en eerdere voorstellen"
            : $"{proposals.Count} bronvoorstel(len) in de reviewqueue gezet — beoordeel ze onder Bronvoorstellen in het beheer");
    }

    /// <summary>Accepteren: markeert het voorstel en zet de bron mét veilige
    /// defaults (Enabled = false!) in het register — tenzij de URL daar al
    /// staat, dan alleen de status. Aanzetten gebeurt daarna bewust via de
    /// bestaande bronnen-tabel; zo gaat er nooit iets automatisch aan.</summary>
    public async Task<ProposalDecision?> AcceptAsync(long id, CancellationToken ct = default)
    {
        var proposal = await db.SourceProposals.FindAsync([id], ct);
        if (proposal is null) return null;

        // SSRF-guard (#45): een webvondst komt alleen door de guard heen het
        // register (en dus de scan-loop) in. De fetch-rand valideert daarna
        // nogmaals — dit is de vroege, duidelijke melding voor de beheerder.
        if (UrlGuard.Check(proposal.Url) is { Allowed: false } g)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "scout", Ref = proposal.Url, Status = "error",
                Detail = $"voorstel niet geaccepteerd — URL geweigerd (SSRF-guard): {g.Reason}",
            });
            await db.SaveChangesAsync(ct);
            return new("refused", null,
                $"URL geweigerd (SSRF-guard): {g.Reason} — het voorstel blijft staan; verwerp het om het uit de queue te halen");
        }

        var registered = (await db.Sources.AsNoTracking()
                .Select(s => new { s.Id, s.Url }).ToListAsync(ct))
            .FirstOrDefault(s => string.Equals(
                SourceScout.NormalizeUrl(s.Url), SourceScout.NormalizeUrl(proposal.Url),
                StringComparison.OrdinalIgnoreCase));

        string? sourceId;
        string message;
        if (registered is null)
        {
            var src = SourceScout.ToSource(proposal);
            src.Id = await UniqueSourceIdAsync(src.Id, ct);
            db.Sources.Add(src);
            sourceId = src.Id;
            message = $"bron '{src.Id}' uitgeschakeld in het register gezet (trust {src.TrustTier}, {src.Parser}, {src.Cadence}) — aanzetten kan via de bronnen-tabel";
        }
        else
        {
            // Bijv. handmatig al toegevoegd — geen duplicaat maken.
            sourceId = registered.Id;
            message = $"URL staat al in het register als '{registered.Id}' — alleen de voorstel-status is bijgewerkt";
        }

        proposal.Status = "accepted";
        proposal.ReviewedAt = DateTimeOffset.UtcNow;
        db.RunLogs.Add(new RunLog
        {
            Kind = "scout", Ref = proposal.Url, Status = "ok",
            Detail = $"voorstel geaccepteerd — {message}",
        });
        await db.SaveChangesAsync(ct);
        return new("accepted", sourceId, message);
    }

    /// <summary>Verwerpen: het voorstel blijft bestaan (status "rejected") en
    /// blijft zo in de uitsluitlijst — de scout stelt hem niet opnieuw voor.</summary>
    public async Task<ProposalDecision?> RejectAsync(long id, CancellationToken ct = default)
    {
        var proposal = await db.SourceProposals.FindAsync([id], ct);
        if (proposal is null) return null;
        proposal.Status = "rejected";
        proposal.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new("rejected", null, "voorstel verworpen — wordt niet opnieuw voorgesteld");
    }

    /// <summary>Idempotente backfill: elke oude scout-run_log-regel (info met
    /// Ref) zonder bijbehorend voorstel wordt een SourceProposal; de
    /// run_log-regels zelf blijven staan als activiteitenlog. Draait bij elke
    /// scout-run en doet na de eerste keer niets meer.</summary>
    private async Task<int> BackfillFromRunLogAsync(CancellationToken ct)
    {
        // Alleen info-regels zijn voorstellen; error-regels zijn degradaties.
        var logs = await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "scout" && l.Ref != null && l.Status == "info")
            .OrderBy(l => l.CreatedAt)
            .Select(l => new { l.Ref, l.Detail, l.CreatedAt })
            .ToListAsync(ct);
        if (logs.Count == 0) return 0;

        var existing = (await db.SourceProposals.AsNoTracking()
                .Select(p => p.Url).ToListAsync(ct))
            .Select(SourceScout.NormalizeUrl)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var l in logs)
        {
            if (!existing.Add(SourceScout.NormalizeUrl(l.Ref!))) continue;
            db.SourceProposals.Add(SourceScout.FromRunLog(l.Ref!, l.Detail, l.CreatedAt));
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>Slug-botsingen (twee pagina's op dezelfde host met hetzelfde
    /// laatste padsegment) krijgen een volgnummer-suffix.</summary>
    private async Task<string> UniqueSourceIdAsync(string baseId, CancellationToken ct)
    {
        var id = baseId;
        for (var n = 2; await db.Sources.AnyAsync(s => s.Id == id, ct); n++)
            id = $"{baseId}-{n}";
        return id;
    }

    private const int SnippetLength = 500;

    /// <summary>Nette degradatie: de reden is zichtbaar in run_log én in het
    /// job-detail; de job crasht niet (AI-uitval is een verwacht pad).</summary>
    private async Task<ScoutResult> DegradeAsync(string message, CancellationToken ct)
    {
        db.RunLogs.Add(new RunLog
        {
            Kind = "scout", Ref = null, Status = "error", Detail = message,
        });
        await db.SaveChangesAsync(ct);
        return new(0, message);
    }
}
