using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record ClarificationMineResult(int Documents, int NewItems, int Failed, string Message);

/// <summary>#177: FAQ-/clarificatie-artikelen (bv. de Unleashed Rules FAQ)
/// worden door de scan-pipeline geknipt en geëmbed als vaste-lengte-slabs die
/// meerdere losse verduidelijkingen mengen — één embedding over zo'n slab
/// slaat de betekenis plat, dus een gerichte vraag ("Legion = finalize an
/// item on the chain") haalt het chunk niet boven. Deze service mint
/// diezelfde brontekst opnieuw, maar dan via concept-extractie (rb-ai, zelfde
/// prompt+parse+verify-patroon als <see cref="ClaimMiningService"/>/
/// <see cref="MechanicMiningService"/>): elke discrete verduidelijking wordt
/// een eigen <see cref="Correction"/> met een gefocuste embedding (alleen de
/// verduidelijking zelf, niet de hele slab) en een onderwerp-anker
/// (Scope/Ref — mechanic:Legion, rule_section:§, card:naam, concept:…).
///
/// Anders dan de claims-pipeline is de bron hier per definitie officieel (de
/// aanroeper selecteert alleen TrustTier == 1 én <see
/// cref="ClarificationSources.IsMatch"/>): elk item wordt dus meteen
/// <c>verified</c> + geëmbed, geen kandidaat-claim die eerst corroboratie of
/// een officiële toets nodig heeft (#166-autoriteitsmodel — een officiële FAQ
/// ís de officiële regel, net als <see cref="BanErrataSyncService"/> bans/
/// errata al zonder reviewstap uit trust-1 bronnen structureert).
///
/// Idempotent op twee niveaus (#92/#93-patroon): <see
/// cref="Document.ClarifiedAt"/> slaat een document pas over als een eerdere
/// run volledig slaagde (een gedeeltelijke/mislukte poging komt vanzelf
/// terug), en per <b>concept</b> dedupliceert <see cref="StoreAsync"/> op
/// (bron, Scope, Ref) + semantische nabijheid — niet op exacte tekst. Dat is
/// cruciaal: een her-run na een gedeeltelijk mislukt/gecapt document (of na
/// een cosmetische bronwijziging die een nieuwe Document-rij maakt)
/// herverwerkt het HELE document, en de LLM herformuleert een verduidelijking
/// dan net iets anders. Een exacte-tekst-toets zou die parafrase niet
/// herkennen en een tweede geverifieerde ruling opstapelen (zichtbaar in
/// /ask, /rulings en op de mechaniekpagina, zonder reviewqueue). De
/// (Scope, Ref)+embedding-poort (zelfde patroon als
/// <see cref="ClaimMiningService"/>) herkent de parafrase en werkt de
/// bestaande ruling bij in plaats van te dupliceren. Best-effort en gecapt
/// per run; elke faalstap is herleidbaar in run_log (kind "clarify").
///
/// Backfilt bestaande bronnen vanzelf (Sjoerd-eis, #177-vervolg): de
/// bronselectie hierboven heeft geen tijdvenster — elke enabled trust-1 bron
/// die matcht op <see cref="ClarificationSources.IsMatch"/> doet mee, of hij
/// gisteren of jaren geleden is toegevoegd. De al-geïngeste Unleashed Rules
/// FAQ (Document met de Legion-passage staat al in de tabel, ClarifiedAt is
/// null sinds deze kolom nieuw is) komt dus bij de eerstvolgende run gewoon
/// mee — géén apart backfill-commando nodig, exact het patroon van
/// ClaimsMinedAt/ClaimMiningService (die community-documenten van vóór #50
/// evengoed mint). <paramref name="maxItems"/> is het "niet in één keer
/// alles"-venster (#58/#119-stijl): bij veel opgehoopte bronnen stopt een run
/// op de cap met de rest ongemarkeerd, en pakt de volgende run (job "clarify"
/// handmatig, of de nachtelijke <c>ScanScheduler</c>-tick, #122) verder waar
/// deze gebleven is.</summary>
public class ClarificationMiningService(RbRulesDbContext db, RbAiClient ai, EmbeddingService embeddings)
{
    public const string LedgerKind = "clarify";

