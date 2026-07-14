using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

public record ClarificationMineResult(
    int Documents, int Verified, int Pending, int Updated, int Failed, int Retracted, string Message);

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
/// De bron is per definitie officieel (de aanroeper selecteert alleen
/// TrustTier == 1 en <see cref="ClarificationSources.IsMatch"/>), maar
/// auto-verified voor LLM-geparafraseerde tekst is te los (autoriteits-review,
/// #177). Daarom een <b>hybride poort</b>: een concept wordt alleen
/// <c>verified</c> als het BEIDE checks doorstaat -- (1) grounded: het citaat
/// komt echt in de brontekst voor (<see cref="ClarificationGrounding"/>,
/// vangt een gehallucineerd citaat) en (2) anchored: het onderwerp resolvet
/// naar een bestaande knoop (<see cref="ClaimTopicMapper"/>: kaartnaam,
/// mechaniek-vocabulaire, section-code of primer-concept -- vangt een
/// verzonnen/fout anker dat anders stil aan een kaartpagina zou koppelen).
/// Anders ⇒ <c>unverified</c> met een <see cref="Correction.StatusReason"/>
/// ⇒ de bestaande corrections-reviewqueue in, waar de beheerder corrigeert/
/// goedkeurt/afwijst. Een afgewezen (<c>rejected</c>) concept blijft afgewezen:
/// de dedupe heropent een rejected tombstone nooit.
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
/// <b>#185-herkadering:</b> patch notes zijn UIT deze pijplijn gehaald
/// (<see cref="ClarificationSources.IsMatch"/> matcht ze niet meer) — een
/// patch-notes-artikel is een REGELWIJZIGING (delta), geen op-zichzelf-
/// staande ruling, en hoort daarom alleen nog in de wijzigingen-feed via de
/// gewone ingest-diff. Elke run trekt bovendien eerst de al gemínede
/// patch-notes-Corrections van vóór deze scheiding terug
/// (<see cref="RetractPatchNotesCorrectionsAsync"/>), en de hybride poort
/// heeft er een derde toets bij: <see cref="ClarificationInformativeness"/>
/// weert een geëxtraheerd item dat zelf niets meer is dan een kale
/// aankondiging ("X is verduidelijkt/gewijzigd") zonder de regel/definitie/
/// interactie te noemen — de vorm van de #185-bug (een lege Legion-"ruling"
/// uit core-rules-patch-notes).
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

        // #185-opruiming: eerst de al bestaande patch-notes-Corrections
        // terugtrekken (oude #177-heuristiek minede die nog mee) — idempotent,
        // vóór de eigenlijke extractie zodat elke run (handmatig of nachtelijk)
        // dit vanzelf blijft bewaken zonder apart commando.
        var retracted = await RetractPatchNotesCorrectionsAsync(ct);

        // In-memory filter (ClarificationSources.IsMatch is puur/geen EF-
        // vertaalbare methode, docs/CONVENTIONS.md): het aantal trust-1
        // bronnen is klein, dus materialiseren eerst is goedkoop.
        //
        // #185: patch-notes-signaal wint. IsMatch (FAQ-woorden) en
        // IsPatchNotesSignal (patch-notes-woorden) zijn onafhankelijke
        // substring-checks over dezelfde Id/Url/Name — een bron die béíde
        // families bevat (bv. een artikel "Rules FAQ & Patch Notes") zou
        // anders hier gemíned worden én meteen door RetractPatchNotesCorrections-
        // Async weer hard verwijderd, met de ClarifiedAt-gate die her-mining
        // blokkeert ⇒ stil, permanent verlies van geldige rulings (thrash).
        // Daarom sluiten we IsPatchNotesSignal-bronnen hier expliciet uit: een
        // gemengde bron telt als patch-notes (voedt de wijzigingen-feed, niet
        // de rulings-laag) — de veilige, conservatieve keuze; retractie ruimt
        // eventuele oude corrections van zo'n bron eenmalig op, zonder thrash.
        var sources = (await db.Sources.AsNoTracking()
                .Where(s => s.Enabled && s.TrustTier == 1)
                .OrderByDescending(s => s.Rank)
                .ToListAsync(ct))
            .Where(s => ClarificationSources.IsMatch(s.Id, s.Url, s.Name)
                        && !ClarificationSources.IsPatchNotesSignal(s.Id, s.Url, s.Name))
            .ToList();

        // Anker-resolver voor de hybride poort (#177): dezelfde bronnen als de
        // graph-projectie (GraphSyncService) — kaartnamen (incl. varianten →
        // canoniek), het mechaniek-vocabulaire (seed + geaccepteerde keywords,
        // dus "Legion" resolvet ook zonder gemínede kaart), bestaande §-codes
        // en primer-concepten. Eén keer per run gebouwd; puur en getest
        // (ClaimTopicMapper). Onbekend onderwerp ⇒ null ⇒ niet anchored ⇒ review.
        var anchors = await BuildAnchorsAsync(ct);

        var docs = 0;
        var verified = 0;
        var pending = 0;
        var updated = 0;
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
            var srcVerified = 0;
            var srcPending = 0;
            var itemFailures = new List<string>();

            var segments = Segment(doc.Content);
            for (var si = 0; si < segments.Count; si++)
            {
                if (budgetHit) { extractionComplete = false; break; }
                progress?.Invoke(
                    $"{src.Id}: deel {si + 1}/{segments.Count} extraheren ({verified} geverifieerd, {pending} ter review)");

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
                    var (outcome, failure) = await StoreAsync(src, ec, doc.Content, anchors, ct);
                    switch (outcome)
                    {
                        case ClarifyOutcome.NewVerified: verified++; srcVerified++; break;
                        case ClarifyOutcome.NewPending: pending++; srcPending++; break;
                        case ClarifyOutcome.Updated: updated++; break;
                        case ClarifyOutcome.Failed:
                            failed++;
                            itemFailures.Add(failure ?? "onbekende fout");
                            break;
                        // RejectedKept/Skipped: bewust geen teller — de menselijke
                        // afwijzing/al-bekend-status is gerespecteerd, geen ruis.
                    }
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
                Detail = $"{srcVerified} geverifieerd, {srcPending} ter review"
                         + (documentDone ? "" : " (deels — document blijft staan voor een volgende run)"),
            });
            await db.SaveChangesAsync(ct);
        }

        var message =
            (retracted > 0 ? $"{retracted} patch-notes-ruling(en) ingetrokken (#185) · " : "")
            + $"{docs} document(en) verwerkt: {verified} geverifieerd, {pending} ter review, "
            + $"{updated} bijgewerkt, {failed} mislukt"
            + (failed > 0 ? " (redenen in run_log)" : "")
            + (budgetHit ? $" — cap van {maxItems} bereikt, rest volgt bij de volgende run" : "");
        return new(docs, verified, pending, updated, failed, retracted, message);
    }

    /// <summary>#185-opruiming: retracten van clarify-mining-<see
    /// cref="Correction"/>s wier bron een patch-notes-bron is (Provenance
    /// "clarify-mining:{sourceId}" met een <see cref="ClarificationSources.
    /// IsPatchNotesSignal"/>-bron). Vóór #185 matchte patch-notes ook mee in
    /// <see cref="ClarificationSources.IsMatch"/>, waardoor een
    /// aankondigingszin zonder regelinhoud (bv. de lege Legion-"ruling" uit
    /// core-rules-patch-notes) als geverifieerde ruling kon eindigen — die
    /// hoort niet in de rulings-laag, patch notes voeden alleen nog de
    /// wijzigingen-feed (gewone ingest-diff). Hard verwijderen (niet
    /// "rejected" markeren): het is geen menselijke afwijzing van een
    /// specifieke bewering, het is "dit had nooit een ruling-rij moeten zijn"
    /// — een tombstone zou de reviewqueue-telling nodeloos vervuilen.
    ///
    /// Werkt zowel op <c>verified</c> als <c>unverified</c>/pending items
    /// (Sjoerd-eis): geen statusfilter. Idempotent: draait bij elke
    /// "clarify"-run (handmatig of nachtelijk) mee, en is na de eerste
    /// opruiming een goedkope no-op (geen matchende Corrections meer, want
    /// patch-notes-bronnen worden sinds #185 niet meer gemined). Sources
    /// wordt in-memory gejoind (klein aantal trust-1-bronnen, zelfde
    /// afweging als de bronselectie hierboven); ontbreekt de Source-rij zelf
    /// (bv. verwijderd uit het register), dan valt de check terug op de
    /// sourceId uit de Provenance zelf — die draagt bij de patch-notes-seeds
    /// (bv. "core-rules-patch-notes") het signaal al in de Id.</summary>
    private async Task<int> RetractPatchNotesCorrectionsAsync(CancellationToken ct)
    {
        var candidates = await db.Corrections
            .Where(c => c.Provenance != null && c.Provenance.StartsWith(ProvenancePrefix))
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var sources = await db.Sources.AsNoTracking()
            .Select(s => new { s.Id, s.Url, s.Name })
            .ToDictionaryAsync(s => s.Id, ct);

        var toRetract = candidates.Where(c =>
        {
            var sourceId = c.Provenance![ProvenancePrefix.Length..];
            return sources.TryGetValue(sourceId, out var src)
                ? ClarificationSources.IsPatchNotesSignal(src.Id, src.Url, src.Name)
                : ClarificationSources.IsPatchNotesSignal(sourceId, null, null);
        }).ToList();
        if (toRetract.Count == 0) return 0;

        db.Corrections.RemoveRange(toRetract);
        db.RunLogs.Add(new RunLog
        {
            Kind = LedgerKind, Ref = "cleanup-patch-notes", Status = "info",
            Detail = $"{toRetract.Count} patch-notes-clarify-ruling(en) ingetrokken (#185 — "
                     + "patch notes horen alleen nog in de wijzigingen-feed, niet als ruling)",
        });
        await db.SaveChangesAsync(ct);
        return toRetract.Count;
    }

    private enum ClarifyOutcome { NewVerified, NewPending, Updated, RejectedKept, Skipped, Failed }

    /// <summary>Bouwt de anker-resolver uit dezelfde bronnen als de
    /// graph-projectie (GraphSyncService): alle printings (varianten →
    /// canoniek), het volledige mechaniek-vocabulaire (seed + geaccepteerde
    /// keywords ∪ gemínede kaartmechanieken), bestaande §-codes en
    /// primer-concepten. Puur resultaat (ClaimTopicMapper); onbekend onderwerp
    /// resolvet naar null.</summary>
    private async Task<ClaimTopicMapper> BuildAnchorsAsync(CancellationToken ct)
    {
        var cards = await db.Cards.AsNoTracking()
            .Select(c => new { c.RiftboundId, c.Name, c.VariantOf })
            .ToListAsync(ct);
        var accepted = await db.MechanicKeywords.AsNoTracking()
            .Where(k => k.Status == "accepted").Select(k => k.Term).ToListAsync(ct);
        var minedMechanics = (await db.Cards.AsNoTracking()
                .Where(c => c.Mechanics != null).Select(c => c.Mechanics!).ToListAsync(ct))
            .SelectMany(m => m);
        var sections = await db.RuleChunks.AsNoTracking()
            .Where(r => r.SectionCode != null && r.SectionCode != "")
            .Select(r => new { r.SourceId, Code = r.SectionCode! })
            .Distinct().ToListAsync(ct);
        var concepts = (await db.KnowledgeDocs.AsNoTracking()
                .Where(k => k.Kind == "primer")
                .Select(k => new { k.Topic, k.Title })
                .ToListAsync(ct))
            .GroupBy(k => k.Topic).Select(g => g.First());

        return ClaimTopicMapper.Create(
            cards.Select(c => (c.RiftboundId, c.Name, c.VariantOf)),
            MechanicMiner.Vocabulary(accepted).Concat(minedMechanics),
            sections.Select(s => (s.SourceId, s.Code)),
            concepts.Select(k => (k.Topic, k.Title)));
    }

    /// <summary>Eén concept opslaan met de hybride poort (#177, #185) én
    /// dedupe op conceptniveau. Poort: grounded (citaat in de bron) EN
    /// anchored (onderwerp resolvet) EN informative (geen kale
    /// aankondigingszin, #185) ⇒ verified; anders unverified met
    /// StatusReason (de reviewqueue in). Dedupe-sleutel: (Provenance=bron,
    /// Scope, Ref) -- het citaat telt bewust NIET mee -- plus semantische
    /// nabijheid: een genormaliseerd-gelijke óf embedding-nabije
    /// verduidelijking geldt als dezelfde en wordt bijgewerkt (nooit
    /// gedegradeerd; een rejected tombstone wordt nooit heropend) in plaats
    /// van gedupliceerd. Zo is een her-mine (na retry OF na een cosmetische
    /// bronwijziging met een nieuwe Document-rij) idempotent op
    /// conceptniveau. Degradeert bij Ollama-uitval naar de genormaliseerde
    /// exacte-tekst-toets: een re-run dupliceert dan nog steeds niet, maar
    /// een écht nieuw concept wacht op een run met werkende embeddings (nooit
    /// een ruling zonder embedding, #100).</summary>
    private async Task<(ClarifyOutcome Outcome, string? Failure)> StoreAsync(
        Source src, ExtractedClarification ec, string docContent,
        ClaimTopicMapper anchors, CancellationToken ct)
    {
        var scope = ScopeFor(ec.TopicType);
        var topicRef = ec.TopicRef.Trim();
        var provenance = $"{ProvenancePrefix}{src.Id}";
        var normClarification = ClaimMiner.NormalizeStatement(ec.Clarification);

        // Hybride poort (#177, #185): grounded (citaat écht in de bron) EN
        // anchored (onderwerp resolvet naar een bestaande knoop) EN
        // informative (geen kale "X is verduidelijkt/gewijzigd"-aankondiging
        // zonder regelinhoud — de #185-bug) ⇒ verified; anders pending met
        // reden, de reviewqueue in. Een niet-informatief item gaat naar
        // review in plaats van stil te worden overgeslagen: net als bij
        // grounding/anchoring kan de heuristiek een keer mis zitten, en de
        // beheerder heeft dan alsnog het laatste woord (zelfde uniforme
        // poort-semantiek, geen aparte "skip"-tak).
        var grounded = ClarificationGrounding.IsGrounded(ec.Quote, docContent);
        var anchored = anchors.Resolve(ec.TopicType, topicRef) is not null;
        var informative = !ClarificationInformativeness.IsMetaOnly(ec.Clarification);
        var verifies = grounded && anchored && informative;
        var reason = verifies ? null : GateReason(grounded, anchored, informative, ec);

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
            // dedupe-maat (quote buiten de sleutel). Ook een pending item krijgt
            // een embedding: dat lekt niet in /ask (retrieval filtert op
            // Status=verified) maar houdt de dedupe over runs heen robuust.
            vec = await embeddings.EmbedOneAsync($"{topicRef}\n{ec.Clarification.Trim()}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ollama weg: geen embedding om mee te vergelijken én geen om op te
            // slaan. Val terug op de genormaliseerde exacte-tekst-toets zodat
            // een her-run niet dupliceert; is het concept écht nieuw, dan telt
            // het als faal (document blijft staan voor een volgende run).
            var known = siblings.Any(c => ClaimMiner.NormalizeStatement(ClarificationOf(c.Text)) == normClarification);
            return known ? (ClarifyOutcome.Skipped, null) : (ClarifyOutcome.Failed, $"embedding mislukt (Ollama): {ex.Message}");
        }

        // Match: genormaliseerd-gelijk (snelle, deterministische weg) of
        // binnen de embedding-poort (parafrase van dezelfde verduidelijking).
        var match = siblings.FirstOrDefault(
            c => ClaimMiner.NormalizeStatement(ClarificationOf(c.Text)) == normClarification)
            ?? NearestWithin(siblings, vec, DedupeGateDistance);

        if (match is not null)
        {
            // Afgewezen blijft afgewezen (#177): een beheerder-afwijzing
            // (rejected tombstone) mag een volgende run niet heropenen.
            if (match.Status == "rejected") return (ClarifyOutcome.RejectedKept, null);

            // Bijwerken i.p.v. dupliceren: de nieuwste formulering + citaat +
            // embedding winnen. Nooit degraderen: een al geverifieerde ruling
            // blijft verified, ook als een flaky her-extractie de poort niet
            // haalt; een pending item upgradet zodra een latere run grounded +
            // anchored is.
            match.Text = BuildText(ec);
            match.Question = QuestionLabelFor(ec);
            match.SourceRef = src.Url;
            match.Embedding = vec;
            if (match.Status == "verified" || verifies)
            {
                match.Status = "verified";
                match.StatusReason = null;
                match.VerifiedAt ??= DateTimeOffset.UtcNow;
            }
            else
            {
                match.Status = "unverified";
                match.StatusReason = reason;
                match.VerifiedAt = null;
            }
            await db.SaveChangesAsync(ct);
            return (ClarifyOutcome.Updated, null);
        }

        db.Corrections.Add(new Correction
        {
            Scope = scope,
            Ref = topicRef,
            Text = BuildText(ec),
            Question = QuestionLabelFor(ec),
            SourceRef = src.Url,
            Provenance = provenance,
            Status = verifies ? "verified" : "unverified",
            StatusReason = reason,
            VerifiedAt = verifies ? DateTimeOffset.UtcNow : null,
            Embedding = vec,
        });
        await db.SaveChangesAsync(ct);
        return (verifies ? ClarifyOutcome.NewVerified : ClarifyOutcome.NewPending, null);
    }

    /// <summary>Leesbare reden dat de hybride poort een item pending laat, voor
    /// de reviewqueue (StatusReason). Combineert alle faalredenen als er
    /// meerdere tegelijk gelden.</summary>
    private static string GateReason(bool grounded, bool anchored, bool informative, ExtractedClarification ec)
    {
        var parts = new List<string>();
        if (!grounded)
            parts.Add(string.IsNullOrWhiteSpace(ec.Quote)
                ? "geen citaat om te verifiëren"
                : "citaat niet terug te vinden in de bron");
        if (!anchored)
            parts.Add($"onderwerp '{ec.TopicRef.Trim()}' ({ec.TopicType}) niet herkend");
        if (!informative)
            parts.Add("verduidelijking is een aankondiging zonder regelinhoud (#185)");
        return string.Join("; ", parts);
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
