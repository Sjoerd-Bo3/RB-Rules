namespace RbRules.Domain;

/// <summary>De sleutels van de beheerde instellingen (#254). Bewust stabiele,
/// letterlijke strings: ze staan als primaire sleutel in de <c>setting</c>-tabel, dus
/// hernoemen is een migratie, geen refactor.</summary>
public static class SettingKeys
{
    /// <summary>Brein-GraphRAG-retrieval in /ask (was <c>BREIN_RETRIEVAL_ENABLED</c>).</summary>
    public const string BreinRetrievalEnabled = "brein.retrieval.enabled";

    /// <summary>Steekproefdichtheid van de interactie-audit (#255): 1 op de N
    /// gepromoveerde interacties gaat langs het sterkere model. Beheerd (#254) en
    /// géén env-only vlag: de dichtheid is precies het soort knop dat Sjoerd wil
    /// kunnen bijstellen zonder deploy.</summary>
    public const string BreinAuditSampleN = "brein.audit.sample_n";

    /// <summary>Noodrem op de AUTOMATISCHE nachtrun (was <c>NIGHTLY_ENABLED</c>).
    /// Handmatig starten via de beheer-knop blijft altijd werken.</summary>
    public const string NightlyEnabled = "nightly.enabled";

    public const string NightlyStartHour = "nightly.start_hour";
    public const string NightlyEndHour = "nightly.end_hour";
    public const string NightlyTimeZone = "nightly.timezone";

    /// <summary>Model-alias voor de brein-interactie-extractie (#323): één van
    /// <see cref="BreinExtractModels.Aliases"/>. Default fable (expliciete keuze
    /// van Sjoerd na de 10%-audituitslag van sonnet). Gelezen op het
    /// gebruiksmoment (per mining-run), nooit gecachet over de hele job.</summary>
    public const string BreinExtractModel = "brein.extract.model";

    /// <summary>Aantal kaarten per rb-ai-batchsessie (#323): 1 = het losse pad
    /// van vóór #323, tot 250 (een hele set in één context — expliciete keuze
    /// van Sjoerd). De timeout en maxTurns schalen mee; partial salvage begrenst
    /// de blast radius van een omgevallen sessie.</summary>
    public const string BreinExtractBatchK = "brein.extract.batch_k";
}

/// <summary>Het waardetype van een beheerde instelling — bepaalt zowel de validatie
/// (<see cref="ManagedSettingsCatalog.ParseValue"/>) als het besturingselement dat
/// beheer toont (schakelaar / uur-keuze / tekstveld).</summary>
public enum SettingKind
{
    Bool,
    Hour,
    TimeZone,
    /// <summary>Klein positief geheel getal — bereik per definitie instelbaar
    /// via <see cref="SettingDefinition.Min"/>/<see cref="SettingDefinition.Max"/>
    /// (default 1 t/m 100) — bv. de audit-steekproefdichtheid "1 op de N"
    /// (#255) of de batch-K (#323, 1 t/m 250).</summary>
    Count,
    /// <summary>Eén waarde uit een GESLOTEN lijst
    /// (<see cref="SettingDefinition.Choices"/>) — bv. de model-alias (#323).
    /// Case-insensitieve invoer, genormaliseerd naar de catalogus-vorm; alles
    /// buiten de lijst is een uitlegbare weigering, nooit een vrije string.</summary>
    Choice,
}

/// <summary>Eén beheerbare instelling: wat hij heet, wat hij betekent en waar hij in
/// beheer hoort. De catalogus is de ENIGE bron van beheerbare sleutels — een sleutel
/// die hier niet staat wordt door het admin-endpoint geweigerd, zodat de tabel geen
/// vergaarbak van losse strings wordt.</summary>
/// <param name="Group">Groep in de beheer-UI: <c>brein</c> (cockpit) of
/// <c>nachtrun</c> (jobs-paneel).</param>
/// <param name="Choices">Alleen bij <see cref="SettingKind.Choice"/>: de gesloten
/// waardenlijst. De validatie hoort hier in de CATALOGUS te sluiten, niet pas in
/// de afnemer — "een schakelaar die iets accepteert dat daarna genegeerd wordt"
/// is erger dan een foutmelding.</param>
/// <param name="Min">Alleen bij <see cref="SettingKind.Count"/>: ondergrens
/// (default 1). Zelfde reden als <paramref name="Choices"/>: de catalogus-poort
/// en de afnemer-clamp moeten dezelfde grenzen hanteren.</param>
/// <param name="Max">Alleen bij <see cref="SettingKind.Count"/>: bovengrens
/// (default 100).</param>
public sealed record SettingDefinition(
    string Key, SettingKind Kind, string Group, string Label, string Description,
    IReadOnlyList<string>? Choices = null, int? Min = null, int? Max = null);