    /// <summary>Zelfde segmentgrootte/cap als ClaimMiningService: ruim genoeg
    /// voor een volledig FAQ-artikel (de Unleashed-FAQ is ~42 KB, dus 2
    /// segmenten), begrenst de kosten per document.</summary>
    private const int SegmentChars = 12000;
    private const int MaxSegmentsPerDocument = 4;
    private const int ResponseSnippetLength = 400;
    private const string ProvenancePrefix = "clarify-mining:";

    /// <summary>Embedding-afstandspoort voor de concept-dedupe: binnen dit
    /// venster telt een bestaande ruling over hetzelfde onderwerp als "dezelfde
    /// verduidelijking, anders verwoord" en wordt hij bijgewerkt in plaats van
    /// gedupliceerd. Zelfde waarde als
    /// <see cref="ClaimMiningService"/>'s JudgeGateDistance (0.35), maar hier
    /// zonder LLM-natoets: de vergelijking gebeurt binnen een al op
    /// (bron, Scope, Ref) gefilterde verzameling — twee verduidelijkingen over
    /// exact hetzelfde onderwerp die zó dicht bij elkaar liggen, zíjn dezelfde.
    /// Blijkt de poort te grof, dan is een lichte LLM-"zelfde verduidelijking?"-
    /// toets (ClaimJudge-patroon) de volgende stap — bewust nog niet gedaan
    /// (KISS/YAGNI).</summary>
    private const double DedupeGateDistance = 0.35;

    /// <summary>Scheidingsmarkering tussen de verduidelijking en het
    /// (optionele) bronscitaat in <see cref="Correction.Text"/>; gedeeld door
    /// <see cref="BuildText"/> (schrijven) en <see cref="ClarificationOf"/>
    /// (de dedupe-vergelijking, die het citaat bewust buiten de sleutel laat).</summary>
    private const string QuoteMarker = "\n\nCitaat uit de bron: ";

    public async Task<ClarificationMineResult> RunAsync(
        bool force = false, int maxItems = 60,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        maxItems = Math.Clamp(maxItems, 1, 300);

        // In-memory filter (ClarificationSources.IsMatch is puur/geen EF-
        // vertaalbare methode, docs/CONVENTIONS.md): het aantal trust-1
        // bronnen is klein, dus materialiseren eerst is goedkoop.
        var sources = (await db.Sources.AsNoTracking()
                .Where(s => s.Enabled && s.TrustTier == 1)
                .OrderByDescending(s => s.Rank)
                .ToListAsync(ct))
            .Where(s => ClarificationSources.IsMatch(s.Id, s.Url, s.Name))
            .ToList();

        var docs = 0;
        var newItems = 0;
        var failed = 0;
        var processed = 0;
        var budgetHit = false;

        foreach (var src in sources)
        {
            if (budgetHit) break;
            var doc = await db.Documents
                .Where(d => d.SourceId == src.Id)
                .OrderByDescending(d => d.RetrievedAt)
                .FirstOrDefaultAsync(ct);
            if (doc is null || (doc.ClarifiedAt is not null && !force)) continue;

            docs++;
            var extractionComplete = true;
            var srcNew = 0;
            var itemFailures = new List<string>();

            var segments = Segment(doc.Content);
            for (var si = 0; si < segments.Count; si++)
            {
                if (budgetHit) { extractionComplete = false; break; }
                progress?.Invoke(
                    $"{src.Id}: deel {si + 1}/{segments.Count} extraheren ({newItems} nieuw)");

                var raw = await AskSafeAsync(
                    ClarificationMiner.BuildPrompt(src.Name, segments[si]),
                    ClarificationMiner.SystemPrompt, ct);
                if (raw is null)
                {
                    extractionComplete = false;
                    failed++;
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = LedgerKind, Ref = src.Id, Status = "error",
                        Detail = $"deel {si + 1}/{segments.Count}: rb-ai niet beschikbaar — extractie overgeslagen",
                    });
                    continue;
                }
                var extracted = ClarificationMiner.Parse(raw);
                if (extracted is null)
                {
                    extractionComplete = false;
                    failed++;
                    db.RunLogs.Add(new RunLog
                    {
                        Kind = LedgerKind, Ref = src.Id, Status = "error",
                        Detail = $"deel {si + 1}/{segments.Count}: LLM-antwoord onbruikbaar — geen parseerbare concepten. "
                                 + $"Respons (afgekapt): {LlmJson.Snippet(raw, ResponseSnippetLength)}",
                    });
                    continue;
                }

                foreach (var ec in extracted)
                {
                    if (processed >= maxItems)
                    {
                        budgetHit = true;
                        extractionComplete = false;
                        break;
                    }
                    processed++;
                    var (isNewItem, failure) = await StoreAsync(src, ec, ct);
                    if (failure is not null) { failed++; itemFailures.Add(failure); }
                    else if (isNewItem) { newItems++; srcNew++; }
                }
            }

