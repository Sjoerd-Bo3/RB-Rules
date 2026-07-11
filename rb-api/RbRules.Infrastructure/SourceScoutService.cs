using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record ScoutResult(int Proposals, string Message);

/// <summary>Bronnenjacht (#63, stap 2): laat rb-ai met task "research" (#64)
/// het web afzoeken naar nieuwe Riftbound-bronnen. Vondsten zijn uitdrukkelijk
/// vóórstellen: ze worden alleen als run_log-regels gelogd (kind "scout",
/// Ref = url voor dedupe over runs heen) — nooit automatisch aan het
/// bronnenregister toegevoegd. Opname en trust-toekenning blijven een
/// beheerdersbeslissing (docs/KNOWLEDGE.md: bron-trust is heilig).</summary>
public class SourceScoutService(RbRulesDbContext db, RbAiClient ai)
{
    public async Task<ScoutResult> RunAsync(
        Action<string>? progress = null, CancellationToken ct = default)
    {
        // Uitsluitlijst: alles wat al in het register staat (ook uitgezette
        // bronnen) plus alles wat een eerdere scout-run al voorstelde —
        // hetzelfde nogmaals voorstellen is ruis voor de beheerder.
        var registered = await db.Sources.AsNoTracking()
            .Select(s => s.Url).ToListAsync(ct);
        var proposedEarlier = await db.RunLogs.AsNoTracking()
            .Where(l => l.Kind == "scout" && l.Ref != null)
            .Select(l => l.Ref!)
            .Distinct()
            .ToListAsync(ct);
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

        progress?.Invoke("vondsten parsen en als voorstel loggen");
        var proposals = SourceScout.Parse(raw, known);
        if (proposals is null)
        {
            // Diagnose (#63): de afgekapte rauwe respons meeloggen, zodat de
            // beheerder in Recente activiteit ziet wát het model antwoordde.
            // De respons is LLM-output uit rb-ai en bevat geen secrets
            // (rb-api kent geen API-keys; prompts bevatten alleen bron-URL's).
            return await DegradeAsync(
                "LLM-antwoord onbruikbaar — geen parseerbare bronvoorstellen (blijft staan voor een volgende run). "
                + $"Respons (afgekapt): {Snippet(raw)}",
                ct);
        }

        foreach (var p in proposals)
        {
            db.RunLogs.Add(new RunLog
            {
                Kind = "scout", Ref = p.Url, Status = "info",
                Detail = $"{p.Url} — {p.Name} ({p.Type}): {p.Motivation}",
            });
        }
        await db.SaveChangesAsync(ct);

        return new(proposals.Count, proposals.Count == 0
            ? "geen nieuwe bronnen gevonden buiten het register en eerdere voorstellen"
            : $"{proposals.Count} bronvoorstel(len) gelogd (run_log, kind 'scout') — beoordeel ze en voeg goede bronnen handmatig toe");
    }

    private const int SnippetLength = 500;

    /// <summary>Rauwe LLM-respons plat en afgekapt voor één run_log-regel:
    /// whitespace samengevouwen, maximaal <see cref="SnippetLength"/> tekens.</summary>
    private static string Snippet(string raw)
    {
        var flat = string.Join(' ',
            raw.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length == 0) return "(leeg antwoord)";
        return flat.Length <= SnippetLength ? flat : flat[..SnippetLength] + "…";
    }

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
