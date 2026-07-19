using System.Security.Cryptography;
using System.Text;

namespace RbRules.Domain.Ontology;

/// <summary>Semantische versie (major.minor.patch) van de ontologie (fase 6, #230).
/// Puur, dependency-vrij, waarde-vergelijkbaar. De ontologie is een first-class,
/// geversioneerd artefact dat met elke set meegroeit; de bump-regels (zie
/// <see cref="OntologyBumpKind"/>) bepalen welk deel opschuift.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    /// <summary>Parse "x.y.z"; retourneert null bij een malformed string (nooit
    /// gokken — dezelfde tolerante-maar-strikte lijn als <see cref="OntologySchema.ParseEntityType"/>).</summary>
    public static SemVer? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split('.');
        if (parts.Length != 3) return null;
        if (int.TryParse(parts[0], out var ma) && ma >= 0 &&
            int.TryParse(parts[1], out var mi) && mi >= 0 &&
            int.TryParse(parts[2], out var pa) && pa >= 0)
            return new SemVer(ma, mi, pa);
        return null;
    }

    /// <summary>De volgende versie voor een bump-soort: major reset minor+patch,
    /// minor reset patch (SemVer-standaard).</summary>
    public SemVer Bump(OntologyBumpKind kind) => kind switch
    {
        OntologyBumpKind.Major => new SemVer(Major + 1, 0, 0),
        OntologyBumpKind.Minor => new SemVer(Major, Minor + 1, 0),
        OntologyBumpKind.Patch => new SemVer(Major, Minor, Patch + 1),
        _ => this,
    };

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
}

/// <summary>Bump-regels (§6): <em>patch</em> = nieuwe instanties (nieuw
/// Keyword-woord, kaarten — meest voorkomend per set, GEEN schema-wijziging);
/// <em>minor</em> = additief nieuw relatietype/subklasse; <em>major</em> =
/// klasse-split/disjointness-wijziging → her-validatie van de hele graaf.</summary>
public enum OntologyBumpKind
{
    /// <summary>Alleen nieuwe instanties (data) — de schema-structuur is
    /// ongewijzigd. Geen ontologie-versie-bump nodig; de gate blijft groen.</summary>
    Patch,
    /// <summary>Additief: een nieuwe klasse/subklasse of een nieuw relatietype,
    /// zonder iets bestaands te verwijderen of te versmallen.</summary>
    Minor,
    /// <summary>Structuurbrekend: verwijderde/hernoemde klasse of parent,
    /// versmalde relatie, of enige wijziging aan de disjointness-assen.</summary>
    Major,
}

/// <summary>De structurele signatuur van <see cref="OntologySchema"/> als drie
/// geordende string-verzamelingen (fase 6, #230). BEWUST alleen structuur (klassen +
/// parents, disjointness-paren, relatie-domain/range/kardinaliteit/traits/params) —
/// GEEN omschrijvingen: een documentatie-tweak mag de has-pending-poort niet laten
/// vuren, net zoals EF's has-model-changes alleen op de vorm let. Deterministisch en
/// ordening-stabiel zodat de vingerafdruk over machines/runs gelijk blijft.</summary>
public sealed record OntologyStructure(
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> DisjointPairs,
    IReadOnlyList<string> Relations);