/// <summary>Het resultaat van het valideren/normaliseren van één ingevoerde waarde.
/// <see cref="Value"/> is de GENORMALISEERDE opslagvorm ("true"/"false", "22",
/// "Europe/Amsterdam"), zodat de bestaande env-parsers (die onderling nét andere
/// woorden accepteren) het over opgeslagen waarden altijd eens zijn.</summary>
public sealed record SettingParse(string? Value, string? Error)
{
    public bool Ok => Error is null;

    public static SettingParse Fail(string error) => new(null, error);
    public static SettingParse Success(string value) => new(value, null);
}

/// <summary>De catalogus van beheerbare instellingen (#254) plus hun validatie —
/// puur en testbaar zonder database of omgeving. De instellingen zelf zijn OVERRIDES:
/// zonder rij in de <c>setting</c>-tabel geldt de bestaande env-/codewaarde, dus
/// zonder ingreep verandert er niets aan het gedrag.</summary>
public static class ManagedSettingsCatalog
{
    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new(SettingKeys.BreinRetrievalEnabled, SettingKind.Bool, "brein",
            "Brein-retrieval in /ask",
            "Gebruikt de brein-graaf (GraphRAG) in /ask-antwoorden. Uit = /ask draait "
            + "exact zoals zonder brein: geen extra latency, geen gedragswijziging."),
        new(SettingKeys.BreinAuditSampleN, SettingKind.Count, "brein",
            "Steekproef-audit: 1 op N",
            "Welk deel van de gepromoveerde interacties het sterkere model beoordeelt "
            + "(1 = alles, 10 = een tiende). De audit meet alleen — hij promoveert of "
            + "degradeert nooit zelf."),
        new(SettingKeys.BreinExtractModel, SettingKind.Choice, "brein",
            "Brein-extractie: model",
            "Welk Claude-model de interactie-extractie (mining) draait. De -1m-varianten "
            + "kiezen het 1M-contextvenster voor grote batches; is de variant niet op het "
            + "abonnement beschikbaar, dan meldt het run-detail 'model_unavailable' en is "
            + "terugzetten de fix. Kosten zijn abonnement-venster, geen dollars.",
            Choices: BreinExtractModels.Aliases),
        new(SettingKeys.BreinExtractBatchK, SettingKind.Count, "brein",
            "Brein-extractie: kaarten per sessie",
            "Hoeveel kaarten één rb-ai-sessie behandelt (K). 1 = het losse pad; hoger "
            + "amortiseert de vaste sessiekost. De timeout schaalt mee met K en een "
            + "omgevallen sessie behoudt de al-gevangen kaarten (partial salvage), maar "
            + "de niet-gevangen rest komt de volgende run terug — grotere K = grotere "
            + "blast radius per sessie. Let op het nachtvenster: de deadline wordt "
            + "alleen tússen sessies getoetst, dus een sessie die vlak voor het "
            + "venster-einde start loopt in het slechtste geval haar hele budget door "
            + "(± 2,6 uur bij K=50, ± 12,5 uur bij K=250).",
            Min: 1, Max: 250),
        new(SettingKeys.NightlyEnabled, SettingKind.Bool, "nachtrun",
            "Automatische nachtrun",
            "Mag de scheduler de nachtrun zelf starten binnen het venster? Uit = de "
            + "noodrem; handmatig starten blijft werken."),
        new(SettingKeys.NightlyStartHour, SettingKind.Hour, "nachtrun",
            "Start-uur", "Begin van het nachtvenster (lokale klok, half-open)."),
        new(SettingKeys.NightlyEndHour, SettingKind.Hour, "nachtrun",
            "Eind-uur", "Einde van het nachtvenster; ook de deadline van de run."),
        new(SettingKeys.NightlyTimeZone, SettingKind.TimeZone, "nachtrun",
            "Tijdzone", "IANA-tijdzone waarin het venster geldt."),
    ];

    public static SettingDefinition? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(d => d.Key == key);

    /// <summary>Valideer en normaliseer een ingevoerde waarde voor een sleutel.
    /// Onbekende sleutel of onzin-waarde ⇒ een uitlegbare fout, nooit een stilzwijgend
    /// genegeerde instelling: een schakelaar die niets doet is erger dan een foutmelding.</summary>
    public static SettingParse ParseValue(string key, string? raw)
    {
        if (Find(key) is not { } def)
            return SettingParse.Fail($"Onbekende instelling '{key}'.");
        var v = (raw ?? "").Trim();
        if (v.Length == 0)
            return SettingParse.Fail($"'{def.Label}' heeft een waarde nodig.");

        return def.Kind switch
        {
            SettingKind.Bool => ParseBool(v) is { } b
                ? SettingParse.Success(b ? "true" : "false")
                : SettingParse.Fail($"'{v}' is geen aan/uit-waarde (gebruik aan/uit)."),
            SettingKind.Hour => int.TryParse(v, out var h) && h is >= 0 and <= 23
                ? SettingParse.Success(h.ToString())
                : SettingParse.Fail($"'{v}' is geen uur (0 t/m 23)."),
            SettingKind.TimeZone => IsKnownTimeZone(v)
                ? SettingParse.Success(v)
                : SettingParse.Fail($"'{v}' is op deze host geen bekende tijdzone "
                    + "(IANA-vorm, bv. Europe/Amsterdam)."),
            SettingKind.Count => ParseCount(def, v),
            SettingKind.Choice => ParseChoice(def, v),
            _ => SettingParse.Fail($"Onbekend waardetype voor '{key}'."),
        };
    }

    /// <summary>Count met per-definitie-bereik (#323): de catalogus-poort en de
    /// afnemer-clamp hanteren zo dezelfde grenzen — een waarde die hier door mag
    /// maar daarna stil geclampt wordt, is een schakelaar die liegt.</summary>
    private static SettingParse ParseCount(SettingDefinition def, string v)
    {
        var min = def.Min ?? 1;
        var max = def.Max ?? 100;
        return int.TryParse(v, out var n) && n >= min && n <= max
            ? SettingParse.Success(n.ToString())
            : SettingParse.Fail($"'{v}' is geen aantal ({min} t/m {max}).");
    }

    /// <summary>Gesloten keuzelijst (#323): case-insensitieve invoer wordt
    /// genormaliseerd naar de catalogus-vorm; buiten de lijst = uitlegbare
    /// weigering — nooit een vrije string richting rb-ai.</summary>
    private static SettingParse ParseChoice(SettingDefinition def, string v)
    {
        var match = (def.Choices ?? [])
            .FirstOrDefault(c => string.Equals(c, v, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? SettingParse.Success(match)
            : SettingParse.Fail(
                $"'{v}' is geen geldige keuze (gebruik {string.Join(" | ", def.Choices ?? [])}).");
    }

    /// <summary>Ruime aan/uit-lezing voor INVOER (de opslagvorm is altijd
    /// "true"/"false"). Null = geen aan/uit-woord.</summary>
    public static bool? ParseBool(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "on" or "yes" or "enabled" or "aan" => true,
        "false" or "0" or "off" or "no" or "disabled" or "uit" => false,
        _ => null,
    };

    /// <summary>Het nachtvenster moet binnen één kalenderdag vallen — de
    /// eenmaal-per-dag-dedup (<see cref="NightlyWindow.RanToday"/>) klopt niet voor een
    /// middernacht-kruisend venster. Anders dan de env-variant (die stil terugvalt op
    /// de default) WEIGERT beheer zo'n venster met uitleg: wie een knop omzet hoort te
    /// horen dat het niet kon. Null = in orde.</summary>
    public static string? ValidateWindow(int startHour, int endHour) =>
        startHour < endHour
            ? null
            : $"Het nachtvenster moet binnen één kalenderdag vallen: start ({startHour}:00) "
              + $"moet vóór eind ({endHour}:00) liggen.";

    private static bool IsKnownTimeZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
