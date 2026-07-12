using RbRules.Domain;
using RbRules.Infrastructure;

namespace RbRules.Api.Endpoints;

/// <summary>De zes brein-koppelvlakken (#105, docs/BRAIN.md §2.3) onder
/// /api/brain/*. Compose-intern zoals heel rb-api (geen admin-key, geen
/// llm-rate-limit — dit zijn pure DB/Neo4j-reads); de browser komt hier
/// alleen via rb-web-proxy's, de ask-agent via de rb-ai-brein-tools.
/// Fouten zijn data: elk foutpad is een Problem-response met een detail die
/// de agent kan lezen — Neo4j-uitval raakt alleen neighbors/path ("graph
/// niet beschikbaar"), de vier Postgres-koppelvlakken blijven werken.</summary>
public static class BrainEndpoints
{
    public static void MapBrainEndpoints(this IEndpointRouteBuilder app)
    {
        var brain = app.MapGroup("/api/brain");

        // ── search: één embed-call, vijf lagen, gelabeld ───────────────
        brain.MapGet("/search", async (
            string? q, string? layers, int? take, BrainService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Problem(400, "ongeldige aanvraag", "q is verplicht");
            if (!BrainQuery.TryParseLayers(layers, out var layerSet, out var fout))
                return Problem(400, "ongeldige aanvraag", fout);
            // Extreem lange invoer hoort niet de embedder in (RuleEndpoints-cap).
            var query = q.Trim();
            if (query.Length > 400) query = query[..400];
            return Results.Ok(await svc.SearchAsync(
                query, layerSet, Math.Clamp(take ?? 8, 1, 20), ct));
        });

        // ── node: Postgres-projectie, nooit embeddings ─────────────────
        // Catch-all-route ({**ref}): section-refs bevatten een slash en de
        // rb-ai-tools sturen refs URL-ge-encodeerd (%2F) — beide vormen
        // moeten op dezelfde knoop uitkomen.
        brain.MapGet("/node/{**refText}", async (
            string refText, BrainService svc, CancellationToken ct) =>
        {
            if (!BrainQuery.TryParseRouteRef(refText, out var nodeRef))
                return InvalidRef(refText);
            var node = await svc.NodeAsync(nodeRef, ct);
            return node is null
                ? Problem(404, "niet gevonden", $"onbekende ref: {nodeRef.Format()}")
                : Results.Ok(node);
        });

        // ── neighbors: Neo4j, edge-whitelist + kind-filter + richting ──
        brain.MapGet("/neighbors/{**refText}", async (
            string refText, string? edges, string? kind, string? richting, string? direction,
            int? take, BrainGraphService graph, ILogger<BrainGraphService> logger,
            CancellationToken ct) =>
        {
            if (!BrainQuery.TryParseRouteRef(refText, out var nodeRef))
                return InvalidRef(refText);
            if (BrainQuery.GraphLabel(nodeRef.Kind) is not { } label)
                return Problem(400, "ongeldige aanvraag",
                    $"refs van deze soort staan niet in de kennisgraaf: {nodeRef.Format()} " +
                    "(geverifieerde rulings leven alleen in Postgres — gebruik node/search)");
            if (!BrainQuery.TryParseEdges(edges, out var edgeFilter, out var fout))
                return Problem(400, "ongeldige aanvraag", fout);
            // Kind (#116) is een property-waarde uit het open vocabulaire —
            // genormaliseerd en geparametriseerd, geen whitelist (die blijft
            // voor het edge-TYPE).
            if (!BrainQuery.TryParseKind(kind, out var kindFilter, out fout))
                return Problem(400, "ongeldige aanvraag", fout);
            // NL "richting" is het contract (§2.3); "direction" is de alias.
            if (!BrainQuery.TryParseRichting(richting ?? direction, out var dir, out fout))
                return Problem(400, "ongeldige aanvraag", fout);

            try
            {
                var neighbors = await graph.NeighborsAsync(
                    label, nodeRef.Format(), edgeFilter, kindFilter, dir,
                    Math.Clamp(take ?? 20, 1, 50), ct);
                return neighbors is null
                    ? Problem(404, "niet gevonden",
                        $"ref {nodeRef.Format()} staat niet in de kennisgraaf — " +
                        "bestaat de knoop en is de graph-job gedraaid?")
                    : Results.Ok(new BrainNeighborsResponse(nodeRef.Format(), neighbors));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return GraphUnavailable(logger, ex);
            }
        });

        // ── path: kortste pad = de bewijsketen ─────────────────────────
        brain.MapGet("/path", async (
            string? from, string? to, int? maxLen, string? kind,
            BrainGraphService graph, ILogger<BrainGraphService> logger, CancellationToken ct) =>
        {
            if (!BrainQuery.TryParseRouteRef(from, out var fromRef))
                return InvalidRef(from ?? "(leeg)", "from");
            if (!BrainQuery.TryParseRouteRef(to, out var toRef))
                return InvalidRef(to ?? "(leeg)", "to");
            if (!BrainQuery.TryParseKind(kind, out var kindFilter, out var kindFout))
                return Problem(400, "ongeldige aanvraag", kindFout);
            if (BrainQuery.GraphLabel(fromRef.Kind) is not { } fromLabel)
                return Problem(400, "ongeldige aanvraag",
                    $"from-ref staat niet in de kennisgraaf: {fromRef.Format()}");
            if (BrainQuery.GraphLabel(toRef.Kind) is not { } toLabel)
                return Problem(400, "ongeldige aanvraag",
                    $"to-ref staat niet in de kennisgraaf: {toRef.Format()}");
            if (fromRef == toRef)
                return Problem(400, "ongeldige aanvraag",
                    "from en to zijn dezelfde ref — een pad vergt twee verschillende knopen");

            try
            {
                var (outcome, chain) = await graph.PathAsync(
                    fromLabel, fromRef.Format(), toLabel, toRef.Format(),
                    Math.Clamp(maxLen ?? 4, 1, 6), kindFilter, ct);
                return outcome switch
                {
                    BrainGraphService.PathOutcome.FromMissing => Problem(404, "niet gevonden",
                        $"from-ref {fromRef.Format()} staat niet in de kennisgraaf"),
                    BrainGraphService.PathOutcome.ToMissing => Problem(404, "niet gevonden",
                        $"to-ref {toRef.Format()} staat niet in de kennisgraaf"),
                    // Geen pad is een geldig antwoord (lege keten), geen fout.
                    _ => Results.Ok(new BrainPathResponse(
                        fromRef.Format(), toRef.Format(), chain)),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return GraphUnavailable(logger, ex);
            }
        });

        // ── evidence: bewijsvoering per claim ──────────────────────────
        brain.MapGet("/evidence/{claimRef}", async (
            string claimRef, BrainService svc, CancellationToken ct) =>
        {
            if (!BrainQuery.TryParseRouteRef(claimRef, out var parsed) ||
                parsed.Kind != BrainRefKind.Claim ||
                !long.TryParse(parsed.Key, out var claimId))
                return Problem(400, "ongeldige aanvraag",
                    $"evidence verwacht een claim:<id>-ref, kreeg '{claimRef}'");
            var evidence = await svc.EvidenceAsync(claimId, ct);
            return evidence is null
                ? Problem(404, "niet gevonden", $"onbekende claim: {parsed.Format()}")
                : Results.Ok(evidence);
        });

        // ── contradictions: weerlegde kennis, expliciet gelabeld ───────
        brain.MapGet("/contradictions", async (
            string? topic, BrainService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(topic))
                return Problem(400, "ongeldige aanvraag",
                    "topic is verplicht (een BrainRef zoals mechanic:Deflect, of vrije tekst)");
            var trimmed = topic.Trim();
            if (trimmed.Length > 200) trimmed = trimmed[..200];
            return Results.Ok(await svc.ContradictionsAsync(trimmed, ct));
        });
    }

    /// <summary>Problem-response {title, detail}: de detail-tekst is wat de
    /// ask-agent als toolresultaat ziet — altijd concreet en leesbaar.</summary>
    private static IResult Problem(int status, string title, string detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);

    private static IResult InvalidRef(string raw, string param = "ref") =>
        Problem(400, "ongeldige aanvraag",
            $"ongeldige {param} '{raw}' — verwacht kind:key, " +
            "bv. card:ogn-011-298, mechanic:Deflect of section:core-rules-pdf/101.2");

    /// <summary>Degradatie (§2.3): Neo4j-uitval maakt neighbors/path een 503
    /// met "graph niet beschikbaar" in de detail (de agent-tools herkennen
    /// die tekst); search/node/evidence/contradictions draaien op Postgres
    /// en blijven werken.</summary>
    private static IResult GraphUnavailable(ILogger logger, Exception ex)
    {
        logger.LogWarning(ex, "Brein-graph-query mislukt — Neo4j onbereikbaar?");
        return Problem(503, "graph niet beschikbaar",
            $"graph niet beschikbaar: {ex.Message} — semantisch zoeken (search) en " +
            "node/evidence/contradictions werken nog.");
    }
}
