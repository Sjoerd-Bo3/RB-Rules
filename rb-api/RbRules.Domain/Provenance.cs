using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RbRules.Domain;

/// <summary>ULID-achtige, tijd-sorteerbare identifier voor provenance-knopen
/// (<see cref="MiningRun"/>, <see cref="Assertion"/>) — fase 0a (#233). 26
/// Crockford-base32-tekens over 128 bits: 48-bit millisecondentijd in de hoge
/// bytes + 80 bits toeval. Big-endian numeriek gecodeerd, dus lexicografisch
/// sorteren = chronologisch sorteren (de "wanneer"-as zit gratis in de sleutel).
/// Bewust géén NuGet-afhankelijkheid: het Domain-project blijft dependency-arm
/// (alleen Pgvector), en de codering is klein en getest.</summary>
public static class Ulid
{
    // Crockford base32 (zonder I, L, O, U — leesbaar, geen homoglief-verwarring).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 26;

    /// <summary>Nieuwe id op basis van de UTC-klok en cryptografisch toeval.</summary>
    public static string NewUlid() => NewUlid(DateTimeOffset.UtcNow, RandomNumberGenerator.GetBytes(10));

    /// <summary>Deterministische variant (tijd + 10 toeval-bytes) — voor tests
    /// en voor callers die hun eigen entropie leveren.</summary>
    public static string NewUlid(DateTimeOffset timestamp, byte[] randomness)
    {
        ArgumentNullException.ThrowIfNull(randomness);
        if (randomness.Length != 10)
            throw new ArgumentException("ULID vereist precies 10 toeval-bytes (80 bits).", nameof(randomness));

        var ms = timestamp.ToUnixTimeMilliseconds();
        if (ms < 0)
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Tijd vóór de Unix-epoch heeft geen geldige ULID-codering.");

        // 16 bytes = 6 bytes (48-bit) tijd big-endian + 10 bytes toeval.
        var bytes = new byte[16];
        for (var i = 0; i < 6; i++)
            bytes[5 - i] = (byte)(ms >> (8 * i));
        Array.Copy(randomness, 0, bytes, 6, 10);

        return Encode(bytes);
    }

    /// <summary>128-bit big-endian → 26 Crockford-tekens, links met '0' gevuld.
    /// Vaste breedte houdt de string sorteerbaar gelijk aan de numerieke waarde.</summary>
    private static string Encode(byte[] bytes)
    {
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        Span<char> chars = stackalloc char[Length];
        for (var i = Length - 1; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, 32, out var rem);
            chars[i] = Alphabet[(int)rem];
        }
        return new string(chars);
    }
}

