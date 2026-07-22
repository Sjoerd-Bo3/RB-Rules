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

    /// <summary>Gesloten model-alias voor de tool-forced brein-extractie (#325).
    /// rb-ai resolveert de alias naar provider + concreet model; model-id's zijn
    /// hier bewust niet vrij invoerbaar.</summary>
    public const string BreinExtractModel = "brein.extract.model";

    /// <summary>Noodrem op de AUTOMATISCHE nachtrun (was <c>NIGHTLY_ENABLED</c>).
    /// Handmatig starten via de beheer-knop blijft altijd werken.</summary>
    public const string NightlyEnabled = "nightly.enabled";

    public const string NightlyStartHour = "nightly.start_hour";
    public const string NightlyEndHour = "nightly.end_hour";
    public const string NightlyTimeZone = "nightly.timezone";
}

/// <summary>Het waardetype van een beheerde instelling — bepaalt zowel de validatie
/// (<see cref="ManagedSettingsCatalog.ParseValue"/>) als het besturingselement dat
/// beheer toont (schakelaar / uur-keuze / tekstveld).</summary>
public enum SettingKind
{
    Bool,
    Hour,
    TimeZone,
    /// <summary>Klein positief geheel getal (1 t/m 100) — bv. de
    /// audit-steekproefdichtheid "1 op de N" (#255).</summary>
    Count,
    /// <summary>Keuze uit de gesloten <see cref="SettingDefinition.Options"/>.</summary>
    Choice,
}

/// <summary>Eén beheerbare instelling: wat hij heet, wat hij betekent en waar hij in
/// beheer hoort. De catalogus is de ENIGE bron van beheerbare sleutels — een sleutel
/// die hier niet staat wordt door het admin-endpoint geweigerd, zodat de tabel geen
/// vergaarbak van losse strings wordt.</summary>
/// <param name="Group">Groep in de beheer-UI: <c>brein</c> (cockpit) of
/// <c>nachtrun</c> (jobs-paneel).</param>
public sealed record SettingDefinition(
    string Key, SettingKind Kind, string Group, string Label, string Description,
    IReadOnlyList<string>? Options = null);

/// <summary>Stabiele aliases aan de rb-api-grens (#325). Alleen rb-ai kent de
/// provider/model-id-mapping; deze lijst voorkomt dat beheer een vrije string tot
/// aan een SDK of externe API kan laten reizen.</summary>
public static class BreinExtractModelAliases
{
    public const string Sonnet = "sonnet";
    public const string Opus = "opus";
    public const string Fable = "fable";
    public const string Codex = "codex";

    public static readonly IReadOnlyList<string> All =
        [Sonnet, Opus, Fable, Codex];

    public static bool IsValid(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}

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
            "Extractiemodel",
            "Model-alias voor interactie- en predicaatextractie. De alias resolveert "
            + "in rb-ai naar een vaste provider en model-id; vrije modelnamen zijn niet toegestaan.",
            BreinExtractModelAliases.All),
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
            SettingKind.Count => int.TryParse(v, out var n) && n is >= 1 and <= 100
                ? SettingParse.Success(n.ToString())
                : SettingParse.Fail($"'{v}' is geen aantal (1 t/m 100)."),
            SettingKind.Choice => def.Options?.Contains(v, StringComparer.Ordinal) == true
                ? SettingParse.Success(v)
                : SettingParse.Fail($"'{v}' is geen geldige keuze voor '{def.Label}' "
                    + $"(kies {string.Join(", ", def.Options ?? [])})."),
            _ => SettingParse.Fail($"Onbekend waardetype voor '{key}'."),
        };
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
