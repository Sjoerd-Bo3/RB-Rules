using Pgvector;

namespace RbRules.Domain;

/// <summary>Vaste embedding-configuratie. Dimensie is in de migraties gebakken
/// (getypte vector-kolommen); een modelwissel vereist dus een expliciete
/// migratie + her-embed — nooit meer een stille dimensie-mismatch.</summary>
public static class EmbeddingConfig
{
    /// <summary>bge-m3 (meertalig, NL↔EN) via Ollama.</summary>
    public const int Dimensions = 1024;
    public const string Model = "bge-m3";
}

public class Source
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public required string Type { get; set; }          // official | community
    public short TrustTier { get; set; }               // 1 (officieel) .. 4
    public int Rank { get; set; }
    public required string Parser { get; set; }        // html | pdf | json_api
    public required string Cadence { get; set; }       // daily | weekly
    public bool Enabled { get; set; } = true;
    public string? LastHash { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
}

public class Document
{
    public long Id { get; set; }
    public required string SourceId { get; set; }
    public Source? Source { get; set; }
    public required string Content { get; set; }
    public required string ContentHash { get; set; }
    /// <summary>Werkelijke bestands-URL bij PDF-bronnen (de bron-URL is de
    /// ontdek-pagina) — basis voor deeplinks als "…rules.pdf#page=12".</summary>
    public string? FileUrl { get; set; }
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Change
{
    public long Id { get; set; }
    public required string SourceId { get; set; }
    public Source? Source { get; set; }
    public string ChangeType { get; set; } = "unknown"; // ban|errata|core-rule|tournament-rule|set-release|editorial
    public string Severity { get; set; } = "medium";    // high|medium|low
    public string? Summary { get; set; }
    public string? Meaning { get; set; }
    public string? Diff { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Conflict
{
    public long Id { get; set; }
    public required string Topic { get; set; }
    public string? SourceAId { get; set; }
    public string? SourceBId { get; set; }
    public required string Kind { get; set; }           // stale | contradiction
    public string? WinnerSourceId { get; set; }
    /// <summary>Audit-fix: werd door de PoP wel geparsed maar nooit opgeslagen.</summary>
    public string? Explanation { get; set; }
    public string Status { get; set; } = "open";        // open|reviewed|resolved
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Correction
{
    public long Id { get; set; }
    public required string Scope { get; set; }          // card | rule_section | answer
    public required string Ref { get; set; }
    public required string Text { get; set; }
    public string? Question { get; set; }
    public string? Provenance { get; set; }
    public string Status { get; set; } = "unverified";  // unverified|verified
    public Vector? Embedding { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedAt { get; set; }
}

public class CardSet
{
    public required string SetId { get; set; }          // 'OGN'
    public required string Name { get; set; }
    public DateOnly? PublishedOn { get; set; }
    public int? CardCount { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Card
{
    public required string RiftboundId { get; set; }    // 'ogn-011-298'
    public required string Name { get; set; }
    public string? Type { get; set; }
    public string? Supertype { get; set; }
    public string? Rarity { get; set; }
    public string[] Domains { get; set; } = [];
    public int? Energy { get; set; }
    public int? Might { get; set; }
    public int? Power { get; set; }
    public string? SetId { get; set; }
    public string? SetLabel { get; set; }
    public int? CollectorNumber { get; set; }
    public string? TextPlain { get; set; }
    public string? ImageUrl { get; set; }
    public string[] Tags { get; set; } = [];            // facties/tribes — GEEN mechanieken
    /// <summary>F3: LLM-geminede spelmechanieken (Accelerate, Tank, …).
    /// null = nog niet gemined; [] = gemined, niets gevonden.</summary>
    public string[]? Mechanics { get; set; }
    /// <summary>F3: genormaliseerde trigger-clausules ("when a unit dies").</summary>
    public string[]? Triggers { get; set; }
    /// <summary>F3: genormaliseerde effect-clausules ("kill a unit").</summary>
    public string[]? Effects { get; set; }
    /// <summary>S1-fundament: kaart-embedding voor semantisch zoeken.</summary>
    public Vector? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }         // provenance (model-wissel-guard)
    /// <summary>Alt-art/promo/herdruk-groepering: null = canonieke printing,
    /// anders het RiftboundId van de canonieke kaart met dezelfde naam.</summary>
    public string? VariantOf { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class RuleChunk
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public Document? Document { get; set; }
    public required string SourceId { get; set; }
    public string? SectionCode { get; set; }
    /// <summary>Audit-fix: chunk-volgorde was onherstelbaar in de PoP.</summary>
    public int ChunkIndex { get; set; }
    /// <summary>PDF-pagina waar de sectie begint (deeplink #page=N).</summary>
    public int? Page { get; set; }
    public required string Text { get; set; }
    public Vector? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
}

public class RunLog
{
    public long Id { get; set; }
    public required string Kind { get; set; }           // scan|cards|embed|conflicts|graph
    public string? Ref { get; set; }
    public required string Status { get; set; }         // ok|changed|new|unchanged|error|info
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Gecachete LLM-uitleg waarom twee kaarten op elkaar lijken (#30).
/// Paar is geordend (CardAId &lt; CardBId); cache invalideert bij tekstwijziging.</summary>
public class SimilarityExplanation
{
    public long Id { get; set; }
    public required string CardAId { get; set; }
    public required string CardBId { get; set; }
    public required string Text { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Duurmeting per rulings-vraag — voedt de "gemiddeld ±Xs"-indicatie
/// op de vraagpagina met echte cijfers i.p.v. een schatting.</summary>
public class AskMetric
{
    public long Id { get; set; }
    public int DurationMs { get; set; }
    public string? QuestionType { get; set; }
    public bool HadImage { get; set; }
    public bool Ok { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Denkstappen-trace per rulings-vraag (#40, admin-only): welke
/// route nam de vraag door de pipeline en met welke context.</summary>
public class AskTrace
{
    public long Id { get; set; }
    public required string Question { get; set; }
    public string? QuestionType { get; set; }
    public string? SourceBias { get; set; }
    public bool MentionsCard { get; set; }
    /// <summary>Herkende mechaniek-keywords, komma-gescheiden.</summary>
    public string? MechanicMatches { get; set; }
    /// <summary>§-codes van de meegegeven citaties, in RRF-volgorde.</summary>
    public string? Sections { get; set; }
    /// <summary>Kaartnamen die als context meegingen.</summary>
    public string? ContextCards { get; set; }
    public int VerifiedRulings { get; set; }
    public string? Model { get; set; }                  // cheap|hard
    public bool HadImage { get; set; }
    public int DurationMs { get; set; }
    public bool Ok { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class PushSubscription
{
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Gestructureerde ban-entry (audit-fix: geen "zin bevat 'ban'"-
/// heuristiek meer). Bron: LLM-extractie uit de officiële Rules Hub.</summary>
public class BanEntry
{
    public long Id { get; set; }
    public required string Name { get; set; }           // zoals gepubliceerd
    public string? CardRiftboundId { get; set; }        // gematcht op kaartnaam
    public required string Kind { get; set; }           // card | battlefield
    public string Format { get; set; } = "constructed";
    public DateOnly? EffectiveFrom { get; set; }
    public required string SourceUrl { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Gestructureerde errata: de actuele (oracle-)tekst van een kaart.</summary>
public class Erratum
{
    public long Id { get; set; }
    public required string CardName { get; set; }
    public string? CardRiftboundId { get; set; }
    public required string NewText { get; set; }
    public required string SourceUrl { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>S3: LLM-geverifieerde kaart↔kaart-interactie (kandidaten komen uit
/// trigger↔effect/mechanic-overlap; alleen geverifieerde paren worden bewaard).</summary>
public class CardInteraction
{
    public long Id { get; set; }
    public required string CardAId { get; set; }
    public required string CardBId { get; set; }
    public required string Kind { get; set; }       // combo | synergy | counter | nonbo
    public required string Explanation { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
}