/// <summary>PROV-O-<em>Activity</em> (fase 0a, #233): één mining-/afleidingsrun
/// die feiten produceerde. Vult het gat tussen <see cref="RunLog"/> (te grof,
/// puur operationeel) en de losse afgeleide feiten (voorheen provenance-loos).
/// Elke <see cref="Assertion"/> verwijst hiernaar via WAS_GENERATED_BY, zodat
/// "welk model, welke prompt-versie, welk vocabulaire" van elk feit herleidbaar
/// is. Postgres is de bron van waarheid; de Neo4j-projectie is idempotent
/// herbouwbaar.</summary>
public class MiningRun
{
    /// <summary>ULID — tijd-sorteerbaar, zie <see cref="Ulid"/>.</summary>
    public required string Id { get; set; }
    /// <summary>relation | interaction | mechanic | embedding | claim | … —
    /// welk soort feit deze run afleidde (vrij, maar consistent per pipeline).</summary>
    public required string Kind { get; set; }
    /// <summary>Hash/versie van het prompt-sjabloon (bv. "reln-v7#a1b2"); null
    /// voor een puur deterministische run zonder LLM.</summary>
    public string? PromptVersion { get; set; }
    /// <summary>rb-ai-modelId (bv. "claude-opus-4-8"); null bij deterministisch.</summary>
    public string? LlmModel { get; set; }
    /// <summary>bge-m3 (of opvolger) als de run embeddings raakte; anders null.</summary>
    public string? EmbeddingModel { get; set; }
    /// <summary>Hash van de vocabulaire-snapshot (mechanics/keywords/kinds)
    /// waartegen de run resolveerde — de stale-conditie voor her-mining (§3.5).</summary>
    public string? VocabSnapshot { get; set; }
    /// <summary>Git-SHA van de build die de run draaide (reproduceerbaarheid).</summary>
    public string? GitSha { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Null zolang de run loopt/afbrak — een half-afgemaakte run is
    /// herkenbaar en telt niet als schone provenance-bron.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    public int Candidates { get; set; }
    public int Verified { get; set; }
    public int Rejected { get; set; }
}

/// <summary>PROV-O-gereïficeerd feit-met-herkomst (fase 0a, #233): de canonieke
/// provenance-envelop rond elk afgeleid feit. Versla faalmodus #4 (ontbrekende
/// provenance) structureel — een Assertion die niet zowel WAS_GENERATED_BY
/// (<see cref="MiningRunId"/>) als DERIVED_FROM (<see cref="DerivedFromRef"/>)
/// draagt, wordt door <see cref="AssertionProvenanceGuard"/> en de
/// DbContext-schrijfpoort geweigerd (defense-in-depth). <see cref="Subject"/> is
/// de BrainRef van het feit dat wordt beweerd (bv. "relation:42"); zo hangt de
/// provenance aan het feit zonder de feit-tabellen zelf te vervuilen.</summary>
public class Assertion
{
    /// <summary>ULID, zie <see cref="Ulid"/>.</summary>
    public required string Id { get; set; }
    /// <summary>BrainRef van het beweerde feit (<see cref="BrainRef.Format"/>),
    /// bv. "relation:42" of "card:ogn-011-298". De koppeling feit→herkomst
    /// zonder de feit-tabel een provenance-kolom te geven.</summary>
    public required string Subject { get; set; }
    /// <summary>relation | card_interaction | mechanic | embedding | … — het
    /// soort feit (matcht de audit-teller per feit-tabel, §3.4).</summary>
    public required string FactKind { get; set; }
    /// <summary>WAS_GENERATED_BY → <see cref="MiningRun"/> (verplicht).</summary>
    public required string MiningRunId { get; set; }
    public MiningRun? MiningRun { get; set; }
    /// <summary>DERIVED_FROM (verplicht): BrainRef van de bron waaruit het feit
    /// is afgeleid — "source:core-rules-pdf", "section:core-rules-pdf/7.4",
    /// "card:…". De schrijfpoort eist een niet-lege, parseerbare ref.</summary>
    public required string DerivedFromRef { get; set; }
    /// <summary>Optionele precieze documentversie (draagt de content-hash/sha256
    /// van exact die brontekst) — null als het feit niet uit één documentrij
    /// komt. Aanvullend op <see cref="DerivedFromRef"/>, niet in plaats daarvan.</summary>
    public long? DerivedFromDocumentId { get; set; }
    // Model-/prompt-stempel (gedenormaliseerd naast de MiningRun voor snelle
    // per-feit-inspectie zonder join — de MiningRun blijft de bron).
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public string? EmbeddingModel { get; set; }
    public int? EmbeddingDim { get; set; }
    // VERIFIED_BY (fase 0a: als properties; de Verification-knoop is een latere
    // verfijning — de schrijfpoort gate't alleen WAS_GENERATED_BY/DERIVED_FROM).
    public string? Verifier { get; set; }
    public string? EvidenceSpan { get; set; }
    public string? Verdict { get; set; }
    /// <summary>Wanneer het beweerde feit in de wereld ging gelden (valid-time),
    /// bv. de errata-ingangsdatum; null = onbekend. Bewust géén volledige
    /// bitemporaliteit (kritiek B8) — alleen deze lichte valid-time naast de
    /// record-tijd.</summary>
    public DateTimeOffset? ValidFrom { get; set; }
    /// <summary>Record-/transactietijd: wanneer deze Assertion is vastgelegd.</summary>
    public DateTimeOffset AssertedAt { get; set; } = DateTimeOffset.UtcNow;

    public BrainRef Ref => BrainRef.Assertion(Id);
}

/// <summary>Deterministische schrijfpoort (fase 0a, #233) — de .NET-helft van
/// het dubbele write-guard (de andere helft is de DbContext-override die dit
/// aanroept, plus de Neo4j-uniciteitsconstraint). Puur, zonder IO: een
/// <see cref="Assertion"/> zonder zowel WAS_GENERATED_BY als DERIVED_FROM haalt
/// de shape niet en faalt hard — provenance is een invariant, geen discipline.</summary>
public static class AssertionProvenanceGuard
{
    public enum Code { MissingGeneratedBy, MissingDerivedFrom, InvalidDerivedFromRef, MissingSubject, MissingFactKind }

    public sealed record Violation(Code Code, string Message);

