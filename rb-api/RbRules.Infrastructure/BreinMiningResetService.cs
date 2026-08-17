using Microsoft.EntityFrameworkCore;
using RbRules.Domain;

namespace RbRules.Infrastructure;

/// <summary>Hoe ver de reset gaat (#263). Twee expliciete keuzes, geen verborgen
/// modus-vlag — de beheerder kiest ze als twee losse knoppen in de Gevarenzone
/// (zelfde precedent als "benchmark" vs. "benchmarksweep" in
/// <see cref="JobCatalog"/>).</summary>
public enum BreinResetScope
{
    /// <summary>Alleen de interactie-laag (#226): <see cref="Interaction"/> +
    /// <see cref="InteractionCondition"/> + <see cref="InteractionDecision"/> + de
    /// <see cref="Assertion"/>-rijen met <c>FactKind = interaction</c> (het
    /// watermark), plus het lichten van de poort-grafstenen.</summary>
    Interactions,

    /// <summary>Interacties én de canonieke laag eronder (#250):
    /// <see cref="MechanicPredicateAssertion"/> + <see cref="CanonicalEntity"/>
    /// (met hun merge-kandidaten/-beslissingen) — zodat ook de entiteit-/predicaat-
    /// extractie op een schone pool meetbaar is.</summary>
    InteractionsAndEntities,
}

/// <summary>Aantallen per teruggezette categorie (#263). Elke regel is een
/// verwijderde/gelichte rij; <paramref name="MiningRunsKept"/> telt wat we
/// BEWUST hebben laten staan.</summary>
public sealed record BreinMiningResetResult(
    BreinResetScope Scope,
    int Interactions,
    int Conditions,
    int Decisions,
    int Assertions,
    int TombstonesLifted,
    int Predicates,
    int Entities,
    int MergeCandidates,
    int MergeDecisions,
    int MiningRunsKept,
    int Audits = 0)
{
    public string Message
    {
        get
        {
            var parts = new List<string>
            {
                $"{Interactions} interacties, {Conditions} condities, {Decisions} beslissingen, "
                + $"{Assertions} interactie-assertions, {Audits} audit-oordelen verwijderd; "
                + $"{TombstonesLifted} poort-grafsteen(en) gelicht",
            };
            if (Scope == BreinResetScope.InteractionsAndEntities)
            {
                parts.Add(
                    $"{Predicates} mechanic-predicaten, {Entities} canonieke entiteiten, "
                    + $"{MergeCandidates} merge-kandidaten en {MergeDecisions} merge-beslissingen verwijderd");
            }
            parts.Add(
                $"{MiningRunsKept} mining-run(s) BEHOUDEN als provenance-baseline");
            parts.Add(
                "Claims, primer, correcties, relaties, kaarten, regels en bans zijn niet aangeraakt. "
                + "Draai daarna "
                + (Scope == BreinResetScope.InteractionsAndEntities
                    ? "\"breinentiteiten\" → \"breinmine-interacties\" → \"breinmine-predicaten\""
                    : "\"breinmine-interacties\"")
                + " en tot slot \"graph\" + \"breinprojectie\" om Neo4j met de nieuwe stand te laten meelopen.");
            return string.Join(". ", parts);
        }
    }
}

