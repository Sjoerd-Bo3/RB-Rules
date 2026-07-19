namespace RbRules.Domain;

/// <summary>De gedenormaliseerde <c>RELATES_TO</c>-qualifier-cache (fase 2, #226,
/// §2.3/§0): één platte projectie van een <see cref="Interaction"/> + haar
/// condities voor snelle retrieval. NOOIT de bron van waarheid — volledig
/// herbouwbaar uit de gereïficeerde interactie (dat borgt
/// <c>InteractionProjectionTests</c>). Volgt §0: één conditie per as mag de cache
/// dragen; bij ≥2 condities op een as óf een rol-onderscheid (patient-specifieke
/// conditie) zet <see cref="ReifiedOnly"/> op true — de platte edge kan het feit
/// dan niet volledig dragen en consumenten moeten de reïficatie raadplegen.</summary>
/// <param name="Kind">Lowercase relatie-kind ("counters"/"modifies"/…).</param>
/// <param name="Window">De WINDOW-conditiewaarde (bv. "Showdown"), of null.</param>
/// <param name="ActorStatus">De (agent-)STATUS-conditiewaarde, of null.</param>
/// <param name="CostDelta">De COST-conditiewaarde, of null.</param>
/// <param name="Tier">Retrieval-tier (1 = verankerd/promoted).</param>
/// <param name="ReifiedOnly">De platte cache kan het feit niet volledig dragen —
/// raadpleeg de Interaction.</param>
public sealed record RelatesToQualifiers(
    string Kind,
    string? Window,
    string? ActorStatus,
    string? CostDelta,
    int Tier,
    bool ReifiedOnly);

/// <summary>Pure projectie-bouwstenen (fase 2, #226) tussen de gereïficeerde
/// interactie (SoT in Postgres) en de Neo4j-projectie. IO-loos en volledig
/// testbaar; <c>GraphSyncService</c> hangt de Cypher-writes eromheen.</summary>
public static class InteractionProjection
{
    /// <summary>Projecteert een interactie-knoop (<c>:Interaction</c>) alleen als ze
    /// niet verworpen is — een rejected interactie leeft alleen als tombstone
    /// (herstelpad), niet als graaf-knoop.</summary>
    public static bool ShouldProjectNode(string status) =>
        status != InteractionStatus.Rejected;

    /// <summary>Projecteert de gedenormaliseerde <c>RELATES_TO</c>-cache-edge alleen
    /// voor verankerde interacties (promoted/verified). Een kandidaat of
    /// model-hypothese is retrieval-zichtbaar als knoop, maar mag nog geen
    /// snelle-retrieval-cache-edge zaaien alsof het gevestigde kennis is.</summary>
    public static bool ShouldProjectCache(string status) =>
        status is InteractionStatus.Promoted or InteractionStatus.Verified;

    /// <summary>Bouwt de platte qualifier-cache uit een interactie + haar condities.
    /// Deterministisch en herbouwbaar: dezelfde interactie levert altijd dezelfde
    /// cache (nooit andersom — de cache is geen bron).</summary>
    public static RelatesToQualifiers ToQualifiers(Interaction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var conditions = interaction.Conditions ?? [];

        var window = SingleAxisValue(conditions, InteractionConditionKinds.Window, out var windowCount);
        var status = SingleAxisValue(conditions, InteractionConditionKinds.Status, out var statusCount);
        var cost = SingleAxisValue(conditions, InteractionConditionKinds.Cost, out var costCount);

        // §0: ≥2 condities op één as, of een patient-specifieke conditie, kan de
        // platte cache niet dragen — markeer zodat consumenten de reïficatie lezen.
        var reifiedOnly = windowCount > 1 || statusCount > 1 || costCount > 1
            || conditions.Any(c => c.SubjectRole == InteractionRoles.Patient);

        return new RelatesToQualifiers(
            Kind: (interaction.Kind ?? "").Trim().ToLowerInvariant(),
            Window: window,
            ActorStatus: status,
            CostDelta: cost,
            Tier: interaction.Status == InteractionStatus.Promoted ? 1 : 2,
            ReifiedOnly: reifiedOnly);
    }