/// <summary>Vat <see cref="OntologySchema"/> samen tot een <see cref="OntologyStructure"/>
/// en een SHA-256-vingerafdruk — de "vastgelegde versie-snapshot" waartegen de
/// has-pending-poort de code toetst (fase 6, #230).</summary>
public static class OntologySnapshot
{
    /// <summary>Canonieke, ordening-stabiele structuur van het huidige schema.</summary>
    public static OntologyStructure Capture()
    {
        // Klassen: "Naam<-Parent1,Parent2" (parents alfabetisch, klassen alfabetisch).
        var classes = OntologySchema.Classes.Values
            .Select(c => $"{c.Type}<-{string.Join(",", c.DirectParents.Select(p => p.ToString()).OrderBy(s => s, StringComparer.Ordinal))}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // Disjointness-paren ongeordend genormaliseerd ("A|B" met A<B) → alfabetisch.
        var disjoint = OntologySchema.DisjointPairs
            .Select(p =>
            {
                var a = p.A.ToString();
                var b = p.B.ToString();
                return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        // Relaties: "EDGE:dom>rng:min..max:traits:[params]".
        var relations = OntologySchema.Relations.Values
            .Select(r =>
            {
                var dom = string.Join(",", r.Domain.Select(d => d.ToString()).OrderBy(s => s, StringComparer.Ordinal));
                var rng = string.Join(",", r.Range.Select(d => d.ToString()).OrderBy(s => s, StringComparer.Ordinal));
                var max = r.MaxCardinality?.ToString() ?? "*";
                var pars = string.Join(",", r.Parameters.OrderBy(s => s, StringComparer.Ordinal));
                return $"{r.EdgeName}:{dom}>{rng}:{r.MinCardinality}..{max}:{r.Traits}:[{pars}]";
            })
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return new OntologyStructure(classes, disjoint, relations);
    }

    /// <summary>SHA-256 (hex, lowercase) over de gecanoniseerde structuur. Elke
    /// sectie krijgt een prefix zodat een string niet per ongeluk van de ene naar
    /// de andere sectie kan "lekken" en zo een botsing maskeren.</summary>
    public static string Fingerprint(OntologyStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var sb = new StringBuilder();
        sb.Append("CLASSES\n");
        foreach (var c in structure.Classes) sb.Append(c).Append('\n');
        sb.Append("DISJOINT\n");
        foreach (var d in structure.DisjointPairs) sb.Append(d).Append('\n');
        sb.Append("RELATIONS\n");
        foreach (var r in structure.Relations) sb.Append(r).Append('\n');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>De vingerafdruk van het huidige schema (het "code"-zijde-artefact).</summary>
    public static string CurrentFingerprint() => Fingerprint(Capture());
}

/// <summary>De VASTGELEGDE ontologie-versie + haar structuur-vingerafdruk (fase 6,
/// #230) — het checked-in equivalent van EF's model-snapshot. Wijzig je
/// <see cref="OntologySchema"/> (de ENIGE schema-bron), dan wijkt
/// <see cref="OntologySnapshot.CurrentFingerprint"/> af van
/// <see cref="Fingerprint"/> en faalt <see cref="OntologyChangeGate"/> — precies
/// zoals <c>has-pending-model-changes</c>. Herstel: bepaal de bump-soort
/// (<see cref="OntologyBumpClassifier"/>), verhoog <see cref="Version"/> en werk
/// <see cref="Fingerprint"/> bij (nooit hand-patchen zonder bewuste bump — dat is
/// de discipline die de poort afdwingt).</summary>
public static class OntologyBaseline
{
    /// <summary>De laatst vastgelegde ontologie-versie.</summary>
    public static readonly SemVer Version = new(1, 0, 0);

    /// <summary>SHA-256-vingerafdruk van de structuur bij <see cref="Version"/>.
    /// Bijwerken is een BEWUSTE handeling samen met een versie-bump.</summary>
    public const string Fingerprint = "e1ab8d63a20136483224354de904c5739a79c3f623a95366837b48626d5397c7";
}

/// <summary>De has-pending-ontology-changes-poort (fase 6, #230) — puur, €0, geen
/// LLM, geschikt als CI-gate (spiegelt <c>has-pending-model-changes</c>). Faalt
/// zodra de code-structuur van <see cref="OntologySchema"/> afwijkt van de
/// vastgelegde <see cref="OntologyBaseline"/>.</summary>
public static class OntologyChangeGate
{
    public sealed record Report(
        SemVer RecordedVersion,
        string RecordedFingerprint,
        string ComputedFingerprint)
    {
        /// <summary>De ontologie-code loopt vóór op de vastgelegde snapshot.</summary>
        public bool HasPendingChanges =>
            !string.Equals(RecordedFingerprint, ComputedFingerprint, StringComparison.Ordinal);

        /// <summary>De harde gate: groen zolang code en snapshot gelijk zijn.</summary>
        public bool Passes => !HasPendingChanges;

        public string Summary => Passes
            ? $"Ontologie op versie {RecordedVersion} in sync met de vastgelegde snapshot."
            : $"Openstaande ontologie-wijziging: code-vingerafdruk {ComputedFingerprint} " +
              $"wijkt af van de vastgelegde {RecordedFingerprint} (versie {RecordedVersion}). " +
              "Bepaal de bump-soort, verhoog OntologyBaseline.Version en werk de Fingerprint bij.";
    }

    /// <summary>Toets de huidige <see cref="OntologySchema"/> tegen de baseline.</summary>
    public static Report Check() => new(
        OntologyBaseline.Version,
        OntologyBaseline.Fingerprint,
        OntologySnapshot.CurrentFingerprint());
}

/// <summary>Bepaalt de vereiste bump-soort door twee structuren te vergelijken
/// (fase 6, §6). Puur: additief (nieuwe klasse/relatie) → <c>Minor</c>; enige
/// wijziging aan de disjointness-assen óf een verwijderde/versmalde klasse/relatie
/// → <c>Major</c>; identiek → <c>Patch</c> (alleen instanties veranderden, geen
/// schema-bump nodig).</summary>
public static class OntologyBumpClassifier
{
    public static OntologyBumpKind Classify(OntologyStructure previous, OntologyStructure current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var prevClasses = previous.Classes.ToHashSet(StringComparer.Ordinal);
        var curClasses = current.Classes.ToHashSet(StringComparer.Ordinal);
        var prevRel = previous.Relations.ToHashSet(StringComparer.Ordinal);
        var curRel = current.Relations.ToHashSet(StringComparer.Ordinal);
        var prevDis = previous.DisjointPairs.ToHashSet(StringComparer.Ordinal);
        var curDis = current.DisjointPairs.ToHashSet(StringComparer.Ordinal);

        if (prevClasses.SetEquals(curClasses) && prevRel.SetEquals(curRel) && prevDis.SetEquals(curDis))
            return OntologyBumpKind.Patch;

        // Disjointness is een as op zich: elke wijziging (ook additief) her-valideert
        // de hele graaf → major (§6: "klasse-split/disjointness-wijziging").
        if (!prevDis.SetEquals(curDis))
            return OntologyBumpKind.Major;

        // Iets uit de oude structuur ontbreekt/veranderde (verwijderd, hernoemd,
        // versmald) → structuurbrekend → major.
        var classesShrank = !prevClasses.IsSubsetOf(curClasses);
        var relationsShrank = !prevRel.IsSubsetOf(curRel);
        if (classesShrank || relationsShrank)
            return OntologyBumpKind.Major;

        // Zuiver additief (nieuwe klasse/subklasse of nieuw relatietype).
        return OntologyBumpKind.Minor;
    }
}

/// <summary>Vastgelegde, toegepaste ontologie-versie als rij (fase 6, #230) — de
/// provenance-tak van de schema-evolutie: WELKE versie, met WELKE
/// structuur-vingerafdruk, via WELKE bump, op WELK moment, DOOR welke run. Postgres
/// is de bron van waarheid; elke migratie is een <em>Activity</em> in de
/// provenance-graaf (EF-Core-achtig, idempotent). De rij verdwijnt nooit — de
/// versie-historie is het audit-spoor.</summary>
public class OntologyVersionRecord
{
    public long Id { get; set; }

    /// <summary>Semver als tekst ("1.2.0") — sorteerbaar via de service, opgeslagen
    /// als string zodat de rij zelf-beschrijvend blijft.</summary>
    public required string Version { get; set; }

    /// <summary>De structuur-vingerafdruk (<see cref="OntologySnapshot.Fingerprint"/>)
    /// bij deze versie — zo is een oude graaf-projectie herleidbaar tot precies het
    /// schema dat toen gold.</summary>
    public required string Fingerprint { get; set; }

    /// <summary>patch | minor | major (<see cref="OntologyBumpKind"/> als string).</summary>
    public required string BumpKind { get; set; }

    /// <summary>Korte, leesbare aanleiding ("set OGN: +REDIRECTS relatietype").</summary>
    public required string Notes { get; set; }

    /// <summary>0a-provenance (#233): de run/migratie die deze versie vastlegde.</summary>
    public required string RunId { get; set; }

    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;
}