/// <summary>Gerichte reset van ALLEEN de brein-mining-laag (#263) — de smalle
/// tegenhanger van <see cref="KnowledgeRegenerationService"/>, die claims, primer,
/// correcties én relaties wist en daarmee veel te grof is voor dit doel.
///
/// Waarom dit bestaat: het voortgangs-watermark van
/// <see cref="BreinInteractionMiningService"/> is "deze kaart leverde ooit een
/// interactie-<see cref="Assertion"/> op" (<c>FactKind = interaction</c>,
/// <c>DerivedFromRef = card:…</c>). Na de runs van 19–20 juli 2026 stonden ~800
/// kaarten afgevinkt met de extractie die #249 als ondeugdelijk vaststelde (69%
/// kaart↔eigen-keyword). Zonder deze reset slaat de verbeterde miner precies die
/// kaarten over en is de verbetering niet te meten: dezelfde pool moet opnieuw
/// door de nieuwe extractie kunnen.
///
/// <b>Wat WEL weg gaat</b> (per <see cref="BreinResetScope"/>):
/// <list type="bullet">
/// <item><see cref="Interaction"/> — met <see cref="InteractionCondition"/>
/// (FK <c>OnDelete(Cascade)</c>, maar expliciet meegeteld) en
/// <see cref="InteractionDecision"/>: die memo's horen bij een interactie-id dat
/// niet meer bestaat, dus ze achterlaten levert alleen een reviewqueue vol
/// verwijzingen naar niets.</item>
/// <item><see cref="Assertion"/> met <c>FactKind = interaction</c> én de expliciete
/// watermark-velden op <see cref="Card"/> (#249) en <see cref="CanonicalEntity"/>
/// (#286) — SAMEN vormen die het watermark; zie <see cref="ClearWatermarkAsync"/>.</item>
/// <item>Scope <see cref="BreinResetScope.InteractionsAndEntities"/> ook:
/// <see cref="MechanicPredicateAssertion"/>, <see cref="CanonicalEntity"/>,
/// <see cref="MergeCandidate"/> en <see cref="MergeDecision"/>.</item>
/// </list>
///
/// <b>Wat NIET weg gaat</b> (bron, mensenwerk of andere kennislaag): claims,
/// primer-docs, correcties, relaties, kaarten, regels, bans, errata, decks — en
/// nadrukkelijk ook <see cref="CardInteraction"/>, de OUDE lexicale interactie-laag
/// van <c>InteractionService</c> (job "interacties"): die draagt <c>FactKind =
/// card_interaction</c> en staat los van de brein-mining van #226.
///
/// <b>Keuze 1 — <see cref="MiningRun"/>-historie BLIJFT staan.</b> Een MiningRun is
/// de PROV-O-<em>Activity</em>: welk model, welke prompt-versie, welke vocabulaire-
/// snapshot, met welke tellingen. Juist dát is de baseline waartegen de #249-
/// verbetering gemeten wordt ("zelfde pool, nieuwe extractie, vergelijkbare
/// cijfers") — hem mee-wissen zou het meetdoel van deze issue slopen. Het schema
/// zegt hetzelfde: <see cref="Assertion"/> → <see cref="MiningRun"/> staat op
/// <c>DeleteBehavior.Restrict</c> met de motivering "provenance is geen wegwerp-
/// administratie". Na de reset blijven de runs staan als activiteiten wier feiten
/// zijn teruggezet — dat is een eerlijke, leesbare toestand, geen wees-state.
///
/// <b>Keuze 2 — poort-grafstenen worden GELICHT, niet verwijderd.</b> Een
/// <see cref="RejectionTombstone"/> die de OUDE poort schreef, blokkeert precies de
/// dedupe-sleutels die de nieuwe extractie opnieuw moet kunnen aandragen; laten
/// staan zou de reset half maken. Verwijderen mag echter niet (rode draad #236:
/// "nooit een hard-delete: de grafsteen ís het herstelpad"), dus we gebruiken het
/// bestaande, gedocumenteerde herstelpad: <c>Lifted = true</c>. Het audit-spoor
/// blijft compleet. Grafstenen met <c>Actor = "admin"</c> blijven ONGEMOEID — dat
/// is een menselijk oordeel, geen output van de ondeugdelijke extractie.
///
/// <b>Uitvoering</b>: één transactie (CONVENTIONS.md, "transacties rond rebuilds")
/// en achteraf een <see cref="RunLog"/>-regel met de tellingen. Bewust GEEN
/// automatische her-mining erna en bewust geen stap in "alles", een pad of de
/// nachtrun — dit is een expliciete, destructieve beheerdersactie.
///
/// <b>Let op bij uitbreiding</b>: verhuist het watermark ooit naar een expliciet
/// veld op <see cref="Card"/> (of een andere markeringstabel), dan is
/// <see cref="ClearWatermarkAsync"/> de ENIGE plek die mee moet — de rest van deze
/// service kent het watermark niet.</summary>
public class BreinMiningResetService(RbRulesDbContext db)
{
    /// <summary>run_log-soort; de scope staat in het detail.</summary>
    public const string LedgerKind = "breinreset";