    public sealed record Result(bool IsValid, IReadOnlyList<Violation> Violations)
    {
        public string? Reason => Violations.Count == 0 ? null : string.Join("; ", Violations.Select(v => v.Message));
        public static readonly Result Valid = new(true, []);
    }

    /// <summary>Valideert de provenance-shape van één Assertion.</summary>
    public static Result Validate(Assertion a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var v = new List<Violation>();

        if (string.IsNullOrWhiteSpace(a.Subject))
            v.Add(new(Code.MissingSubject, "Assertion mist een subject-ref."));
        if (string.IsNullOrWhiteSpace(a.FactKind))
            v.Add(new(Code.MissingFactKind, "Assertion mist een factKind."));

        if (string.IsNullOrWhiteSpace(a.MiningRunId))
            v.Add(new(Code.MissingGeneratedBy,
                "Assertion mist WAS_GENERATED_BY (miningRunId) — provenance is verplicht."));

        if (string.IsNullOrWhiteSpace(a.DerivedFromRef))
            v.Add(new(Code.MissingDerivedFrom,
                "Assertion mist DERIVED_FROM (derivedFromRef) — provenance is verplicht."));
        else if (!BrainRef.TryParse(a.DerivedFromRef, out _))
            v.Add(new(Code.InvalidDerivedFromRef,
                $"derivedFromRef '{a.DerivedFromRef}' is geen geldige BrainRef."));

        return v.Count == 0 ? Result.Valid : new(false, v);
    }
}

/// <summary>Embedding-provenance (fase 0a, #233): dimensie/model-herkomst is
/// heilig. Elke embedding-rij hoort model + dim + content-hash te dragen, zodat
/// een modelwissel gericht her-embedt en een dimensie-mismatch onmogelijk stil
/// insluipt. Puur: de content-hash is een SHA-256 van de exacte tekst die
/// werd geëmbed.</summary>
public static class EmbeddingProvenance
{
    /// <summary>SHA-256 (hex, lowercase) van de te embedden tekst — de sleutel
    /// om te zien of een rij her-embed moet worden (tekst gewijzigd) of niet.</summary>
    public static string ContentHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Ring-A-poort per rij: draagt de embedding een geldig model
    /// (verwacht bge-m3), de verwachte dimensie en een content-hash?</summary>
    public static bool IsComplete(string? model, int dim, string? contentHash) =>
        !string.IsNullOrWhiteSpace(model)
        && string.Equals(model, EmbeddingConfig.Model, StringComparison.Ordinal)
        && dim == EmbeddingConfig.Dimensions
        && !string.IsNullOrWhiteSpace(contentHash);
}

/// <summary>Ring-A-provenance-gate (fase 0a, #233) — puur, €0, geen LLM. Vat de
/// tellingen van <c>ProvenanceAuditService</c> samen tot één harde CI-uitspraak:
/// nieuw geïngeste feiten mogen geen enkele ontbrekende provenance hebben. Split
/// bewust "nieuw" (moet 0 zijn — de gate) van "legacy backfill" (geïnventariseerd,
/// niet stilzwijgend gedoogd, maar blokkeert de gate niet).</summary>
public static class ProvenanceAudit
{
    /// <param name="FactsMissingAssertion">Afgeleide feiten (relation,
    /// card_interaction, …) zonder bijbehorende <see cref="Assertion"/>.</param>
    /// <param name="EmbeddingsMissingProvenance">Embedding-rijen zonder
    /// model/dim/content-hash.</param>
    /// <param name="LegacyBacklog">Vóór fase 0a bestaande provenance-loze feiten
    /// — geïnventariseerd voor backfill, telt niet mee in de gate.</param>
    public sealed record Report(
        int FactsMissingAssertion,
        int EmbeddingsMissingProvenance,
        int LegacyBacklog)
    {
        /// <summary>De harde gate: alles wat ná fase 0a is geschreven moet
        /// volledige provenance hebben.</summary>
        public bool Passes => FactsMissingAssertion == 0 && EmbeddingsMissingProvenance == 0;

        public string Summary => Passes
            ? LegacyBacklog == 0
                ? "Provenance compleet."
                : $"Provenance compleet voor nieuwe feiten; {LegacyBacklog} legacy-rij(en) te backfillen."
            : $"Provenance-gat: {FactsMissingAssertion} feit(en) zonder Assertion, " +
              $"{EmbeddingsMissingProvenance} embedding(s) zonder herkomst.";
    }
}
