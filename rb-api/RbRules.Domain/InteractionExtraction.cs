using System.Text.Json;
using RbRules.Domain.Ontology;

namespace RbRules.Domain;

/// <summary>Eén aangeboden ref voor de tool-forced extractie (fase 2, #226, §3.1):
/// de BrainRef die de LLM als <c>from</c>/<c>to</c> MAG noemen, plus het
/// ontologie-type (Card/Mechanic, #304) waarmee de reïficatie-vorm-poort de rol-range
/// toetst. De <c>enum</c> in het tool-schema wordt hieruit gegenereerd — het model
/// kan geen ref buiten de aangeboden set noemen.</summary>
public sealed record OfferedRef(string Ref, string Label, EntityType Type);

/// <summary>Het gesloten vocabulaire waarmee het <c>emit_interactions</c>-tool-schema
/// zijn enum-poorten sluit (§3.1): de aangeboden refs + de qualifier-lexica
/// (Window/Status). Cost-condities zijn vrijer gestructureerd (operator+floor) en
/// worden lexicaal niet gesloten, wél door de conditie-as-enum begrensd.</summary>
/// <param name="SectionRefs">De citeerbare <c>section:</c>-refs van de regelsecties
/// die als bewijs meegaan (#286). Ze zijn GEEN rol — alleen het gesloten enum voor
/// <c>governed_by</c>. Leeg = het veld verdwijnt uit het schema.</param>
public sealed record ExtractionVocab(
    IReadOnlyList<OfferedRef> Refs,
    IReadOnlyList<string> WindowLexicon,
    IReadOnlyList<string> StatusLexicon,
    IReadOnlyList<string>? SectionRefs = null);

/// <summary>Eén geëxtraheerde conditie (tool-output).</summary>
public sealed record ExtractedCondition(
    string OnKind, string? SubjectRole, string Value, string? Operator);

/// <summary>Eén geëxtraheerde, gekwalificeerde interactie (tool-output), al door de
/// tweede (deterministische) muur gehaald: refs ∈ aangeboden set, kind ∈
/// gereïficeerd-verplichte relaties, condities ∈ het gesloten lexicon.</summary>
/// <param name="GovernedByRef">De officiële regelsectie die deze interactie
/// verankert (#286) — één van de aangeboden <c>section:</c>-refs, of null. GEEN rol:
/// de HAS_ROLE-range is Card/Mechanic, dit vult <see cref="Interaction.GovernedByRef"/>
/// (GOVERNED_BY). Gesloten enum, dus het model kan geen sectie verzinnen.</param>
public sealed record ExtractedInteraction(
    string FromRef, string ToRef, string Kind, bool Interacts,
    string? Explanation, IReadOnlyList<ExtractedCondition> Conditions,
    string? GovernedByRef = null);

