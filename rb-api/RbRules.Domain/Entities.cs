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
    /// <summary>Wanneer de claims-pipeline (#50) dit document verwerkte;
    /// null = nog niet. Een nieuwe documentversie (bronwijziging) krijgt een
    /// eigen rij en wordt dus vanzelf opnieuw gemined.</summary>
    public DateTimeOffset? ClaimsMinedAt { get; set; }
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
    /// <summary>Ingelogde vrager (#42) — voedt de per-account-dagquota en het
    /// kosten-overzicht in het beheer. Null = anonieme vraag.</summary>
    public long? UserId { get; set; }
    /// <summary>cheap|hard|agentic — het pad dat het antwoord écht leverde
    /// (kostenindicatie, #42/#107). AskTrace kent het model ook, maar die
    /// tabel bewaart alleen de laatste 200 traces; hier blijft de verdeling
    /// over langere periodes optelbaar.</summary>
    public string? Model { get; set; }
    /// <summary>Agentic ask (#107, docs/BRAIN.md §2.4): het antwoord kwam
    /// daadwerkelijk van de agent — zo toont de duurstatistiek beide paden
    /// apart. Vangnet-inzet (agent faalde, single-pass antwoordde) telt als
    /// false en is herkenbaar aan de marker in AskTrace.BrainSteps.</summary>
    public bool Agentic { get; set; }
    /// <summary>Echte token-tellingen per vraag (#121), opgeteld over álle
    /// LLM-calls die de vraag kostte (rewrite + antwoord; bij agentic alle
    /// beurten incl. tool-overhead — input telt rb-ai's cache-tokens mee).
    /// Null = geen enkele call gaf usage terug (oude rb-ai of AI-uitval):
    /// onbekend is niet hetzelfde als 0.</summary>
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Kennisbank-document (docs/KNOWLEDGE.md). Kind "primer" =
/// gedistilleerd spelbegrip; draft → door de beheerder approved, daarna
/// doet het doc mee in de /ask-context.</summary>
public class KnowledgeDoc
{
    public long Id { get; set; }
    public required string Kind { get; set; }           // primer | (later: claim-samenvatting …)
    public required string Topic { get; set; }          // PrimerTopics.Key
    public required string Title { get; set; }
    public required string Body { get; set; }
    /// <summary>§-codes waarop het doc gebaseerd is, komma-gescheiden.</summary>
    public string? SectionRefs { get; set; }
    public string Status { get; set; } = "draft";       // draft | approved
    public Vector? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    /// <summary>Wanneer de relatie-mining (#116) dit doc als anker verwerkte;
    /// null = nog niet. Zelf-invaliderend: een run pakt docs waarvan
    /// relations_mined_at vóór updated_at ligt vanzelf opnieuw op.</summary>
    public DateTimeOffset? RelationsMinedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Denkstappen-trace per rulings-vraag (#40, admin-only): welke
/// route nam de vraag door de pipeline en met welke context.</summary>
public class AskTrace
{
    public long Id { get; set; }
    public required string Question { get; set; }
    public string? QuestionType { get; set; }
    /// <summary>#66: LLM-herformulering waarmee gezocht is (zoekzin, queries,
    /// lexicale termen); null = rewrite mislukt, gezocht met de rauwe vraag.</summary>
    public string? RewrittenQuery { get; set; }
    public string? SourceBias { get; set; }
    public bool MentionsCard { get; set; }
    /// <summary>Herkende mechaniek-keywords, komma-gescheiden.</summary>
    public string? MechanicMatches { get; set; }
    /// <summary>§-codes van de meegegeven citaties, in RRF-volgorde.</summary>
    public string? Sections { get; set; }
    /// <summary>Kaartnamen die als context meegingen.</summary>
    public string? ContextCards { get; set; }
    /// <summary>Titels van de primer-docs die als spelbegrip meegingen.</summary>
    public string? PrimerDocs { get; set; }
    /// <summary>Community-claims (kennislaag 2, #51) die als context meegingen,
    /// als "topicType:topicRef", komma-gescheiden.</summary>
    public string? CommunityClaims { get; set; }
    public int VerifiedRulings { get; set; }
    public string? Model { get; set; }                  // cheap|hard|agentic
    public bool HadImage { get; set; }
    public int DurationMs { get; set; }
    /// <summary>Agentic ask (#107): het antwoord kwam daadwerkelijk van de
    /// agent (docs/BRAIN.md §2.4). Escalaties die op het vangnet eindigden
    /// staan op false en zijn herkenbaar aan de marker in BrainSteps.</summary>
    public bool Agentic { get; set; }
    /// <summary>Brein-stappen van de agent (#107): één regel per tool-call
    /// (toolnaam + argumenten), zoals rb-ai ze teruggeeft — bij vangnet-inzet
    /// de vóór de uitval al gedane stappen plus een expliciete marker; null
    /// zolang de vraag niet escaleerde.</summary>
    public string? BrainSteps { get; set; }
    public bool Ok { get; set; } = true;
    /// <summary>Ingelogde vrager (#42); null = anoniem.</summary>
    public long? UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Account voor de publieke site (#42): e-mail + magic-link, bewust
/// zonder wachtwoorden. Quota zijn per gebruiker instelbaar in het beheer.</summary>
public class AppUser
{
    public long Id { get; set; }
    /// <summary>Genormaliseerd (lowercase) — zie Accounts.NormalizeEmail.</summary>
    public required string Email { get; set; }
    public bool Blocked { get; set; }
    /// <summary>Vragen per UTC-dag op /api/ask (foto-vragen tellen ook mee).</summary>
    public int DailyQuota { get; set; } = 30;
    /// <summary>Foto-vragen per UTC-dag — die forceren het dure model.</summary>
    public int DailyPhotoQuota { get; set; } = 5;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    /// <summary>WebAuthn user handle (#109): willekeurige bytes die het account
    /// richting authenticators identificeren — bewust niet het e-mailadres
    /// (spec: geen PII in de handle) en stabiel per account, zodat meerdere
    /// passkeys bij de authenticator als één account verschijnen. Null zolang
    /// het account nog geen passkey-registratie heeft gedaan.</summary>
    public byte[]? PasskeyHandle { get; set; }
}

/// <summary>Passkey-credential (WebAuthn, #109): de publieke sleutel waarmee
/// een authenticator (Face ID, vingerafdruk, security key) zich bewijst.
/// SignCount is de replay-teller (Passkeys.IsSignCountValid); Aaguid
/// identificeert het authenticator-type. Meerdere per account is juist de
/// bedoeling (telefoon + laptop).</summary>
public class PasskeyCredential
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser? User { get; set; }
    public required byte[] CredentialId { get; set; }
    public required byte[] PublicKey { get; set; }
    /// <summary>WebAuthn-teller is een uint32; long omdat Postgres geen
    /// unsigned kent. Cast naar uint richting fido2-net-lib.</summary>
    public long SignCount { get; set; }
    public Guid Aaguid { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Lopende WebAuthn-ceremonie (#109): de server-side challenge, kort
/// geldig en single-use — zelfde hygiëne als login_token (alleen de hash van
/// het client-token in de database). OptionsJson bewaart de volledige opties
/// omdat fido2-net-lib die nodig heeft om het antwoord te verifiëren.</summary>
public class PasskeyChallenge
{
    public long Id { get; set; }
    public required string TokenHash { get; set; }
    /// <summary>Passkeys.RegisterKind of Passkeys.LoginKind.</summary>
    public required string Kind { get; set; }
    /// <summary>Registratie van een nieuw account: de gekozen identifier.</summary>
    public string? Email { get; set; }
    /// <summary>Registratie van een extra passkey bij een bestaand account.</summary>
    public long? UserId { get; set; }
    public required string OptionsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Ingelogde sessie (#42): rb-web bewaart het token in een httpOnly-
/// cookie en stuurt het als X-User-Token mee; hier staat alleen de hash.</summary>
public class UserSession
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AppUser? User { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Eenmalige magic-link (#42): kort geldig, single-use, alleen de
/// hash opgeslagen. Per adres is maar één link tegelijk actief.</summary>
public class LoginToken
{
    public long Id { get; set; }
    public required string Email { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
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

/// <summary>Kennislaag 2 (#50): een geparafraseerde community-bewering over
/// hoe een regel/kaart/mechaniek/conventie in de praktijk werkt, met
/// corroboratie (hoeveel onafhankelijke bronnen hetzelfde zeggen) en een
/// gewogen trust-score. Interpretatief — officieel (laag 0) wint altijd.</summary>
public class Claim
{
    public long Id { get; set; }
    public required string TopicType { get; set; }      // card|mechanic|section|concept
    public required string TopicRef { get; set; }       // kaartnaam/mechaniek/§-code/concept
    /// <summary>De bewering, geparafraseerd in het NL (auteursrecht: nooit
    /// overgenomen tekst — het korte citaat leeft bij de bron).</summary>
    public required string Statement { get; set; }
    /// <summary>Aantal onafhankelijke bronnen dat hetzelfde beweert.</summary>
    public int Corroboration { get; set; } = 1;
    /// <summary>Gewogen bron-trust × corroboratie (0..1), zie ClaimScoring.</summary>
    public double TrustScore { get; set; }
    public string Status { get; set; } = "unreviewed";  // unreviewed|accepted|rejected|superseded
    /// <summary>Toelichting bij rejected/superseded (bijv. de officiële § die
    /// de claim tegenspreekt — "officieel wint altijd").</summary>
    public string? StatusReason { get; set; }
    /// <summary>Toets tegen officiële §'s: unchecked|confirmed|contradicted|unclear.</summary>
    public string OfficialStatus { get; set; } = "unchecked";
    /// <summary>Beheerder-notitie bij het reviewen (#124, "zo zit het wél") —
    /// blijft bij het item staan en is via de promotie-actie door te zetten
    /// als geverifieerde ruling (Correction), zodat de kennis antwoorden stuurt.</summary>
    public string? ReviewNote { get; set; }
    /// <summary>Archief (#124): gearchiveerd = uit de default-reviewweergave,
    /// terugvindbaar via de archief-chip. Puur beheer-zicht — een accepted
    /// claim blijft gewoon meedoen in /ask, gearchiveerd of niet.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
    public Vector? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Bewijsvoering per claim: welke bron beweerde dit, waar, en met
/// welk kort citaat. Eén bron telt één keer mee in de corroboratie.</summary>
public class ClaimSource
{
    public long Id { get; set; }
    public long ClaimId { get; set; }
    public Claim? Claim { get; set; }
    public required string SourceId { get; set; }
    public required string Url { get; set; }
    /// <summary>Kort letterlijk citaat als bewijs (auteursrecht: parafrase +
    /// kort citaat + bronlink, geen overgenomen teksten).</summary>
    public string? QuoteExcerpt { get; set; }
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Bronvoorstel uit de bronnenjacht (#63): een webvondst van de
/// scout als reviewqueue-item. Accepteren zet de bron met veilige defaults
/// (uitgeschakeld!) in het register; verwerpen houdt de URL uit volgende
/// runs. Niets gaat automatisch aan — trust-toekenning en activeren blijven
/// een beheerdersbeslissing (docs/KNOWLEDGE.md: bron-trust is heilig).</summary>
public class SourceProposal
{
    public long Id { get; set; }
    public required string Url { get; set; }
    public required string Name { get; set; }
    /// <summary>Type-inschatting van de scout: official | partner | community.</summary>
    public required string Type { get; set; }
    /// <summary>Waarom deze bron de kennisbank zou versterken (LLM, NL).</summary>
    public required string Motivation { get; set; }
    public string Status { get; set; } = "proposed";    // proposed|accepted|rejected
    public DateTimeOffset FoundAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
}

/// <summary>Evolutie-raamwerk (#52): groeiend mechaniek-vocabulaire. De miner
/// rapporteert bracketed termen uit kaartteksten die niet in het vocabulaire
/// staan als kandidaat; de beheerder accepteert of verwerpt ze. Geaccepteerde
/// termen tellen mee in het mining-vocabulaire en de betrokken kaarten worden
/// opnieuw gemined — zo kent het systeem "Overwhelm" op de dag dat de eerste
/// kaart ermee verschijnt.</summary>
public class MechanicKeyword
{
    public long Id { get; set; }
    public required string Term { get; set; }
    public string Status { get; set; } = "candidate";   // candidate|accepted|rejected
    /// <summary>In hoeveel kaartteksten de term voorkomt (review-sortering).</summary>
    public int Occurrences { get; set; }
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
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

/// <summary>Dynamische LLM-relatie tussen twee brein-knopen (#116): het
/// INTERACTS_WITH/claims-patroon veralgemeniseerd. Van/naar zijn BrainRefs
/// (docs/BRAIN.md §2.1) over álle kennislagen heen; het kind is een open maar
/// gereviewd vocabulaire (RelationKind). LLM-relaties gaan NOOIT rechtstreeks
/// de graph in: Postgres is de bron, de graph-rebuild projecteert alleen
/// accepted/unreviewed relaties waarvan het kind geaccepteerd is.</summary>
public class Relation
{
    public long Id { get; set; }
    public required string FromRef { get; set; }        // bv. "mechanic:Deflect"
    public required string ToRef { get; set; }          // bv. "section:core-rules-pdf/7.4"
    /// <summary>Genormaliseerd kind-label (RelationMiner.NormalizeKind),
    /// bv. "counters" of "wordt beperkt door".</summary>
    public required string Kind { get; set; }
    /// <summary>Waarom deze relatie bestaat (LLM, NL) — gaat als property mee
    /// de graph in zodat tools de relatie kunnen duiden.</summary>
    public required string Explanation { get; set; }
    /// <summary>Bewijsbron/anker van de mining, bv. "concept:combat" of
    /// "mechanics-overzicht" — herleidbaarheid per voorstel.</summary>
    public required string Provenance { get; set; }
    /// <summary>Trust van de bewijsbron (0..1, ClaimScoring-schaal) — de
    /// kennispiramide blijft leidend, ook op relaties.</summary>
    public double Trust { get; set; }
    public string Status { get; set; } = "unreviewed";  // unreviewed|accepted|rejected
    /// <summary>Beheerder-notitie bij het reviewen (#124) — zichtbaar bij het
    /// item (verwerp-reden) en promoveerbaar tot geverifieerde ruling.</summary>
    public string? ReviewNote { get; set; }
    /// <summary>Archief (#124): uit de default-reviewweergave, terugvindbaar
    /// via de archief-chip. Puur beheer-zicht — de graph-projectie kijkt
    /// alleen naar Status, niet naar het archief.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
}

/// <summary>Kandidaat-vocabulaire voor relatie-kinds (#116, patroon
/// MechanicKeyword uit #52): de LLM mag nieuwe kinds voorstellen; onbekende
/// kinds landen hier als kandidaat. Pas geaccepteerde kinds (plus de
/// seed-lijst in RelationMiner) doen mee in de graph-projectie.</summary>
public class RelationKind
{
    public long Id { get; set; }
    /// <summary>Genormaliseerd (RelationMiner.NormalizeKind).</summary>
    public required string Kind { get; set; }
    public string Status { get; set; } = "candidate";   // candidate|accepted|rejected
    /// <summary>Aantal opgeslagen relatievoorstellen met dit kind
    /// (review-sortering op impact).</summary>
    public int Occurrences { get; set; }
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
}