            // Gelijke redenen gegroepeerd tot één regel (claims-patroon,
            // #93): Ollama-uitval raakt doorgaans alle items van een document.
            foreach (var g in itemFailures.GroupBy(r => r))
            {
                db.RunLogs.Add(new RunLog
                {
                    Kind = LedgerKind, Ref = src.Id, Status = "error",
                    Detail = $"{g.Count()} concept(en) niet verwerkt: {g.Key}",
                });
            }

            // #92-patroon: pas markeren wanneer extractie én opslag voor dit
            // document volledig geslaagd zijn (0 items vinden is ook een
            // geldig resultaat). Een mislukte, afgebroken of op de cap
            // gestrande run komt zo vanzelf opnieuw aan de beurt.
            var documentDone = extractionComplete && itemFailures.Count == 0;
            if (documentDone) doc.ClarifiedAt = DateTimeOffset.UtcNow;
            db.RunLogs.Add(new RunLog
            {
                Kind = LedgerKind, Ref = src.Id,
                Status = documentDone ? "ok" : "info",
                Detail = $"{srcNew} nieuwe verduidelijking(en)"
                         + (documentDone ? "" : " (deels — document blijft staan voor een volgende run)"),
            });
            await db.SaveChangesAsync(ct);
        }

        var message =
            $"{docs} document(en) verwerkt: {newItems} nieuwe verduidelijking(en), {failed} mislukt"
            + (failed > 0 ? " (redenen in run_log)" : "")
            + (budgetHit ? $" — cap van {maxItems} bereikt, rest volgt bij de volgende run" : "");
        return new(docs, newItems, failed, message);
    }

    /// <summary>Eén concept opslaan als geverifieerde ruling, met dedupe op
    /// conceptniveau. De sleutel is (Provenance=bron, Scope, Ref) — het citaat
    /// telt bewust NIET mee — plus semantische nabijheid: binnen die al
    /// gefilterde verzameling geldt een genormaliseerd-gelijke óf embedding-
    /// nabije verduidelijking als "dezelfde, anders verwoord" en wordt de
    /// bestaande ruling bijgewerkt (tekst/citaat/embedding vernieuwd) in plaats
    /// van gedupliceerd. Zo is een her-mine (na retry OF na een cosmetische
    /// bronwijziging met een nieuwe Document-rij) idempotent op conceptniveau.
    /// Degradeert bij Ollama-uitval naar de genormaliseerde exacte-tekst-toets:
    /// een re-run dupliceert dan nog steeds niet, maar een écht nieuw concept
    /// wacht op een run met werkende embeddings (nooit een verified ruling
    /// zonder embedding, #100).</summary>
    private async Task<(bool IsNew, string? Failure)> StoreAsync(
        Source src, ExtractedClarification ec, CancellationToken ct)
    {
        var scope = ScopeFor(ec.TopicType);
        var topicRef = ec.TopicRef.Trim();
        var provenance = $"{ProvenancePrefix}{src.Id}";
        var normClarification = ClaimMiner.NormalizeStatement(ec.Clarification);

        // Dedupe-scope: alle clarify-rulings van déze bron voor ditzelfde
        // onderwerp (Scope, Ref). Klein per (bron, onderwerp), dus tracked
        // materialiseren en de afstand in-memory berekenen (geen pgvector-SQL
        // nodig — werkt zo ook in de InMemory-tests, net als de test-seam van
        // ClaimMiningService.CheckOfficialAsync).
        var refLower = topicRef.ToLowerInvariant();
        var siblings = await db.Corrections
            .Where(c => c.Provenance == provenance && c.Scope == scope
                        && c.Ref.ToLower() == refLower)
            .ToListAsync(ct);

        Pgvector.Vector vec;
        try
        {
            // Gefocuste embedding (issue #177): alleen onderwerp + verduide-
            // lijking (zonder citaat), niet de hele slab — zo haalt een
            // gerichte vraag dit item wél boven, en is de embedding meteen de
            // dedupe-maat (quote buiten de sleutel).
            vec = await embeddings.EmbedOneAsync($"{topicRef}\n{ec.Clarification.Trim()}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ollama weg: geen embedding om mee te vergelijken én geen om op te
            // slaan. Val terug op de genormaliseerde exacte-tekst-toets zodat
            // een her-run niet dupliceert; is het concept écht nieuw, dan telt
            // het als faal (document blijft staan voor een volgende run).
            var known = siblings.Any(c => ClaimMiner.NormalizeStatement(ClarificationOf(c.Text)) == normClarification);
            return known ? (false, null) : (false, $"embedding mislukt (Ollama): {ex.Message}");
        }

        // Match: genormaliseerd-gelijk (snelle, deterministische weg) of
        // binnen de embedding-poort (parafrase van dezelfde verduidelijking).
        var match = siblings.FirstOrDefault(
            c => ClaimMiner.NormalizeStatement(ClarificationOf(c.Text)) == normClarification)
            ?? NearestWithin(siblings, vec, DedupeGateDistance);

        if (match is not null)
        {
            // Bijwerken i.p.v. dupliceren: de nieuwste formulering + citaat +
            // embedding winnen. VerifiedAt blijft staan — een verversing is
            // geen nieuwe verificatie en mag de /rulings-recencyvolgorde niet
            // elke nacht omgooien.
            match.Text = BuildText(ec);
            match.Question = QuestionLabelFor(ec);
            match.SourceRef = src.Url;
            match.Embedding = vec;
            match.Status = "verified";
            await db.SaveChangesAsync(ct);
            return (false, null);
        }

        db.Corrections.Add(new Correction
        {
            Scope = scope,
            Ref = topicRef,
            Text = BuildText(ec),
            Question = QuestionLabelFor(ec),
            SourceRef = src.Url,
            Provenance = provenance,
            // Officiële bron ⇒ direct verified (#166-autoriteitsmodel): geen
            // corroboratie/officiële-toets nodig zoals bij community-claims.
            Status = "verified",
            VerifiedAt = DateTimeOffset.UtcNow,
            Embedding = vec,
        });
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    /// <summary>Dichtstbijzijnde sibling binnen de poort (cosine-afstand), of
    /// null. In-memory berekend over een kleine, al op (bron, Scope, Ref)
    /// gefilterde verzameling.</summary>
    private static Correction? NearestWithin(
        List<Correction> siblings, Pgvector.Vector vec, double gate)
    {
        Correction? nearest = null;
        var best = double.MaxValue;
        foreach (var c in siblings)
        {
            if (c.Embedding is null) continue;
            var d = CosineDistance(c.Embedding, vec);
            if (d < best) { best = d; nearest = c; }
        }
        return best <= gate ? nearest : null;
    }

    /// <summary>Cosine-afstand (1 − cosine-similariteit), identiek aan wat
    /// pgvector's CosineDistance in SQL doet — hier in-memory zodat de dedupe
    /// ook zonder Postgres (en in de InMemory-tests) werkt. Een nulvector of
    /// dimensie-mismatch geeft de maximale afstand (nooit een crash).</summary>
    private static double CosineDistance(Pgvector.Vector a, Pgvector.Vector b)
    {
        var x = a.ToArray();
        var y = b.ToArray();
        if (x.Length != y.Length) return 1.0;
        double dot = 0, nx = 0, ny = 0;
        for (var i = 0; i < x.Length; i++)
        {
            dot += (double)x[i] * y[i];
            nx += (double)x[i] * x[i];
            ny += (double)y[i] * y[i];
        }
        return nx == 0 || ny == 0 ? 1.0 : 1.0 - dot / (Math.Sqrt(nx) * Math.Sqrt(ny));
    }

    /// <summary>De verduidelijking uit een opgeslagen <see cref="Correction.
    /// Text"/> terug — het (optionele) bronscitaat na <see cref="QuoteMarker"/>
    /// valt weg, zodat de dedupe alleen op de verduidelijking zelf vergelijkt
    /// (quote niet in de sleutel).</summary>
    private static string ClarificationOf(string text)
    {
        var i = text.IndexOf(QuoteMarker, StringComparison.Ordinal);
        return i < 0 ? text : text[..i];
    }

    /// <summary>topicType → Correction.Scope: "section" wordt het bestaande
    /// opslagformaat "rule_section"; onbekend degradeert naar "concept"
    /// (zelfde veilige-kant-keuze als ClaimMiner.ParseClaims).</summary>
    private static string ScopeFor(string topicType) => topicType switch
    {
        "card" => "card",
        "mechanic" => "mechanic",
        "section" => "rule_section",
        _ => "concept",
    };

    /// <summary>De opgeslagen tekst: de verduidelijking zelf, plus — als het
    /// artikel er een citaat bij gaf — dat citaat zichtbaar aangehaald
    /// (bron + citaat, issue #177), zonder een apart schemaveld nodig te
    /// hebben.</summary>
    private static string BuildText(ExtractedClarification ec) =>
        string.IsNullOrWhiteSpace(ec.Quote)
            ? ec.Clarification
            : $"{ec.Clarification}{QuoteMarker}“{ec.Quote}”";

    /// <summary>Question fungeert hier als kort label (onderwerp, evt. met
    /// §-verwijzing) voor de snippet-weergave in /ask en /rulings — niet als
    /// letterlijke vraag (er is geen natuurlijke vraag bij een FAQ-concept).</summary>
    private static string? QuestionLabelFor(ExtractedClarification ec)
    {
        if (string.IsNullOrWhiteSpace(ec.SectionRef) || ec.TopicType == "section") return ec.TopicRef;
        return $"{ec.TopicRef} (§{ec.SectionRef.Trim()})";
    }

    /// <summary>AskAsync met het scout-timeoutpatroon: een HttpClient-timeout
    /// telt als uitval van één stap, niet als crash van de hele run.</summary>
    private async Task<string?> AskSafeAsync(string prompt, string system, CancellationToken ct)
    {
        try
        {
            return await ai.AskAsync(prompt, system, ct: ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Knipt documenttekst in extractie-delen op een woordgrens
    /// (zelfde implementatie als ClaimMiningService.Segment).</summary>
    private static List<string> Segment(string content)
    {
        var segments = new List<string>();
        var rest = content.Trim();
        while (rest.Length > 0 && segments.Count < MaxSegmentsPerDocument)
        {
            if (rest.Length <= SegmentChars)
            {
                segments.Add(rest);
                break;
            }
            var cut = rest.LastIndexOf(' ', SegmentChars);
            if (cut < SegmentChars / 2) cut = SegmentChars;
            segments.Add(rest[..cut]);
            rest = rest[cut..].TrimStart();
        }
        return segments;
    }
}