/// <summary>Fase 2 (#226, §3.1) — de ontologie-begrensde, tool-forced
/// structured-output-vorm voor de gekwalificeerde-interactie-extractie. Bouwt het
/// <c>emit_interactions</c>-JSON-Schema waarin <c>from</c>/<c>to</c> een enum zijn
/// van de aangeboden refs, <c>kind</c> een enum van de gereïficeerd-verplichte
/// ontologie-relaties, en de conditie-velden enums uit het gesloten Window/Status/
/// Cost-lexicon — het model KAN geen ref/kind/window buiten het schema noemen. De
/// deterministische parser (<see cref="Parse"/>) blijft als tweede muur
/// (defense-in-depth): wat het schema toch zou lekken, valt hier alsnog weg. Puur;
/// de daadwerkelijke rb-ai-call woont in de service.</summary>
public static class InteractionExtraction
{
    public const string SystemPrompt = """
        Je extraheert gekwalificeerde kaart/keyword-interacties in Riftbound TCG.
        Roep UITSLUITEND de tool `emit_interactions` aan; verzin geen refs, kinds of
        windows buiten de aangeboden enums. Per interactie:
        - from/to: refs uit de aangeboden lijst (agent = handelend, patient = ondergaand)
        - kind: COUNTERS | MODIFIES | GRANTS | REQUIRES
        - interacts: alleen true bij een echte, noemenswaardige interactie (streng;
          "past in hetzelfde deck" is GEEN interactie)
        - conditions: alleen als de regel een voorwaarde stelt (window/status/cost);
          laat leeg als de interactie onvoorwaardelijk is. Pers geen ambigue conditie
          plat — laat die dan weg.
        - governed_by (indien aangeboden): de regelsectie uit de invoer die deze
          interactie normatief verankert. Alleen invullen als die sectie de relatie
          ECHT beschrijft; bij twijfel null.
        - explanation: één zin, in het Engels, die de relatie uit het aangeboden
          bewijs verantwoordt.

        Waar het om gaat:
        - ZOEK VOORAL naar relaties TUSSEN KEYWORDS (mechanic:X ↔ mechanic:Y): hoe
          grijpen twee mechanieken op elkaar in? Dat is de kennis die nergens anders
          staat.
        - BEWIJSNIVEAU: een mechanic↔mechanic-claim telt ALLEEN met steun uit regel-
          of definitietekst ("[regels …]" of "[definitie]"). Een kaarttekst bewijst
          hooguit iets over die kaart zelf: een kaart-specifiek effect dat twee
          keywords verbindt is GEEN eigenschap van die keywords — meld zo'n paar dan
          niet als mechanic↔mechanic. Voor een kaart-rol (card↔card of
          card↔keyword) is de eigen kaarttekst wél het juiste bewijs.
        - Meld NOOIT dat een kaart haar eigen keyword heeft. Dat een kaart met
          [Equip] het keyword Equip draagt is al bekend en wordt weggegooid — het is
          geen interactie maar een eigenschap.
        - Een kaart-rol is alleen zinvol tegenover iets ANDERS dan haar eigen
          keywords: een andere kaart, of een keyword dat zij beïnvloedt zonder het
          zelf te dragen.
        - Regelteksten in de invoer ("[regels …]") zijn BEWIJS, geen rol: gebruik ze
          om een relatie te onderbouwen, maar noem ze niet als from/to.
        """;

    /// <summary>De reïficatie-verplichte relatie-kinds als enum-vocabulaire
    /// (COUNTERS/MODIFIES/GRANTS/REQUIRES) — dezelfde bron als
    /// <see cref="InteractionKinds.All"/>.</summary>
    public static IReadOnlyList<string> KindEnum => InteractionKinds.All;

    /// <summary>Bouwt het tool-schema (JSON-Schema-object als geserialiseerde string)
    /// met alle enum-poorten dichtgetimmerd op het aangeboden vocabulaire.</summary>
    public static string BuildToolSchema(ExtractionVocab vocab)
    {
        ArgumentNullException.ThrowIfNull(vocab);
        var refEnum = vocab.Refs.Select(r => r.Ref).ToList();

        var conditionSchema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["on_kind"] = EnumProp(InteractionConditionKinds.All, "Conditie-as."),
                ["subject_role"] = EnumProp(InteractionRoles.All, "Op wie de conditie slaat.", nullable: true),
                ["window"] = EnumProp(vocab.WindowLexicon, "Alleen bij on_kind=WINDOW.", nullable: true),
                ["status"] = EnumProp(vocab.StatusLexicon, "Alleen bij on_kind=STATUS.", nullable: true),
                ["value"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Vrije waarde bij on_kind=COST (bv. reduce:damage:floor=0)." },
                ["operator"] = new Dictionary<string, object?> { ["type"] = new[] { "string", "null" }, ["description"] = "equals|lte|reduce|…" },
            },
            ["required"] = new[] { "on_kind" },
        };