    private static string? SingleAxisValue(
        IReadOnlyList<InteractionCondition> conditions, string onKind, out int count)
    {
        var matches = conditions.Where(c =>
            InteractionConditionKinds.Canonicalize(c.OnKind) == onKind).ToList();
        count = matches.Count;
        // Precies één conditie op deze as → de cache mag de waarde dragen.
        return count == 1 ? matches[0].Value : null;
    }

    /// <summary>De Cypher-param-rijen voor de idempotente Neo4j-projectie
    /// (<c>GraphSyncService</c>), puur en getest zodat de rij-vorm niet in de
    /// IO-laag verstopt zit. Dictionaries-only (de driver serialiseert geen
    /// anonymous types in collecties). Rejected interacties worden overgeslagen
    /// (<see cref="ShouldProjectNode"/>); alleen verankerde interacties zaaien een
    /// RELATES_TO-cache-edge (<see cref="ShouldProjectCache"/>).</summary>
    public static InteractionProjectionRows BuildProjectionRows(IEnumerable<Interaction> interactions)
    {
        ArgumentNullException.ThrowIfNull(interactions);
        var nodes = new List<Dictionary<string, object?>>();
        var roleEdges = new List<Dictionary<string, object?>>();
        var conditionNodes = new List<Dictionary<string, object?>>();
        var governedBy = new List<Dictionary<string, object?>>();
        var cache = new List<Dictionary<string, object?>>();

        foreach (var ix in interactions)
        {
            if (!ShouldProjectNode(ix.Status)) continue;
            var ixRef = ix.Ref.Format();

            nodes.Add(new()
            {
                ["ref"] = ixRef,
                ["kind"] = (ix.Kind ?? "").Trim().ToUpperInvariant(),
                ["status"] = ix.Status,
                ["statusReason"] = ix.StatusReason,
            });
            roleEdges.Add(new() { ["interaction"] = ixRef, ["filler"] = ix.AgentRef, ["role"] = InteractionRoles.Agent });
            roleEdges.Add(new() { ["interaction"] = ixRef, ["filler"] = ix.PatientRef, ["role"] = InteractionRoles.Patient });

            foreach (var c in ix.Conditions ?? [])
                conditionNodes.Add(new()
                {
                    ["ref"] = c.Ref.Format(),
                    ["interaction"] = ixRef,
                    ["onKind"] = InteractionConditionKinds.Canonicalize(c.OnKind) ?? c.OnKind,
                    ["subjectRole"] = c.SubjectRole,
                    ["value"] = c.Value,
                    ["operator"] = c.Operator,
                });

            if (!string.IsNullOrWhiteSpace(ix.GovernedByRef))
                governedBy.Add(new() { ["interaction"] = ixRef, ["section"] = ix.GovernedByRef });

            if (ShouldProjectCache(ix.Status))
            {
                var q = ToQualifiers(ix);
                cache.Add(new()
                {
                    ["from"] = ix.AgentRef,
                    ["to"] = ix.PatientRef,
                    ["kind"] = q.Kind,
                    ["window"] = q.Window,
                    ["actorStatus"] = q.ActorStatus,
                    ["costDelta"] = q.CostDelta,
                    ["tier"] = q.Tier,
                    ["reifiedOnly"] = q.ReifiedOnly,
                });
            }
        }

        return new InteractionProjectionRows(nodes, roleEdges, conditionNodes, governedBy, cache);
    }
}

/// <summary>De param-rijen voor de gereïficeerde-interactie-projectie (fase 2,
/// #226). Alle lijsten zijn dictionaries-only, klaar voor batched UNWIND.</summary>
public sealed record InteractionProjectionRows(
    IReadOnlyList<Dictionary<string, object?>> Nodes,
    IReadOnlyList<Dictionary<string, object?>> RoleEdges,
    IReadOnlyList<Dictionary<string, object?>> ConditionNodes,
    IReadOnlyList<Dictionary<string, object?>> GovernedByEdges,
    IReadOnlyList<Dictionary<string, object?>> RelatesToCache);
