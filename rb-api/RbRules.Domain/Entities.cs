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
    /// <summary>Herkomst (#167): welke <see cref="SourceFeed"/> deze bron
    /// ontdekte via de feed-crawl (alleen het AutoApprove-pad vult dit).
    /// Null = handmatig/legacy toegevoegd, of via de scout/hub-ontdekking
    /// (een ander, ouder ontdekkingspad met een eigen reviewqueue).</summary>
    public string? FeedId { get; set; }
    /// <summary>Temporele precedentie (#168): publicatiedatum van het artikel
    /// zelf, uit de bron-feed (<see cref="RiotNewsFeed.RiotNewsArticle.Date"/>)
    /// — alleen gevuld voor via <see cref="SourceFeed"/> AutoApprove ontdekte
    /// bronnen; een handmatig/legacy toegevoegde bron kent haar eigen
    /// publicatiedatum niet en blijft null (nooit raden).</summary>
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>Temporele precedentie (#168): wanneer de scan voor het laatst
    /// een écht gewijzigde inhoud detecteerde (niet elke <see
    /// cref="LastChecked"/> — alleen een reële content-wijziging, zelfde
    /// moment als het bijbehorende <see cref="Change"/>-item). Null = nog
    /// nooit een wijziging gezien sinds deze kolom bestaat.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
    /// <summary>Bron-type-classificatie (#188 increment 2, <see
    /// cref="RbRules.Domain.SourceContentKind"/>): "faq" | "patch-notes" |
    /// "other" — een LLM-BESLISSING i.p.v. de oude keyword-heuristiek (<see
    /// cref="ClarificationSources"/>). Gezet bij de scan van een trust-1-bron
    /// (<see cref="RbRules.Infrastructure.IngestService"/>). Null = nog niet
    /// geclassificeerd — consumers vallen dan terug op de heuristiek (<see
    /// cref="RbRules.Domain.SourceContentKind.Resolve"/>), zodat bestaande
    /// bronnen blijven werken totdat ze opnieuw gescand zijn.</summary>
    public string? ContentKind { get; set; }
    /// <summary>Herkomst van <see cref="ContentKind"/>: "llm", "heuristic"
    /// (AI-uitval of onbruikbaar LLM-antwoord bij de classificatie-poging) of
    /// "admin" (expliciete override via het source-PATCH-pad, #188-review —
    /// <see cref="RbRules.Domain.SourceContentKind.TryApplyOverride"/>).
    /// Een heuristische classificatie mag een latere scan alsnog naar een
    /// LLM-oordeel upgraden (nooit stilzwijgend andersom — een LLM- of
    /// admin-oordeel wordt niet opnieuw overschreven; "admin" telt in de
    /// consensus-poort van de patch-notes-retractie bovendien als menselijke
    /// bevestiging). Null zolang <see cref="ContentKind"/> zelf null is.</summary>
    public string? ContentKindSource { get; set; }
    /// <summary>Negeren met reden (#180): een BEWUSTE beoordeling dat deze
    /// bron niets aan het systeem toevoegt (merch/toernooi-/preorder-
    /// artikelen die de feed-crawl toch als trust-1 registreert) — nadrukkelijk
    /// iets anders dan <see cref="Enabled"/> ("tijdelijk uit"): een genegeerde
    /// bron kan <see cref="Enabled"/> op true laten staan, de scan-lus
    /// (<see cref="RbRules.Infrastructure.IngestService"/>) slaat 'm sowieso
    /// over. Null = niet genegeerd. Negeren is geen delete: bestaande
    /// Document/Change-rijen blijven onaangeroerd, en net als de #167-
    /// tombstone voor een verwijderde feed-bron blijft de rij zelf bestaan
    /// zodat FeedCrawlService 'm nooit stilzwijgend heradopteert of
    /// hercreëert (de known-URL-dedup ziet de rij gewoon nog staan).</summary>
    public DateTimeOffset? IgnoredAt { get; set; }
    /// <summary>Vrije tekst, alleen zinvol samen met <see cref="IgnoredAt"/>
    /// (null zolang die null is) — bv. "merch/preorder-artikel, geen
    /// regelbron" of "levert na meerdere scans niets op".</summary>
    public string? IgnoreReason { get; set; }
}