        var properties = new Dictionary<string, object?>
        {
            ["from"] = EnumProp(refEnum, "Agent-ref (uit de aangeboden set)."),
            ["to"] = EnumProp(refEnum, "Patient-ref (uit de aangeboden set)."),
            ["kind"] = EnumProp(KindEnum, "Gekwalificeerd relatie-kind."),
            ["interacts"] = new Dictionary<string, object?> { ["type"] = "boolean" },
            ["explanation"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["conditions"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = conditionSchema },
        };

        // Rijkere vraag in DEZELFDE aanroep (#286): de vaste kosten (SDK-opstart,
        // beurten) zijn toch al betaald, dus een extra veld over tekst die al in de
        // prompt staat is vrijwel gratis — anders dan een groter ref-vocabulaire, dat
        // de redeneerruimte vermenigvuldigt. Het veld verschijnt alleen als er
        // citeerbare secties meegaan; zonder enum-waarden zou het een open vraag zijn.
        if (vocab.SectionRefs is { Count: > 0 } sections)
            properties["governed_by"] = EnumProp(
                sections, "De officiële regelsectie die deze interactie verankert.",
                nullable: true);

        var interactionSchema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new[] { "from", "to", "kind", "interacts" },
        };

        var toolSchema = new Dictionary<string, object?>
        {
            ["name"] = "emit_interactions",
            ["description"] = "Emit ontologie-begrensde, gekwalificeerde interacties.",
            ["input_schema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["interactions"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = interactionSchema },
                },
                ["required"] = new[] { "interactions" },
            },
        };

        return JsonSerializer.Serialize(toolSchema);
    }

    private static Dictionary<string, object?> EnumProp(
        IReadOnlyList<string> values, string description, bool nullable = false) => new()
    {
        ["type"] = nullable ? new[] { "string", "null" } : "string",
        ["enum"] = nullable ? values.Append(null).ToArray() : values.Cast<object?>().ToArray(),
        ["description"] = description,
    };

    /// <summary>De tweede muur (defense-in-depth): parse de tool-output en gooi
    /// weg wat buiten het aangeboden vocabulaire valt (onbekende ref, niet-
    /// gereïficeerd kind, conditie-as/lexicon-schending). Accepteert zowel de
    /// tool-envelop <c>{"interactions":[…]}</c> als een kale array.</summary>
    public static IReadOnlyList<ExtractedInteraction> Parse(string? raw, ExtractionVocab vocab) =>
        ParseDetailed(raw, vocab).Items;

    /// <summary>Zelfde parse, maar mét het vorm-oordeel (#251-review): een afgekapte
    /// body of een schema-vreemde envelop (<c>{"interactions":"none"}</c>) levert
    /// <see cref="ExtractionParse{T}.Malformed"/> in plaats van stil een lege lijst.
    /// De mining-lus telt dat als <see cref="AiCallOutcome.Unparseable"/> i.p.v. als
    /// geslaagd-maar-leeg werk, en zet er géén voortgangs-watermark op — een kaart
    /// met een kapot antwoord moet juist terugkomen. Wat de enum-/lexicon-poorten
    /// hieronder wegfilteren blijft wél geslaagd: de envelop was geldig.</summary>
    public static ExtractionParse<ExtractedInteraction> ParseDetailed(
        string? raw, ExtractionVocab vocab)
    {
        ArgumentNullException.ThrowIfNull(vocab);
        if (string.IsNullOrWhiteSpace(raw)) return ExtractionParse<ExtractedInteraction>.Broken;

        var refSet = vocab.Refs.Select(r => r.Ref).ToHashSet(StringComparer.Ordinal);
        var windowSet = vocab.WindowLexicon.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statusSet = vocab.StatusLexicon.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sectionSet = (vocab.SectionRefs ?? []).ToHashSet(StringComparer.Ordinal);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonSpan(raw));
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return ExtractionParse<ExtractedInteraction>.Broken; }

        var array = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("interactions", out var inner) && inner.ValueKind == JsonValueKind.Array
            ? inner
            : root.ValueKind == JsonValueKind.Array ? root : default;
        if (array.ValueKind != JsonValueKind.Array)
            return ExtractionParse<ExtractedInteraction>.Broken;

        var result = new List<ExtractedInteraction>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var from = Str(item, "from");
            var to = Str(item, "to");
            var kind = InteractionKinds.Canonicalize(Str(item, "kind"));
            if (from is null || to is null || kind is null) continue;
            if (!refSet.Contains(from) || !refSet.Contains(to)) continue;      // enum-poort
            if (from == to) continue;                                          // geen self-loop

            var interacts = item.TryGetProperty("interacts", out var i) && i.ValueKind == JsonValueKind.True;
            var explanation = Str(item, "explanation");

            var conditions = new List<ExtractedCondition>();
            if (item.TryGetProperty("conditions", out var conds) && conds.ValueKind == JsonValueKind.Array)
                foreach (var c in conds.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    var onKind = InteractionConditionKinds.Canonicalize(Str(c, "on_kind"));
                    if (onKind is null) continue;
                    var role = Str(c, "subject_role");
                    if (role is not null && !InteractionRoles.IsValid(role)) role = null;

                    // Waarde uit de as-specifieke poort: WINDOW/STATUS uit het
                    // gesloten lexicon, COST uit de vrije value-string.
                    var value = onKind switch
                    {
                        InteractionConditionKinds.Window => Str(c, "window") is { } w && windowSet.Contains(w) ? w : null,
                        InteractionConditionKinds.Status => Str(c, "status") is { } s && statusSet.Contains(s) ? s : null,
                        InteractionConditionKinds.Cost => Str(c, "value"),
                        _ => null,
                    };
                    if (value is null) continue;   // lexicon-schending → weg
                    conditions.Add(new(onKind, role, value, Str(c, "operator")));
                }

            // Anker-poort (#286): een sectie die niet is aangeboden is verzonnen —
            // weg ermee, precies zoals een ref buiten de enum. Nooit een term buiten
            // het aangeboden lijstje (CLAUDE.md, de gesloten LLM-vraag).
            var governedBy = Str(item, "governed_by") is { } g && sectionSet.Contains(g) ? g : null;

            result.Add(new(from, to, kind, interacts, explanation, conditions, governedBy));
        }
        return new(result, Malformed: false);
    }

    private static string ExtractJsonSpan(string raw)
    {
        // Zowel {…}-envelop als [...]-array tolereren (LLM's plakken soms proza eromheen).
        var objStart = raw.IndexOf('{');
        var arrStart = raw.IndexOf('[');
        if (objStart < 0 && arrStart < 0) return raw;
        var useObj = objStart >= 0 && (arrStart < 0 || objStart < arrStart);
        var start = useObj ? objStart : arrStart;
        var end = raw.LastIndexOf(useObj ? '}' : ']');
        return end > start ? raw[start..(end + 1)] : raw;
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;
}

/// <summary>Fase 2 (#226, §3.1) — de RelationTypeConstraint-poort (from-kind × kind
/// × to-kind), afgeleid uit de ontologie zodat "een Card <c>clarifies</c> een
/// Source" er nooit in komt. Voor gekwalificeerde interacties: agent en patient
/// zijn een Card of Mechanic (#304), kind ∈ de gereïficeerd-verplichte relaties. Eén bron
/// (de ontologie) genereert zowel de prompt-enums als deze parser-poort.</summary>
public static class RelationTypeConstraint
{
    /// <summary>Mag deze (from-type, kind, to-type)-combinatie als gereïficeerde
    /// interactie bestaan? Delegeert naar
    /// <see cref="OntologyValidationService.ValidateReifiedInteraction"/> zodat
    /// schema en poort dezelfde bron delen.</summary>
    public static bool Allows(EntityType fromType, string kind, EntityType toType)
    {
        var canonical = InteractionKinds.Canonicalize(kind);
        if (canonical is null) return false;
        var relation = OntologySchema.RelationByEdgeName(canonical);
        if (relation is null) return false;
        return OntologyValidationService
            .ValidateReifiedInteraction(fromType, toType, relation.Type)
            .IsValid;
    }
}
