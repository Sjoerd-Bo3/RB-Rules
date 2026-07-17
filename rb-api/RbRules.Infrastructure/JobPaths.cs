namespace RbRules.Infrastructure;

/// <summary>Eén stap in een pad (#190): verwijst naar een bestaande
/// <see cref="JobCatalog"/>-job door naam (gevalideerd — zie
/// JobPathsTests). <paramref name="Drain"/> laat <see cref="PathRunner"/>
/// diezelfde job herhalen tot <see cref="JobOutcome.Drained"/> true is (geen
/// cap meer geraakt) — <paramref name="MaxRepeats"/> is de harde vangrail en
/// de no-progress-guard in PathRunner de zachte, zodat een permanent capte
/// of stilstaande job een pad nooit eindeloos laat doorlopen. Drain hoort
/// alleen op per-run gecapte jobs: een ongecapte job doet zijn hele backlog
/// in één run en zou bij Drain alleen zijn failures herkauwen.</summary>
public sealed record PathStep(string JobName, bool Drain = false, int MaxRepeats = 10);

/// <summary>Een pad: een naam (Kind in run_log/JobLedger, en de padnaam die
/// als "job" verschijnt op /api/admin/status) + geordende stappen.</summary>
public sealed record PathDefinition(string Name, IReadOnlyList<PathStep> Steps);

/// <summary>Catalogus van beheerpaden (#190): geordende JobCatalog-jobs die
/// vanzelf doorstromen — één klik (of scheduler, zie ScanScheduler) runt de
/// hele keten via <see cref="PathRunner"/>, met dezelfde JobRunner-
/// éénjob-gate, dezelfde live-Progress en dezelfde run_log-zichtbaarheid als
/// een losse job. Nieuwe paden zijn puur declaratief: een lijst
/// <see cref="PathStep"/>'s die elk naar een bestaande JobCatalog-naam
/// wijzen — geen wijziging aan JobRunner, de losse jobs of de
/// mining-services zelf nodig.
///
/// De wipe (<c>regenerateknowledge</c>) zit bewust in GEEN pad: dat blijft
/// een expliciete, destructieve Gevarenzone-actie (#187/#190).</summary>
public static class JobPaths
{
    public static PathDefinition? Find(string name) => All.GetValueOrDefault(name);

    public static IReadOnlyList<PathDefinition> AllPaths { get; } = BuildAll();

    private static readonly Dictionary<string, PathDefinition> All =
        AllPaths.ToDictionary(p => p.Name);

    private static List<PathDefinition> BuildAll()
    {
        // Kennis-pad: de LLM-afgeleide kennislaag bijwerken zonder de
        // bron-scan opnieuw te draaien. Elke mining-stap draint (de
        // Phase 2-regeneratieles: claims/clarify hadden meerdere runs nodig
        // om hun cap niet meer te raken). "relationtriage" (#199 v1) staat
        // ná "relations" (er moet iets te triageren zijn) en vóór "graph" —
        // de triage-aanbeveling is geen graph-projectie-input, maar dit is
        // wel de logische plek in de keten (de queue is dan meteen
        // voorgesorteerd zodra deze pad-run klaar is).
        var knowledgeSteps = new PathStep[]
        {
            new("claims", Drain: true),
            new("clarify", Drain: true),
            new("relations", Drain: true),
            new("relationtriage", Drain: true),
            new("graph"),
        };

        return
        [
            // Ingest-pad: nieuwe/gewijzigde bronnen volledig verwerken —
            // scan → classificatie-backfill → changeconsolidatie →
            // mechaniek/claims/clarify-mining (de gecapte miners
            // gedraineerd) → embeddings → graph.
            new PathDefinition("ingest",
            [
                new("scan"),
                // Review-fix #190: classify is ONgecapt — één run verwerkt de
                // hele backlog, dus draineren zou alleen de zojuist gefaalde
                // items futiel herkauwen. Geen Drain; failures komen bij de
                // volgende pad-run of scheduler-tick vanzelf terug.
                new("classify"),
                // Changeconsolidatie (#206): ná classify (ChangeType/Summary
                // moeten al ingevuld zijn om kandidaat-paren te kunnen
                // beoordelen), vóór de kennis-mining hieronder — de volgorde
                // is geen harde afhankelijkheid daarmee, maar wel de
                // logische plek (de feed is dan meteen geconsolideerd zodra
                // deze pad-run klaar is). Ook ongecapt: het aantal
                // ongekoppelde changes binnen het venster is klein, geen
                // Drain nodig (zelfde afweging als classify hierboven).
                new("consolidatechanges"),
                new("mine", Drain: true),
                new("claims", Drain: true),
                new("clarify", Drain: true),
                new("embed"),
                new("graph"),
            ]),
            // Kaart-pad: nieuwe/gewijzigde kaarten door de pijplijn.
            new PathDefinition("card", [new("cards"), new("embed"), new("graph")]),
            // Kennis-pad (los inzetbaar, bv. na handmatige claims-review).
            new PathDefinition("knowledge", knowledgeSteps),
            // Volledige regeneratie: primer + het hele kennis-pad. Bewust
            // GEEN wipe (regenerateknowledge) — dat blijft een losse,
            // expliciete Gevarenzone-actie (issue #190/#187).
            new PathDefinition("full", [new("primer"), .. knowledgeSteps]),
        ];
    }
}