/// <summary>Bron-feed (#167): een index-pagina die periodiek wordt afgespeurd
/// op nieuwe artikel-URL's — een feed IS geen inhoudelijke bron; hij
/// <em>ontdekt</em> bronnen. <see cref="AutoApprove"/> onderscheidt een
/// vertrouwde/officiële feed (nieuw artikel ⇒ meteen een <see cref="Source"/>
/// in het register, enabled) van een minder vertrouwde (⇒ <see
/// cref="SourceProposal"/> in de reviewqueue). <see cref="CategoryFilter"/>
/// is een komma-gescheiden lijst toegestane categorieën (het
/// &lt;categorie&gt;-padsegment in playriftbound.com/en-us/news/&lt;categorie&gt;/
/// &lt;slug&gt;) — null/leeg = alle categorieën, ook artikelen zonder
/// categorie-segment. <see cref="LastHash"/> is puur een goedkope
/// skip-optimalisatie (ongewijzigde pagina ⇒ geen nieuwe artikelen mogelijk);
/// de echte idempotentie zit in de per-URL-dedupe tegen het bronnenregister
/// en de reviewqueue, dus een per-request wisselende linkvolgorde (zoals de
/// Rules Hub laat zien) kan hier nooit dubbele bronnen of ruis opleveren.</summary>
public class SourceFeed
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AutoApprove { get; set; }
    public string? CategoryFilter { get; set; }
    public required string Cadence { get; set; }       // daily | weekly
    public DateTimeOffset? LastChecked { get; set; }
    public string? LastHash { get; set; }
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
    /// <summary>Wanneer de concept-extractie voor FAQ-/clarificatie-artikelen
    /// (#177, ClarificationMiningService) dit document verwerkte; null = nog
    /// niet (of geen clarificatie-bron). Zelfde #92/#93-patroon als
    /// ClaimsMinedAt: pas gezet ná een volledig geslaagde run, zodat een
    /// gedeeltelijke of mislukte poging vanzelf terugkomt; een nieuwe
    /// documentversie krijgt een eigen rij en wordt dus opnieuw gemined.</summary>
    public DateTimeOffset? ClarifiedAt { get; set; }
    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Change
{
    public long Id { get; set; }
    public required string SourceId { get; set; }
    public Source? Source { get; set; }
    // clarification (#177): sjabloon-change bij de eerste scan van een
    // FAQ-/clarificatie-artikel (er is nog geen vorige versie om te diffen).
    public string ChangeType { get; set; } = "unknown"; // ban|errata|core-rule|tournament-rule|set-release|editorial|clarification
    public string Severity { get; set; } = "medium";    // high|medium|low
    public string? Summary { get; set; }
    public string? Meaning { get; set; }
    public string? Diff { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Changeconsolidatie (#206): verwijst naar de PRIMAIRE change
    /// als dit item hetzelfde gebeurtenis vanuit een andere bron bevestigt
    /// (bv. een community-melding van dezelfde ban die de Rules Hub al
    /// meldde). Null = dit item is zelf geen bevestiging (ofwel de primaire
    /// van een geconsolideerd paar, ofwel (nog) ongekoppeld). Wijst ALTIJD
    /// naar de wortel-primaire, nooit naar een andere secundaire — er
    /// ontstaan bewust geen ketens (<see cref="ChangeConsolidationService"/>).
    /// Beide rijen blijven bestaan (herleidbaarheid); consolidatie is een
    /// presentatie-koppeling, geen inhoudelijke waarheid (die blijft bij de
    /// structured BanEntry-/errata-precedentie, #168).</summary>
    public long? ConsolidatedWithId { get; set; }
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
    // card | rule_section | answer (chat-ruling/review-notitie-scopes) |
    // mechanic | concept (#177, ClarificationMiningService — RulingsTopics
    // kent beide al als gedeeld filter-vocabulaire, dus geen migratie nodig)
    // | claim | relation (review-notitie-promotie, #124 — bucketen als
    // "answer" in RulingsTopics, geen eigen filterknop).
    public required string Scope { get; set; }
    public required string Ref { get; set; }
    public required string Text { get; set; }
    public string? Question { get; set; }
    public string? Provenance { get; set; }
    /// <summary>"Waar besloten" (#166): URL (UrlGuard-gecheckt) of vrije
    /// citatie (Discord-thread, officiële post, toernooi + datum) — verplicht
    /// bij in-chat-rulings, optioneel voor oudere/andere ontstaanswegen van
    /// een Correction. Sanitize gebeurt bij weergave, niet bij opslag.</summary>
    public string? SourceRef { get; set; }
    // unverified | verified | rejected. "rejected" (#177 hybride poort) is een
    // tombstone: een beheerder-afwijzing van een pending clarify-item die de
    // mining respecteert (zie ClarificationMiningService — een rejected rij op
    // hetzelfde concept wordt nooit heropend). De self-learning-feedback en de
    // chat-/review-rulings gebruiken alleen unverified/verified.
    public string Status { get; set; } = "unverified";
    /// <summary>Reden dat een item (nog) niet verified is (#177 hybride poort):
    /// bv. "citaat niet terug te vinden in de bron" of "onderwerp niet
    /// herkend". Voedt de reviewqueue zodat de beheerder ziet waaróm iets ter
    /// review staat; null voor handmatig/verified aangemaakte correcties.</summary>
    public string? StatusReason { get; set; }
    /// <summary>Beheerder-opmerking (#184, zelfde patroon als
    /// <see cref="Claim.ReviewNote"/>/<see cref="Relation.ReviewNote"/>) —
    /// blijft bij het item staan (traceerbaar) en triggert een her-evaluatie
    /// (<see cref="RbRules.Infrastructure.CorrectionReevaluationService"/>)
    /// van dít ene item. Mag een anker-correctie bevatten (bv.
    /// "mechanic:Recall") die een fout-aangeankerd of onherkend onderwerp
    /// overschrijft. Een volgende clarify-mining-her-mine
    /// (<see cref="RbRules.Infrastructure.ClarificationMiningService"/>)
    /// respecteert een gezette ReviewNote: Status/StatusReason worden dan
    /// niet stilzwijgend teruggedraaid.</summary>
    public string? ReviewNote { get; set; }
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
    /// <summary>Wie de escalatie afdwong (#153): "gate" of "user"; null =
    /// niet geëscaleerd. Staat óók bij vangnet-inzet (de poging is gedaan):
    /// "user"-rijen zijn de teller voor het Grondig-dagquotum — bewust
    /// inclusief mislukte pogingen, conservatief net als het vraagquotum.</summary>
    public string? EscalatedBy { get; set; }
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
    /// <summary>Wie de escalatie afdwong (#153): "gate" of "user"; null =
    /// niet geëscaleerd — de badge "agentic (gate/gebruiker)" in de
    /// beheer-traces.</summary>
    public string? EscalatedBy { get; set; }
    /// <summary>Brein-stappen van de agent (#107): één regel per tool-call
    /// (toolnaam + argumenten), zoals rb-ai ze teruggeeft — bij vangnet-inzet
    /// de vóór de uitval al gedane stappen plus een expliciete marker; null
    /// zolang de vraag niet escaleerde.</summary>
    public string? BrainSteps { get; set; }
    /// <summary>Het volledige gesprek in de trace (#143): de definitieve
    /// antwoordtekst zoals de vrager hem kreeg — op het streamingpad het
    /// slotframe, bij AI-uitval de eerlijke UnavailableAnswer (Ok=false).</summary>
    public string? Answer { get; set; }
    /// <summary>JSON-snapshot van de eerdere beurten `[{question, answer}]`
    /// (#143) — exact de gecapte doorvraag-context die als GESPREK-blok in de
    /// prompt meeging (#41); null bij een eerste vraag.</summary>
    public string? History { get; set; }
    /// <summary>Per-fase-wandkloktijden als compacte JSON (#152, vorm:
    /// <see cref="AskPhases"/>) — rewrite/embed/retrieval/AI naast de totale
    /// DurationMs, zodat het beheer ziet wáár de tijd van een vraag zit.
    /// Null bij traces van vóór de meting.</summary>
    public string? PhaseTimings { get; set; }
    public bool Ok { get; set; } = true;
    /// <summary>Ingelogde vrager (#42); null = anoniem.</summary>
    public long? UserId { get; set; }
    /// <summary>Privacy-nette IP-koppeling (#157): HMAC-SHA256 van het
    /// client-IP (IpHashing.Hash), nooit het rauwe IP. Null als
    /// ASK_IP_HASH_SECRET ontbreekt of het IP niet vastgesteld kon worden —
    /// zo'n vraag telt dan niet mee in de anonieme ask-geschiedenis (#157).</summary>
    public string? IpHash { get; set; }
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
    /// <summary>Zelf geforceerde Grondig-vragen per UTC-dag (#153) — die
    /// forceren de brein-agent. Alleen gehonoreerde gebruikerskeuzes tellen
    /// (metric-rijen met EscalatedBy "user"); gate-escalaties niet.</summary>
    public int DailyAgenticQuota { get; set; } = 5;
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
    /// <summary>Temporele precedentie (#168): vanaf wanneer deze errata-tekst
    /// gold, afgeleid van de bron die haar publiceerde (<see
    /// cref="Source.UpdatedAt"/> ?? <see cref="Source.PublishedAt"/> van de
    /// bron achter <see cref="SourceUrl"/>). Null als die bron geen datum
    /// draagt — nooit raden. Tie-breaker bij meerdere errata over dezelfde
    /// kaart (zie <see cref="Precedence"/>): hoogste TrustTier, dan nieuwste
    /// EffectiveFrom wint.</summary>
    public DateOnly? EffectiveFrom { get; set; }
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

    /// <summary>LLM-triage-aanbeveling (#199 v1, <see
    /// cref="RbRules.Infrastructure.RelationTriageService"/>): "accept" |
    /// "reject" | "unsure", null = nog niet getriaged. Puur een aanbeveling —
    /// GEEN autoriteitspad: alleen een mens wijzigt <see cref="Status"/>
    /// (rechtstreeks of via de bulk-actie, die per item hetzelfde
    /// accept-/reject-pad aanroept). Blijft staan ná accept/reject zodat de
    /// herkomst van de beslissing zichtbaar blijft (#199 eis 4); een
    /// mens-beoordeeld voorstel (Status niet meer "unreviewed") wordt nooit
    /// opnieuw getriaged.</summary>
    public string? Recommendation { get; set; }
    /// <summary>Één zin (Engels — afgeleide kennis, #187) die de aanbeveling
    /// motiveert, met de geraadpleegde refs erin gevouwen (geen aparte kolom
    /// voor refs — het datamodel blijft bewust tot drie nullable velden).</summary>
    public string? RecommendationReason { get; set; }
    public DateTimeOffset? RecommendedAt { get; set; }
}

/// <summary>Community-deck van Piltover Archive (#15, Piltover-first): wij
/// bouwen geen eigen deckbuilder maar spiegelen hun publieke deck-pagina's,
/// met prominente attributie (SourceUrl deep-linkt terug). Fundament voor de
/// meta-laag (kennispiramide-laag 3).</summary>
public class Deck
{
    public long Id { get; set; }
    /// <summary>PA-deck-uuid (uit /decks/view/{uuid}) — de identiteit voor
    /// idempotente her-runs; uniek geïndexeerd.</summary>
    public required string PaId { get; set; }
    public string? Name { get; set; }
    /// <summary>Attributie: de publieke PA-pagina waar dit deck vandaan komt.</summary>
    public required string SourceUrl { get; set; }
    /// <summary>Deck-domeinen (via de legend) — string[] net als card.domains,
    /// zodat facet-queries over kaarten en decks uniform blijven (geen csv).</summary>
    public string[] Domains { get; set; } = [];
    public DateTimeOffset? PaCreatedAt { get; set; }
    /// <summary>PA's updatedAt — samen met de sitemap-lastmod de basis om
    /// alleen echt gewijzigde decks opnieuw op te halen.</summary>
    public DateTimeOffset? PaUpdatedAt { get; set; }
    public int Views { get; set; }
    public int Likes { get; set; }
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Kaartregel binnen een PA-deck. CardCode is het PA-variantnummer
/// ("OGN-126a"); CanonicalRiftboundId is onze canonieke kaart via de
/// variantgroepering (DeckCardLinker), null zolang wij de kaart niet kennen
/// (onbekend is data, geen crash — de ingest telt ze per run).</summary>
public class DeckCard
{
    public long Id { get; set; }
    public long DeckId { get; set; }
    public Deck? Deck { get; set; }
    /// <summary>legend|champions|battlefields|runes|maindeck|sideboard|bench.</summary>
    public required string Section { get; set; }
    public required string CardCode { get; set; }
    public string? CanonicalRiftboundId { get; set; }
    public int Quantity { get; set; }
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

/// <summary>Benchmark-vraag (#158, de scheidsrechter-judge-test): vaste,
/// extern aangeleverde meerkeuzevraag. Seed-import via BenchmarkSeed
/// (Program.cs-startup, idempotent op ExternalKey — zelfde patroon als
/// SourceSeed): ontbrekende sleutels komen erbij zodra Sjoerd een nieuw deel
/// van de set aanlevert, bestaande rijen blijven ongemoeid. CorrectIndex is
/// meestal nog null — de officiële antwoordsleutel volgt in delen; zónder
/// sleutel mag een run de vraag wél stellen en het antwoord tonen, maar
/// NOOIT als correct/fout scoren (zie BenchmarkService/BenchmarkRun).</summary>
public class BenchmarkQuestion
{
    public long Id { get; set; }
    /// <summary>Stabiele idempotentie-sleutel voor de seed (bv. "judge-1") —
    /// zelfde rol als Source.Id.</summary>
    public required string ExternalKey { get; set; }
    public required string Category { get; set; }         // "judge"
    public required string Question { get; set; }
    /// <summary>Geordende opties (A/B/C/…) — de index is de sleutel voor
    /// CorrectIndex en voor de letter die de agent kiest (BenchmarkPrompt).</summary>
    public required string[] Options { get; set; }
    /// <summary>0-based; null = nog geen officiële sleutel.</summary>
    public int? CorrectIndex { get; set; }
    /// <summary>Optionele toelichting/regelbasis-referentie voor bij het bekijken.</summary>
    public string? Explanation { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Eén benchmarkrun (#158): de vaste vragenset door de bestaande
/// /ask-pipeline met de isolatie-vlag aan (AskService.AskOptions.Benchmark) —
/// geen ask_trace/ask_metric-rij, geen agentic-relatie-terugkoppeling (#120).
/// Score/tellingen liggen hier vast bij afronding zodat run-over-run
/// vergelijken geen herberekening nodig heeft.
///
/// Model-sweep (#174, uitbreiding op #158): drie extra, puur additieve
/// velden — allemaal null voor een gewone single-model run via het
/// jobs-paneel (het bestaande "benchmark"-pad blijft ongewijzigd).</summary>
public class BenchmarkRun
{
    public long Id { get; set; }
    /// <summary>Vrij label (model-context e.d.); null bij een gewone
    /// handmatige run via het jobs-paneel.</summary>
    public string? Label { get; set; }
    public int QuestionCount { get; set; }
    /// <summary>Aantal vragen mét officiële sleutel (CorrectIndex != null) op
    /// het moment van deze run — de noemer van ScorePercent.</summary>
    public int KeyedCount { get; set; }
    public int CorrectCount { get; set; }
    /// <summary>% correct over de gekeyde vragen; null zolang geen enkele
    /// vraag een sleutel heeft (nog niets te scoren, wel te bekijken).</summary>
    public double? ScorePercent { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Model-sweep (#174): het rb-ai-modelId dat déze run gebruikte
    /// (bv. "claude-opus-4-8") — meegegeven als AskOptions.Model. Null buiten
    /// een sweep (het standaardmodel van de gewone benchmark-job).</summary>
    public string? Model { get; set; }
    /// <summary>Model-sweep (#174): 1 of 2 — welke van de twee herhalingen
    /// binnen ditzelfde model dit is (de consistentie-check uit issue #174:
    /// scoren de twee runs gelijk, of was de eerste een toevalstreffer?).
    /// Null buiten een sweep.</summary>
    public int? RunIndex { get; set; }
    /// <summary>Model-sweep (#174): groepeert alle (model, run_index)-rijen
    /// van één sweep — alle runs met dezelfde SweepId horen bij dezelfde
    /// vergelijking en delen dezelfde vragenset-snapshot. Gezet op de
    /// UTC-starttijd van de sweep in milliseconden (dubbelt meteen als
    /// sorteerbare "wanneer"-waarde voor het verloop-over-tijd-overzicht).
    /// Null voor een niet-sweep-run.</summary>
    public long? SweepId { get; set; }
}

/// <summary>Antwoord van één vraag binnen een run (#158): het volledige
/// scheidsrechter-antwoord (met de gecommitteerde-keuze-instructie uit
/// BenchmarkPrompt) plus de door de deterministische parser herkende letter.
/// Correct is uitsluitend null wanneer de vraag geen sleutel heeft — een
/// parse-mislukking op een wél gekeyde vraag levert gewoon false op
/// (ChosenIndex null ≠ CorrectIndex), nooit een crash van de run.</summary>
public class BenchmarkResult
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public BenchmarkRun? Run { get; set; }
    public long QuestionId { get; set; }
    public BenchmarkQuestion? Question { get; set; }
    public required string Answer { get; set; }
    /// <summary>0-based; null = de deterministische parser vond geen
    /// eenduidige letter in het antwoord (geen match ⇒ null, geen fout).</summary>
    public int? ChosenIndex { get; set; }
    public bool? Correct { get; set; }
    public int DurationMs { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