    /// <summary>Alleen de poort schrijft grafstenen tijdens mining
    /// (<c>InteractionPromotionService.RejectAsync</c>); "admin" is mensenwerk.</summary>
    private const string GateActor = "gate";

    // Getrackt verwijderen (RemoveRange) i.p.v. ExecuteDeleteAsync — zelfde
    // afweging als KnowledgeRegenerationService: dit is een zeldzame, expliciete
    // beheeractie (geen heet pad), en getrackt houdt de scope-logica een gewone
    // LINQ-query die de InMemory-tests écht uitvoeren, zodat het scope-bewijs de
    // productiecode test en niet een tweede implementatie.
    public async Task<BreinMiningResetResult> ResetAsync(
        BreinResetScope scope, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // ── 1. Het watermark ───────────────────────────────────────────────
        var assertions = await ClearWatermarkAsync(ct);

        // ── 2. De interactie-laag zelf ─────────────────────────────────────
        // Decisions eerst: ze verwijzen naar interaction-ids (ook naar id 0 voor
        // verwerpingen zonder knoop) en horen bij exact deze mining-laag.
        var decisions = await db.InteractionDecisions.ToListAsync(ct);
        db.InteractionDecisions.RemoveRange(decisions);

        // Condities expliciet (naast de cascade-FK) zodat de telling klopt en de
        // InMemory-tests hetzelfde pad lopen als Postgres.
        var conditions = await db.InteractionConditions.ToListAsync(ct);
        db.InteractionConditions.RemoveRange(conditions);

        var interactions = await db.Interactions.ToListAsync(ct);
        db.Interactions.RemoveRange(interactions);

        // Audit-oordelen (#255) horen bij exact deze interactie-laag: een oordeel
        // over een verwijderd feit zou de gemeten precisie voor eeuwig blijven
        // kleuren (en na her-mining zelfs als vals watermark werken). De
        // audit-MiningRuns blijven staan — zelfde keuze 1 als de mining-runs:
        // provenance is geen wegwerp-administratie.
        var audits = await db.InteractionAudits.ToListAsync(ct);
        db.InteractionAudits.RemoveRange(audits);

        // ── 3. Poort-grafstenen lichten (keuze 2 in de klasse-doc) ─────────
        var tombstones = await db.RejectionTombstones
            .Where(t => !t.Lifted && t.Actor == GateActor)
            .ToListAsync(ct);
        foreach (var t in tombstones)
        {
            t.Lifted = true;
            t.LiftedAt = DateTimeOffset.UtcNow;
        }

        // ── 4. Optioneel: de canonieke laag eronder (#250) ─────────────────
        int predicates = 0, entities = 0, mergeCandidates = 0, mergeDecisions = 0;
        if (scope == BreinResetScope.InteractionsAndEntities)
        {
            var predicateRows = await db.MechanicPredicates.ToListAsync(ct);
            db.MechanicPredicates.RemoveRange(predicateRows);
            predicates = predicateRows.Count;

            var candidateRows = await db.MergeCandidates.ToListAsync(ct);
            db.MergeCandidates.RemoveRange(candidateRows);
            mergeCandidates = candidateRows.Count;

            // Merge-beslissingen MOETEN vóór de entiteiten weg: hun FK's staan op
            // Restrict ("audit-spoor is heilig"), dus een entiteit met een
            // beslissing eromheen laat zich niet verwijderen. Dat verlies is de
            // prijs van deze scope en staat daarom expliciet in de telling, het
            // run_log-detail en de bevestigingstekst in de UI — precies zoals
            // KnowledgeRegenerationService het verlies van handmatige correcties
            // expliciet maakt (#187).
            var decisionRows = await db.MergeDecisions.ToListAsync(ct);
            db.MergeDecisions.RemoveRange(decisionRows);
            mergeDecisions = decisionRows.Count;

            var entityRows = await db.CanonicalEntities.ToListAsync(ct);
            // Merge-tombstones wijzen via een self-FK (Restrict) naar hun
            // overlevende entiteit; die verwijzing eerst losknippen, anders hangt
            // de verwijder-volgorde van EF's toevallige sortering af.
            foreach (var e in entityRows.Where(e => e.MergedIntoId is not null))
                e.MergedIntoId = null;
            await db.SaveChangesAsync(ct);

            db.CanonicalEntities.RemoveRange(entityRows);
            entities = entityRows.Count;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Behouden provenance-historie, expliciet geteld zodat de beheerder in het
        // run_log ziet dát de baseline er nog is (keuze 1 in de klasse-doc).
        string[] keptKinds = scope == BreinResetScope.InteractionsAndEntities
            ? [FactKinds.Interaction, FactKinds.Mechanic]
            : [FactKinds.Interaction];
        var runsKept = await db.MiningRuns.AsNoTracking()
            .CountAsync(r => keptKinds.Contains(r.Kind), ct);

        var result = new BreinMiningResetResult(
            scope, interactions.Count, conditions.Count, decisions.Count, assertions,
            tombstones.Count, predicates, entities, mergeCandidates, mergeDecisions, runsKept,
            Audits: audits.Count);

        db.RunLogs.Add(new RunLog
        {
            Kind = LedgerKind,
            Ref = scope == BreinResetScope.InteractionsAndEntities ? "interacties+entiteiten" : "interacties",
            Status = "ok",
            Detail = result.Message,
        });
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Wist ELK voortgangs-watermark van de interactie-mining en geeft terug
    /// hoeveel <see cref="Assertion"/>-rijen daarbij verdwenen.
    ///
    /// De miner leest sinds #249/#286 DRIE markeringen, en de reset is pas correct als
    /// alle drie hier gewist worden — de klasse-doc noemde dit al "het uitbreidpunt",
    /// en dat is precies wat het is:
    /// <list type="number">
    /// <item>de afgeleide markering: een <see cref="Assertion"/> met
    /// <c>FactKind = interaction</c> en <c>DerivedFromRef = card:{id}</c> (de
    /// achtervang van vóór #249);</item>
    /// <item><see cref="Card.InteractionsMinedAt"/> — het expliciete kaart-watermark
    /// (#249);</item>
    /// <item><see cref="CanonicalEntity.InteractionsMinedAt"/> — het expliciete
    /// subject-watermark van de mechanic-niveau-pass (#286).</item>
    /// </list>
    /// Zonder (2) en (3) is de reset half: de assertions verdwijnen, maar de miner slaat
    /// dezelfde kaarten en subjecten alsnog over — en dan is de verbetering waarvoor
    /// deze service bestaat ("zelfde pool, nieuwe extractie") juist niet meetbaar.</summary>
    private async Task<int> ClearWatermarkAsync(CancellationToken ct)
    {
        var watermark = await db.Assertions
            .Where(a => a.FactKind == FactKinds.Interaction)
            .ToListAsync(ct);
        db.Assertions.RemoveRange(watermark);

        var cards = await db.Cards.Where(c => c.InteractionsMinedAt != null).ToListAsync(ct);
        foreach (var c in cards)
        {
            c.InteractionsMinedAt = null;
            c.InteractionsMinedByRunId = null;
        }

        var entities = await db.CanonicalEntities
            .Where(e => e.InteractionsMinedAt != null).ToListAsync(ct);
        foreach (var e in entities)
        {
            e.InteractionsMinedAt = null;
            e.InteractionsMinedByRunId = null;
        }

        return watermark.Count;
    }
}
