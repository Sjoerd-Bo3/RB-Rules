import {
  createSdkMcpServer,
  query,
  tool,
  type Options,
} from "@anthropic-ai/claude-agent-sdk";
import {
  BRAIN_SERVER_NAME,
  BRAIN_TOOLS,
  brainToolAllowlist,
  compactJson,
  createBrainSession,
} from "./brain-tools.js";
import { aiSemaphore, AI_QUEUE_WAIT_MS, ConcurrencyLimitError } from "./concurrency.js";
import {
  AiRunError,
  describeThrown,
  failureOf,
  logEvent,
  RetryTracker,
  safeDetail,
  StderrTail,
  stderrDigestLine,
  withRetries,
  withStderrDigest,
  type AiFailure,
} from "./failure.js";
import { z } from "zod";
import {
  claudeRateLimitSignal,
  ClaudeAgentToolProvider,
  mergeClaudeMessageFailure,
  type ClaudeRateLimitSignal,
  type QueryRunner as ClaudeQueryRunner,
} from "./providers/claude-agent.js";
import {
  ClaudeAccountPoolProvider,
  getFallbackClaudeAccountRouter,
  isClaudeAccountLocalFailure,
  type ClaudeAccountEnvironment,
  type ClaudeAccountLease,
  type ClaudeAccountRouter,
} from "./providers/claude-accounts.js";
import { CodexAccountPoolProvider } from "./providers/codex.js";
import { ProviderRegistry } from "./providers/registry.js";
import type {
  ModelAlias,
  ProviderDiagnostics,
  ProviderUsage,
  ResolvedModel,
} from "./providers/types.js";
import { RELATIONS_MARKER } from "./relations.js";
import { runtimeManager, runtimeProviderRegistry } from "./control/runtime.js";
import { usageFromSdk, type AskUsage } from "./usage.js";
import {
  pushableInput,
  WarmPool,
  type WarmBootHandle,
  type WarmSignature,
} from "./warmpool.js";

// Claude-auth wordt per geïsoleerd account ontdekt in claude-accounts.ts.
// Een slot bevat óf CLAUDE_CODE_OAUTH_TOKEN óf ANTHROPIC_API_KEY; genummerde
// niet-lege slots zijn leidend en een ongenummerde credential is de fallback.
export type Task = "cheap" | "hard" | "research" | "agentic";

export interface AskImage {
  mediaType: string; // image/jpeg | image/png | image/webp | image/gif
  data: string; // base64
}

const MODEL: Record<Task, string> = {
  cheap: "claude-sonnet-4-6",
  hard: "claude-opus-4-8",
  research: "claude-sonnet-4-6", // web-werk is zoek+samenvat: Sonnet volstaat (kosten, #42)
  agentic: "claude-sonnet-4-6", // brein-loop is 2–8 beurten: Sonnet houdt kosten/latency binnen budget (BRAIN.md §2.4)
};

// Websearch is opt-in per taak (#64): alléén "research" krijgt WebSearch/
// WebFetch; alle andere taken draaien expliciet zonder built-in tools
// (`tools: []`), zodat web-toegang nooit stilzwijgend aan gaat
// (kosten/latency-bewaking, #42).
const RESEARCH_TOOLS = ["WebSearch", "WebFetch"];

// Research doet meerdere zoek/fetch-rondes; de andere taken blijven één
// prompt→antwoord-beurt.
const RESEARCH_MAX_TURNS = 16;

// Ruime maar harde grens: een research-call die hierna nog loopt wordt via
// AbortController afgebroken. De fout bubbelt naar server.ts → 500 → de
// aanroeper (RbAiClient) degradeert tot null. De sidecar crasht nooit.
const RESEARCH_TIMEOUT_MS = 300_000; // 5 minuten

// Resultaatcontract voor task="research" (#64; #63's bronnenjacht bouwt hierop):
// - Het antwoord eindigt ALTIJD met een sectie "Bronnen:" met per regel één
//   volledige URL (https://…) van een daadwerkelijk geraadpleegde pagina.
// - Beweringen zonder gevonden bron worden expliciet als onbevestigd benoemd.
// - Webresultaten zijn per definitie community-/meta-laag (docs/KNOWLEDGE.md):
//   de aanroeper mag ze nooit als officiële laag behandelen.
// Dit blok wordt server-side ACHTER de eventuele system-prompt van de
// aanroeper geplakt, zodat bronvermelding afgedwongen blijft ongeacht wat de
// aanroeper meestuurt.
const RESEARCH_CONTRACT = `Je hebt toegang tot WebSearch en WebFetch. Regels voor je antwoord:
- Baseer elke bewering op daadwerkelijk geraadpleegde webpagina's; verzin geen bronnen of URL's.
- Sluit je antwoord ALTIJD af met een sectie die begint met "Bronnen:" gevolgd door per regel één volledige URL (https://...) van een geraadpleegde pagina, waar mogelijk met publicatie- of raadpleegdatum erachter.
- Vind je voor (een deel van) de vraag geen bruikbare bron, zeg dat dan expliciet in plaats van te gokken; de sectie "Bronnen:" blijft ook dan verplicht (eventueel met alleen de wel-geraadpleegde URL's).
- Sommige sites weigeren datacenter-verkeer; meld het kort als een relevante pagina niet op te halen was.`;

// Agentic (#106, docs/BRAIN.md §2.4): de agent redeneert zelf over het brein
// via zes in-process MCP-tools die 1-op-1 op de brein-API mappen (§2.3,
// brain-tools.ts; HTTP naar rb-api via RB_API_URL). Alléén die tools staan in
// de allowlist — géén web, géén bash. Dubbele rem op kosten en latency:
// maxTurns + harde timeout hier, en een tool-call-cap in de sessie zelf.
const AGENTIC_MAX_TURNS = 8; // §2.4: "maxTurns ~8"
const AGENTIC_TIMEOUT_MS = 120_000; // §2.4: harde grens 90–120s; zelfde AbortController-patroon als research
const AGENTIC_MAX_TOOL_CALLS = 12; // §2.4: "cap ~12 calls"
const AGENTIC_HTTP_TIMEOUT_MS = 10_000; // per brein-call: een hangende rb-api mag de harde timeout niet in z'n eentje opsouperen

// Gedragscontract voor task="agentic". Net als RESEARCH_CONTRACT wordt dit
// blok server-side ACHTER de system-prompt van de aanroeper geplakt, zodat de
// voorrangregels van de kennispiramide (docs/KNOWLEDGE.md, BRAIN.md §2.4)
// gelden ongeacht wat de aanroeper meestuurt.
const AGENT_ADDENDUM = `Je hebt zes brein-tools (semantic_search, get_node, neighbors, path, evidence, contradictions) die de Riftbound-kennisbank bevragen. Regels:
- De kennispiramide blijft gelden: officieel > geverifieerde rulings > primer > community > meta. Weeg elk toolresultaat op zijn laag- en trust-label; een community-claim wint nooit van een officiële regel of geverifieerde ruling.
- Weerlegde of vervangen claims (via contradictions) zijn geen kennis — noem ze hooguit expliciet als weerlegd.
- Een toolresultaat "brein niet beschikbaar" of "niet gevonden" is informatie, geen fout: redeneer verder met wat je al hebt en verzin nooit toolresultaten, refs of citaten.
- Wees zuinig met tool-calls (er is een harde limiet): stop met zoeken zodra je genoeg weet en geef dan je eindantwoord in het gevraagde format, met §-verwijzingen waar je die gevonden hebt.
- Ontdek je tijdens het redeneren een verband tussen twee brein-knopen dat het brein nog niet als relatie kent, meld dat dan NA je volledige eindantwoord: één regel met exact "${RELATIONS_MARKER}" gevolgd door JSON in de vorm {"relations": [{"from": "<ref>", "to": "<ref>", "kind": "...", "explanation": "..."}]}. Alleen refs die letterlijk in je toolresultaten voorkwamen — verzin er geen. kind is kort, herbruikbaar en in kleine letters (bv. counters, enables, versterkt, wordt beperkt door, vereist, verduidelijkt); explanation is 1-2 zinnen in het Engels (#187: afgeleide kennis in de brontaal, dicht bij de officiële bewoording) die het verband onderbouwen vanuit je toolresultaten. Maximaal 5 sterke voorstellen; geen open deuren die de graph al kent. Niets ontdekt? Laat marker en blok dan helemaal weg.`;

/** Meld één brein-tool-aanroep langs de twee kanalen die er zijn (#292).
 *
 * DE SPLITSING IS DE HELE POINTE. Tot #292 ging dezelfde regel — toolnaam PLUS
 * argumenten — naar allebei, en bij `semantic_search` zijn die argumenten in de
 * praktijk de VRAAGTEKST VAN DE GEBRUIKER. Die belandde onbewerkt in
 * `docker logs rb-v2-ai`, een kanaal dat veel losser wordt behandeld dan de
 * plek waar de vraag bewust wél staat (`ask_trace`, achter de admin-poort).
 * Door `safeDetail` halen lost dat NIET op: die haalt secrets weg, geen
 * gebruikersinvoer. De enige echte oplossing is de inhoud niet meegeven.
 *
 *  - `onStep` (→ server.ts `steps` → `AskTrace.BrainSteps`): de VOLLEDIGE stap,
 *    ongewijzigd. Daar hoort hij thuis en daar verandert niets aan; het
 *    verificatiepad van #106/#107 blijft intact.
 *  - stdout: alleen de toolnaam (gesloten verzameling, {@link BRAIN_TOOLS}) en
 *    de MAAT van de argumenten. Genoeg om te zien dát en hoe vaak de agent het
 *    brein bevroeg — de vraag "welke tools riep hij aan, in welke volgorde" is
 *    beantwoordbaar zonder één teken invoer te loggen.
 *
 * Geëxporteerd omdat dit de plek is waar de privacy-beslissing valt: zo is ze
 * op GEDRAG te toetsen (schrijft de logregel echt geen argument-inhoud?) in
 * plaats van met een grep op de broncode. */
export function noteBrainStep(
  toolName: string,
  args: Record<string, unknown>,
  onStep?: (step: string) => void,
): string {
  const rendered = compactJson(args);
  const step = `${toolName} ${rendered.slice(0, 200)}`;
  onStep?.(step);
  logEvent("brain_step", { tool: toolName, bytes: Buffer.byteLength(rendered, "utf8") });
  return step;
}

/** In-process MCP-server met de zes brein-tools (§2.4, createSdkMcpServer).
 * Per aanroep een verse sessie zodat de tool-call-cap per vraag telt. Stappen
 * gaan via {@link noteBrainStep} naar de aanroeper (`steps` →
 * `AskTrace.BrainSteps`, deelissue 4 #107) en inhoudsloos naar de containerlog.
 * Exported zodat de MCP-laag ook zonder LLM-call te smoken/testen is. */
export function createBrainMcpServer(onStep?: (step: string) => void) {
  const session = createBrainSession({
    baseUrl: process.env.RB_API_URL,
    timeoutMs: AGENTIC_HTTP_TIMEOUT_MS,
    maxCalls: AGENTIC_MAX_TOOL_CALLS,
  });
  return createSdkMcpServer({
    name: BRAIN_SERVER_NAME,
    version: "1.0.0",
    tools: BRAIN_TOOLS.map((t) =>
      tool(t.name, t.description, t.schema, async (args) => {
        const a = args as Record<string, unknown>;
        noteBrainStep(t.name, a, onStep);
        // session.run gooit nooit: fouten (rb-api plat, timeout, cap) komen
        // als leesbaar toolresultaat terug — fouten zijn data (§2.3).
        return { content: [{ type: "text" as const, text: await session.run(t.name, a) }] };
      }),
    ),
  });
}

/** Harde grens per extractie-run. Verstelbaar via `AI_EXTRACT_TIMEOUT_MS`, met
 * de bestaande 90 s als default — dus zonder env-wijziging verandert er niets.
 *
 * EERLIJKE MOTIVERING (#281): ophogen is een PLEISTER, geen oplossing. Een
 * productie-experiment liet zien dat de duur van een extractie meeschaalt met
 * het AANTAL AANGEBODEN REFS — 3 refs → 200 na 49,0 s, 39 refs → 500 na 92,1 s.
 * Het vocabulaire dat we per kaart meesturen groeit met elke set die de
 * kennisbank leert, dus dit is een SCHAALKLIP: hoe meer het brein weet, hoe
 * groter de kans dat een extractie omvalt. De timeout verhogen verschuift die
 * klip alleen — bij 60 refs ligt hij er weer. De echte oplossing (minder refs
 * per aanroep aanbieden, of op mechanic-niveau vragen in plaats van per kaart)
 * staat in #288 en hoort NIET in de meet-PR van #281.
 *
 * De knop staat er dus als ops-noodrem, niet als fix. Let op: hem daadwerkelijk
 * gebruiken vraagt óók een regel in de compose-`environment:` van `rb-v2-ai`
 * (#268-valkuil: een variabele in de VM-`.env` is een substitutiebron voor de
 * compose-file, géén container-env). */
const EXTRACT_TIMEOUT_MS = (() => {
  const parsed = Number.parseInt(process.env.AI_EXTRACT_TIMEOUT_MS ?? "", 10);
  return Number.isFinite(parsed) && parsed >= 1_000 ? parsed : 90_000;
})();

/** De SDK-aanroep als injecteerbare functie (test-seam, zie
 * {@link extractWithTool}). Structureel getypeerd op wat wij ervan gebruiken:
 * prompt + options erin, een berichtenstroom eruit. */
export type QueryRunner = ClaudeQueryRunner;

/** Production registry for the stateless extract/audit path. `/ask` stays on Claude. */
export const providerRegistry = runtimeProviderRegistry;

/** Dezelfde naad voor het /ask-pad (#300), met de bredere prompt-vorm die
 * {@link askClaude} gebruikt: een kale string, of een streaming-input-iterator
 * zodra er afbeeldingen meegaan.
 *
 * Waarom askClaude er nu ook een krijgt: zonder naad valt over het /ask-faalpad
 * alleen te toetsen DAT de juiste tekens in de broncode staan, en zo'n bron-grep
 * vangt zijn eigen bug niet — dat is de les die #281 hier al duur betaalde. Met
 * de naad kan een test een subprocess simuleren dat naar stderr schrijft en dan
 * omvalt, en toetsen wat er daadwerkelijk in de uitval-toelichting terechtkomt. */
export type AskQueryRunner = (arg: {
  prompt: string | AsyncIterable<unknown>;
  options: Options;
}) => AsyncIterable<unknown>;

/** Uitkomst van één {@link extractWithTool}-run (#281). `items` houdt het
 * bestaande contract: de gevangen array (mogelijk leeg) of `null` bij uitval.
 * `failure` is nieuw en staat er ALLEEN bij `items === null`: het zegt WAAROM
 * er niets kwam, zodat server.ts het kan loggen én in de foutbody kan
 * meesturen. Vóór #281 was dit onderscheid nergens: elke mislukking — timeout,
 * max beurten, API-fout, een model dat de tool gewoon niet riep — kwam als
 * dezelfde kale `null` naar buiten en daarna als hetzelfde kale
 * `500 {"error":"extractie mislukt"}`. */
export interface ExtractOutcome {
  items: unknown[] | null;
  failure?: AiFailure;
  provider?: string;
  model?: string;
  usage?: ProviderUsage | null;
  /** Is de run door ONZE harde timeout afgekapt (#281)? Apart van
   * `failure.reason`, want `withRetries` mag die reden overschrijven met de
   * upstream-oorzaak (een timeout ná zeven mislukte API-pogingen is een
   * API-fout). Op HTTP-niveau blijft het feit "wij hebben afgekapt" staan, en
   * dáár hangt de 504 aan — zodat rb-api een timeout als `timeout` telt en niet
   * als generieke 5xx. Precies de samenval die #281 blind maakte: drie
   * verschillende oorzaken (tool niet geroepen, timeout, echte fout) kwamen
   * allemaal als dezelfde kale 500 naar buiten. */
  timedOut?: boolean;
}

/** De afloop van één extractie-run, als PURE functie (#281-review).
 *
 * Waarom dit geen inline-branches meer zijn: de eerste versie besliste dit op
 * twee plaatsen — in het catch-blok én na de leeslus — en die twee liepen
 * uiteen. Alleen het catch-pad woog `timedOut`, dus een afgebroken run die
 * NIET gooide (de iterator loopt leeg, of geeft nog een fout-result af) kwam er
 * als `no_tool_call` + 500 uit. Dat is niet alleen fout maar actief misleidend:
 * het stuurt de beheerder naar de prompt terwijl het de tijdslimiet was. En het
 * sprak de kernbevinding van deze PR tegen — de Agent SDK gooit juist NIET
 * noodzakelijk bij een mislukte run.
 *
 * Eén functie voor beide paden maakt die drift per constructie onmogelijk, en
 * maakt de beslissing bovendien uitputtend testbaar zonder SDK.
 *
 * Volgorde is betekenisvol: een geldige vangst wint van elke faalreden (weggooien
 * zou goed werk vernietigen), en de afkapping wint van wat de run verder ook
 * meldde (wij hakten af — dat is wat er gebeurde). */
export function decideExtractOutcome(input: {
  captured: unknown[] | null;
  timedOut: boolean;
  aborted: boolean;
  runFailure?: AiFailure;
  toolName: string;
  timeoutMs: number;
}): ExtractOutcome {
  const { captured, timedOut, aborted, runFailure, toolName, timeoutMs } = input;
  const seconds = timeoutMs / 1000;

  if (captured !== null) {
    // De tool vuurde: de kandidaten zijn geldig en gaan mee. Werd de run dáárna
    // afgekapt, dan is het een PARTIËLE vangst — de meting moet dat zien, anders
    // vallen juist de traagste kaarten uit de schaalklip-meting weg.
    return timedOut
      ? {
          items: captured,
          timedOut: true,
          failure: {
            reason: "timeout",
            detail:
              `tool vuurde nog vóór de tijdslimiet van ${seconds}s; ` +
              "resultaat behouden, run daarna afgekapt",
          },
        }
      : { items: captured };
  }
  if (timedOut) {
    return {
      items: null,
      timedOut: true,
      failure: {
        reason: "timeout",
        detail: `extractie afgebroken na ${seconds}s (harde timeout)`,
      },
    };
  }
  if (aborted) {
    return {
      items: null,
      failure: { reason: "aborted", detail: "client heeft de verbinding gesloten" },
    };
  }
  // Geen afkapping: de run liep echt af. Meldde de SDK een eigen fout, dan is
  // dát de oorzaak; anders koos het model ervoor de tool niet te roepen — een
  // prompt-/schemaprobleem, een andere knop dan een machineprobleem.
  return {
    items: null,
    failure: runFailure ?? {
      reason: "no_tool_call",
      detail: `run afgerond zonder aanroep van ${toolName}`,
    },
  };
}

/** Tool-forced brein-extractie (#226, docs/ARCHITECTURE brein-epic §3.1). Draait
 * één geforceerde in-process MCP-tool (createSdkMcpServer/tool, zelfde mechaniek als
 * de agentic brein-tools). Sinds #312 is de tool-VORM een constante (extract.ts):
 * het vocabulaire reist als prompt-invoer mee en de gesloten-vraag-regel wordt in
 * server.ts deterministisch nagerekend (`enforce*Vocabulary`) — het schema is dus
 * niet langer de poort. De tool-handler VANGT de argumenten in een closure en geeft
 * een ack terug; de daadwerkelijke kandidaten reizen dus niet via de antwoordtekst
 * maar via de tool-input. Puur SDK-gedreven en dus, net als askClaude, niet los
 * unit-getest; de vocabulaire-poort in extract.ts en de faalvertaling in failure.ts
 * zijn dat wél.
 *
 * BEWUST GEEN warme-pool-claim op dit pad (#312, gemeten). De boot-kost die een
 * warme sessie voorbetaalt (subprocess-spawn + MCP-registratie, meetbaar als
 * query()→system/init en auth-onafhankelijk) is 0,41-0,46 s lokaal (mediaan
 * 0,43 s over 10 runs, M-serie); geëxtrapoleerd naar de VM-CPU is dat orde
 * 1-2,5 s tegen een gemiddelde kaart-generatie van 147-163 s (validatieruns;
 * het plafond staat via de VM-env inmiddels op 240 s) — terwijl één extra idle
 * warme sessie 300-400 MiB RSS vasthoudt op de VM waar de OOM-killer al
 * llama-server schoot (#282/#293). De generieke tool-vorm is de eigenlijke winst: het request-prefix
 * (tools + system) is nu byte-stabiel over een mining-run, dus prompt-cache-baar,
 * én de blokkade voor een latere warme aansluiting is weg. Sluit die pas aan
 * wanneer een PRODUCTIE-meting van de boot-kost (≥ enkele seconden) het
 * geheugenoffer rechtvaardigt; de guard-test in ai.test.ts ("extract-pad raakt de
 * warme pool niet aan") is dan de plek die bewust rood hoort te gaan, en een
 * claim mag er alléén komen voor task="cheap" — het audit-pad (task "hard",
 * #255) zou anders stil op een warme cheap-sessie (MODEL.cheap) draaien met
 * valse provenance.
 *
 * Wat de tool vóór een uitval al ving blijft geldig (dan is er gewoon een
 * resultaat); anders komt er `null` mét een {@link AiFailure} terug. */
export async function extractWithTool(opts: {
  toolName: string;
  description: string;
  schema: Parameters<typeof tool>[2];
  resultKey: string;
  system?: string;
  addendum: string;
  text: string;
  signal?: AbortSignal;
  /** Modelkeuze via de bestaande taak-typering (#255): default "cheap" — het
   * bestaande gedrag van de bulk-extractie. De steekproef-audit zet "hard"
   * (MODEL.hard): een sterker model dat de cheap-output beoordeelt is de hele
   * pointe van de audit, en de bestaande Task→model-mapping is er al — géén
   * nieuw model-config-mechanisme. */
  task?: Task;
  /** Closed model alias. Unknown/free model ids never reach a provider. */
  model?: ModelAlias;
  /** Registry seam for provider contract tests. */
  registry?: ProviderRegistry;
  /** Test-seam (#281-review): de SDK-aanroep zelf. Productie laat dit weg en
   * krijgt `query`; een test levert een eigen berichtenstroom en kan zo de
   * faal- en timeout-paden ECHT doorlopen. Zonder deze naad viel er over dit
   * pad alleen te toetsen dát de juiste tekens in de broncode staan — en zo'n
   * bron-grep vangt zijn eigen bug niet: het weghalen van één `timedOut = true`
   * liet alle tests groen terwijl elke timeout weer als generieke 500 naar
   * buiten kwam. Zelfde patroon als `RbAiClient.RetryDelay` in rb-api. */
  runQuery?: QueryRunner;
}): Promise<ExtractOutcome> {
  const {
    toolName, description, schema, resultKey, system, addendum, text, signal,
    task = "cheap",
    model = task === "hard" ? "opus" : "sonnet",
    registry = providerRegistry,
    runQuery,
  } = opts;
  const resolved: ResolvedModel = runQuery
    ? {
        alias: model,
        provider: new ClaudeAgentToolProvider(runQuery),
        providerId: "claude-agent-sdk",
        modelId: model === "opus"
          ? MODEL.hard
          : model === "fable"
            ? "claude-fable-5"
            : MODEL.cheap,
      }
    : registry.resolve(model);
  let captured: unknown[] | null = null;
  const controller = new AbortController();
  let timedOut = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const onAbort = () => controller.abort();
  if (signal?.aborted) controller.abort();
  else signal?.addEventListener("abort", onAbort, { once: true });

  const systemPrompt = [system, addendum].filter(Boolean).join("\n\n");
  let providerResult: Awaited<ReturnType<typeof resolved.provider.invokeTool>> | undefined;

  const enrich = (failure: AiFailure, diagnostics?: ProviderDiagnostics): AiFailure => {
    if (!diagnostics) return failure;
    return {
      reason:
        failure.reason === "timeout" && diagnostics.timeoutReason
          ? diagnostics.timeoutReason
          : failure.reason,
      detail: diagnostics.detail
        ? safeDetail(`${failure.detail} | ${diagnostics.detail}`)
        : failure.detail,
    };
  };
  const finish = (): ExtractOutcome => {
    const outcome = decideExtractOutcome({
      captured,
      timedOut,
      aborted: controller.signal.aborted,
      runFailure: providerResult?.failure,
      toolName,
      timeoutMs: EXTRACT_TIMEOUT_MS,
    });
    const failure = outcome.failure && timedOut
      ? enrich(outcome.failure, providerResult?.diagnostics)
      : outcome.failure;
    return {
      ...outcome,
      ...(failure ? { failure } : {}),
      provider: resolved.providerId,
      model: resolved.modelId,
      usage: providerResult?.usage ?? null,
    };
  };
  let release: (() => void) | undefined;
  try {
    // Background (#279): de extractie-endpoints zijn batch-werk voor de
    // brein-mining — er zit geen bezoeker op te wachten. Ze mogen daarom
    // hoogstens de achtergrond-deelcap bezetten en worden in de rij altijd
    // ingehaald door /ask. Een mining-kaart die hierdoor een 429 krijgt komt
    // de volgende run gewoon terug (per-kaart-watermark); een weggestuurde
    // bezoeker niet.
    release = await aiSemaphore.acquire(1, {
      signal: controller.signal,
      maxWaitMs: AI_QUEUE_WAIT_MS,
      priority: "background",
    });
    // Timer PAS na de permit (#281): de 90 s is een begroting voor de
    // LLM-run, niet voor de wachtrij. Startte hij bij binnenkomst, dan at een
    // volle achtergrond-deelcap tot 30 s (AI_QUEUE_WAIT_MS) van het budget op
    // en verscheen het resultaat als een timeout die op de LLM leek te wijzen
    // terwijl de call gewoon te kort de tijd kreeg. Sinds de mining parallel
    // draait (#279) is die wachttijd de regel, niet de uitzondering.
    timer = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, EXTRACT_TIMEOUT_MS);
    providerResult = await resolved.provider.invokeTool({
      modelId: resolved.modelId,
      systemPrompt,
      prompt: text,
      tool: { name: toolName, description, schema: schema as z.ZodRawShape },
      signal: controller.signal,
      onToolCall: (name, input) => {
        if (name !== toolName) return false;
        const parsed = z.object(schema as z.ZodRawShape).safeParse(input);
        if (!parsed.success) return false;
        const value = (parsed.data as Record<string, unknown>)[resultKey];
        captured = Array.isArray(value) ? value : [];
        return true;
      },
    });
    // De leeslus is afgelopen — één beslispunt, gedeeld met het catch-blok.
    return finish();
  } catch (e) {
    if (e instanceof ConcurrencyLimitError) throw e;
    // Timeout/uitval is verwacht pad: null → rb-api degradeert. Wat de tool vóór
    // de uitval al ving blijft geldig; anders null MET de reden.
    // Een geworpen fout is alleen de oorzaak als er geen afkapping was; anders
    // wint de afkapping (dezelfde beslissing als hierboven).
    if (!timedOut && !controller.signal.aborted && captured === null)
      return {
        items: null,
        failure: failureOf(e),
        provider: resolved.providerId,
        model: resolved.modelId,
        usage: providerResult?.usage ?? null,
      };
    return finish();
  } finally {
    if (timer) clearTimeout(timer);
    signal?.removeEventListener("abort", onAbort);
    release?.();
  }
}

/** Bericht-vorm voor streaming input. Gedeeld door het koude beeld-pad
 * (userMessage hieronder) en de warme pool (het bericht dat bij de claim in
 * de vastgehouden input-iterator wordt gepusht) — de SDK schrijft voor een
 * kale string-prompt exact dezelfde content-vorm naar het subprocess, dus
 * warm en koud doen dezelfde API-call. */
export function buildUserMessage(prompt: string, images: AskImage[]) {
  return {
    type: "user" as const,
    message: {
      role: "user" as const,
      content: [
        ...images.map((img) => ({
          type: "image" as const,
          source: {
            type: "base64" as const,
            media_type: img.mediaType,
            data: img.data,
          },
        })),
        { type: "text" as const, text: prompt },
      ],
    },
    parent_tool_use_id: null,
    session_id: "rb-ai",
  };
}

/** Streaming-input met content-blocks — nodig zodra er afbeeldingen meegaan. */
async function* userMessage(prompt: string, images: AskImage[]) {
  yield buildUserMessage(prompt, images);
}

/** Antwoord + echte token-usage van één askClaude-run (#121). `usage` komt
 * uit het afsluitende result-bericht van de SDK (opgeteld over alle beurten,
 * incl. tool-overhead bij research/agentic) en is null wanneer de SDK er
 * geen meegaf — de aanroeper behandelt dat als "onbekend", nooit als 0.
 *
 * `failure` (#281) draagt de uitvalsoort die datzelfde result-bericht meldde
 * (`error_max_turns`, een `api_error_status`, een geweigerde tool). De Agent
 * SDK GOOIT daar niet bij — het is een gewoon bericht — dus zonder dit veld
 * eindigde een mislukte run als een leeg antwoord zonder enig spoor. */
export interface AskAnswer {
  answer: string;
  usage: AskUsage | null;
  failure?: AiFailure;
  /** Internal account-router signal; never serialized by the HTTP handlers. */
  accountSignal?: ClaudeRateLimitSignal;
}

/** Eén bron van waarheid voor de query-opties per taaktype (#154): het koude
 * pad én de warme pool bouwen hun opties hier — de contract-test in
 * ai.test.ts bewaakt dat warm en koud nooit uiteenlopen. systemPrompt en
 * includePartialMessages liggen bij de SDK vast op het moment van `query()`
 * (initialize-controlbericht resp. spawn-flag) en zijn daarom onderdeel van
 * de warme-pool-signatuur. */
export function buildQueryOptions(input: {
  task: Task;
  systemPrompt?: string;
  includePartialMessages: boolean;
  controller: AbortController;
  onBrainStep?: (step: string) => void;
  /** Model-sweep (#174): expliciete modeloverride voor deze call — alleen
   * gebruikt door benchmarkruns (AskOptions.Model in rb-api). Onbekend
   * volgt de gewone SDK-degradatie (query() geeft een fout terug, gevangen
   * door de aanroeper — geen aparte validatie hier); zonder override
   * ongewijzigd gedrag (MODEL[task]). */
  model?: string;
  /** Afnemer van de stderr van het Claude-subprocess (#300).
   *
   * VERPLICHT, EN DAT IS DE FIX. Deze optie is bij de SDK geen afnemer maar een
   * SCHAKELAAR: `stdio:["pipe","pipe", options.stderr ? "pipe" : "ignore"]`.
   * Zonder haar wordt de stroom niet doorgegeven-maar-genegeerd, ze wordt
   * WEGGEGOOID bij de spawn. `extractWithTool` zette de optie wel en
   * `buildQueryOptions` niet, dus de faaldiagnostiek van #281 was compleet op
   * het extract-pad en half op /ask — inclusief het agentic pad.
   *
   * Bewust een verplichte parameter in plaats van een optionele met een
   * bron-grep-test erop: zo is het de TYPECHECKER die elke nieuwe call-site
   * dwingt een staart te leveren. Een poort die je kunt vergeten is geen poort
   * (#292), en een structurele test op de aanroepvorm heeft altijd omzeilingen
   * (#295-review). Hier kan het per constructie niet fout gaan. */
  stderr: (data: string) => void;
  /** One already-selected account; omitted only by injected test runners. */
  accountEnvironment?: ClaudeAccountEnvironment;
}): Options {
  const {
    task, systemPrompt, includePartialMessages, controller, onBrainStep, model, stderr,
    accountEnvironment,
  } = input;
  const research = task === "research";
  const agentic = task === "agentic";
  return {
    model: model ?? MODEL[task],
    maxTurns: research ? RESEARCH_MAX_TURNS : agentic ? AGENTIC_MAX_TURNS : 1,
    // Basis-toolset (built-ins): leeg voor cheap/hard/agentic (agentic krijgt
    // zijn tools via de MCP-server hieronder), alleen de web-tools voor
    // research.
    tools: research ? RESEARCH_TOOLS : [],
    ...(research
      ? {
          // Headless: web-tools vooraf goedkeuren; al het overige wordt
          // geweigerd in plaats van op een prompt te blijven hangen.
          allowedTools: RESEARCH_TOOLS,
          permissionMode: "dontAsk" as const,
        }
      : {}),
    ...(agentic
      ? {
          // In-process brein-tools (§2.4): alléén mcp__brain__* in de
          // allowlist, headless — al het overige wordt geweigerd.
          mcpServers: { [BRAIN_SERVER_NAME]: createBrainMcpServer(onBrainStep) },
          allowedTools: brainToolAllowlist(),
          permissionMode: "dontAsk" as const,
        }
      : {}),
    abortController: controller,
    ...(systemPrompt ? { systemPrompt } : {}),
    // Streaming (#31): partial messages alleen aanzetten als er een
    // delta-afnemer is — anders blijft het berichtenverkeer zoals het was.
    ...(includePartialMessages ? { includePartialMessages: true } : {}),
    stderr,
    ...(accountEnvironment
      ? {
          env: { ...accountEnvironment },
          persistSession: false,
          settingSources: [],
        }
      : {}),
  };
}

/** De warme-boot-opties zijn per constructie het koude cheap-pad met
 * dezelfde signatuur — apart benoemd zodat de contract-test elke toekomstige
 * special-casing van het warme pad ziet.
 *
 * `stderr` komt van buiten omdat elke sessie een EIGEN staart hoort te hebben
 * (#300): het warm/koud-contract van #154 gaat over de opties waarmee het
 * subprocess gespawnd en de API-call gedaan wordt, en die blijven byte-gelijk —
 * de callback is, net als `abortController`, per sessie uniek en per definitie
 * geen onderdeel van die gelijkheid. Zou hij dat wél zijn, dan zouden warm en
 * koud in dezelfde buffer schrijven en was elke staart bij een gelijktijdige
 * call onbruikbaar. */
export function warmBootOptions(
  sig: WarmSignature,
  controller: AbortController,
  stderr: (data: string) => void,
  accountEnvironment?: ClaudeAccountEnvironment,
): Options {
  return buildQueryOptions({
    task: "cheap",
    systemPrompt: sig.systemPrompt,
    includePartialMessages: sig.includePartialMessages,
    controller,
    stderr,
    accountEnvironment,
  });
}

function bootWarmCheapSession(sig: WarmSignature): WarmBootHandle {
  const controller = new AbortController();
  const input = pushableInput<ReturnType<typeof buildUserMessage>>();
  // Eigen staart per warme sessie (#300), aangelegd vóór de spawn zodat ook
  // wat het subprocess tijdens de boot al roept erin belandt — precies de
  // uitvoer die verklaart waarom een claim later dood blijkt.
  const stderr = new StderrTail();
  const q = query({
    prompt: input.iterable as Parameters<typeof query>[0]["prompt"],
    options: warmBootOptions(
      sig,
      controller,
      (data: string) => stderr.append(data),
      runtimeManager.currentGeneration().claudeRouter.singleEnvironment(),
    ),
  });
  return {
    messages: q,
    stderr,
    push: (m) => input.push(m as ReturnType<typeof buildUserMessage>),
    endInput: () => input.end(),
    kill: () => {
      controller.abort();
      (q as { close?: () => void }).close?.();
    },
  };
}

/** Warme-sessie-pool (#154), signaal-gedreven — zie warmpool.ts voor het
 * ontwerp en de grenzen. Kill-switch: AI_WARM_POOL=0 (default AAN: zonder
 * /prewarm-signaal boot er toch niets, en de degradatie naar koud is
 * transparant — de schakelaar is er voor ops-noodgevallen). */
export const warmPool = new WarmPool({
  boot: bootWarmCheapSession,
  // A warm process is already bound to one credential. With multiple accounts
  // it cannot be reassigned safely, so routing stays cold and quota-aware.
  enabled: !["0", "false", "off"].includes(
    (process.env.AI_WARM_POOL ?? "1").toLowerCase(),
  ),
  ttlMs: (() => {
    const parsed = Number.parseInt(process.env.AI_WARM_TTL_MS ?? "", 10);
    return Number.isFinite(parsed) && parsed >= 1_000 ? parsed : 600_000; // 10 min
  })(),
});
const syncWarmPoolTopology = () => warmPool.setTopologyEnabled(
  runtimeManager.currentGeneration().claudeRouter.accountCount() === 1,
);
syncWarmPoolTopology();
runtimeManager.onTopologyChange(syncWarmPoolTopology);

/** Voortgangsvlag voor de leeslus: heeft de sessie echt output geleverd
 * (assistant/result/tekst-delta)? Bij een warme sessie die dood bleek bij de
 * claim blijft dit false — dan is er gegarandeerd geen API-call gedaan en
 * mag ai.ts transparant koud opnieuw starten. */
export interface CollectProgress {
  sawOutput: boolean;
  /** Text already emitted through onDelta. Retrying after this would append a
   * second account's answer to the first account's partial response. */
  deliveredText: boolean;
}

/** Gedeelde leeslus over de SDK-berichten (koud én warm — #154).
 *
 * De Agent SDK levert dezelfde tekst twee keer: als streaming 'assistant'-
 * berichten én als afsluitend 'result'-bericht. Tel ze NIET op — verzamel
 * apart en geef het 'result' terug; val terug op assistant-tekst zonder
 * result. Het result-bericht draagt ook de echte token-usage (#121). */
export async function collectAnswer(
  messages: AsyncIterable<unknown>,
  onDelta?: (text: string) => void | Promise<void>,
  progress?: CollectProgress,
  retries?: RetryTracker,
): Promise<AskAnswer> {
  let assistantText = "";
  let resultText = "";
  let usage: AskUsage | null = null;
  let failure: AiFailure | undefined;
  let accountSignal: ClaudeRateLimitSignal | undefined;
  for await (const message of messages) {
    // Mislukte run (#281): de SDK meldt die met een result-bericht, niet met
    // een exception. Vóór deze regel viel zo'n run stil terug op een leeg
    // antwoord — de directe oorzaak van 22 spoorloze 5xx'en. `api_retry`-
    // berichten gaan naar de tracker: die maken zichtbaar dat de SDK intern
    // al minutenlang op een 429/529 zat te wachten.
    retries?.observe(message);
    accountSignal = claudeRateLimitSignal(message) ?? accountSignal;
    failure = mergeClaudeMessageFailure(failure, message);
    if (accountSignal?.status === "rejected")
      failure = failure
        ?? { reason: "api_error", detail: "Claude-account heeft zijn rate limit bereikt" };
    const m = message as {
      type: string;
      text?: string;
      result?: string;
      usage?: unknown;
      message?: { content?: Array<{ type: string; text?: string }> };
      event?: { type?: string; delta?: { type?: string; text?: string } };
    };
    if (m.type === "stream_event") {
      // Partial-message-event: alleen echte text-deltas doorgeven; het
      // volledige antwoord komt daarnaast gewoon als assistant/result
      // binnen (dus hier NIET aan assistantText/resultText toevoegen).
      const ev = m.event;
      if (
        ev?.type === "content_block_delta" &&
        ev.delta?.type === "text_delta" &&
        ev.delta.text
      ) {
        if (progress) progress.sawOutput = true;
        if (onDelta) {
          if (progress) progress.deliveredText = true;
          await onDelta(ev.delta.text);
        }
      }
    } else if (m.type === "assistant" && Array.isArray(m.message?.content)) {
      if (progress) progress.sawOutput = true;
      for (const block of m.message.content) {
        if (block.type === "text" && block.text) assistantText += block.text;
      }
    } else if (m.type === "text" && m.text) {
      if (progress) progress.sawOutput = true;
      assistantText += m.text;
    } else if (m.type === "result") {
      if (progress) progress.sawOutput = true;
      if (m.result) resultText += m.result;
      usage = usageFromSdk(m.usage) ?? usage;
    }
  }
  return {
    answer: (resultText || assistantText).trim(),
    usage,
    ...(failure ? { failure } : {}),
    ...(accountSignal ? { accountSignal } : {}),
  };
}

/** De afloop van één uitgelezen /ask-run: gedeeld door het koude pad én de
 * warme claim (#300).
 *
 * WAAROM DIT GEDEELD MOET ZIJN. Het koude pad hád deze poort al — `if
 * (res.failure && !res.answer) throw new AiRunError(...)` — en de warme claim
 * niet: die deed `if (progress.sawOutput) return res` en gaf een mislukte run
 * dus terug als een 200 met een leeg antwoord. Exact dezelfde run leverde koud
 * een AiRunError met reden op en warm niets. Dat is dezelfde soort stilte als
 * #281 zelf: niet een verkeerde melding, maar géén melding, langs precies het
 * pad dat een gedragstest op het andere pad niet ziet. Eén functie voor beide
 * uitgangen maakt die drift per constructie onmogelijk (zelfde reden als
 * {@link decideExtractOutcome} op het extract-pad).
 *
 * De stderr-staart komt van de aanroeper omdat alléén die weet WELKE sessie
 * deze run draaide — zie de attributie-regel in {@link askClaude}. */
function finishAskRun(
  res: AskAnswer,
  stderr: StderrTail,
  retries: RetryTracker,
): AskAnswer {
  if (!res.failure) return res;
  const failure = withStderrDigest(withRetries(res.failure, retries), stderr);
  // Mislukte run ZONDER antwoord (#281): de SDK gooit hier niet, dus dit
  // eindigde voorheen als een 200 met een leeg antwoord — rb-api degradeerde
  // dan wel correct naar null, maar de reden was nergens te zien. Met een
  // antwoord erbij is de run bruikbaar en telt de fout niet als uitval.
  if (!res.answer) throw new AiRunError(failure);
  return { ...res, failure };
}

/** Stuur één prompt (optioneel met afbeeldingen) naar Claude.
 *
 * Met `onDelta` (#31, streaming) levert de Agent SDK naast de gewone
 * berichten ook partial-message-events (`includePartialMessages`); elke
 * text-delta gaat direct naar de callback zodat de aanroeper het antwoord
 * woord-voor-woord kan doorsturen. De return-waarde blijft in beide gevallen
 * het volledige eindantwoord — de niet-streamende route verandert niet.
 *
 * Capaciteit (#155): elke run verwerft eerst een permit in de globale
 * semaphore (agentic weegt 2); boven de cap wacht hij kort in de rij en
 * daarna bubbelt een ConcurrencyLimitError naar server.ts (429).
 *
 * Warme pool (#154): een cheap-call waarvan de sessie-opties byte-gelijk
 * zijn aan een voorverwarmde sessie krijgt die sessie (subprocess-boot al
 * betaald); in alle andere gevallen — en bij een dood gebleken warme sessie
 * — start hij transparant koud. Eén sessie = één call, nooit hergebruik.
 * Een `model`-override (#174) slaat de warme pool altijd over — de
 * voorverwarmde sessie is altijd op MODEL.cheap gebootstrapt, dus een claim
 * zou de override stilzwijgend negeren; zie de guard hieronder. */
export async function askClaude(opts: {
  prompt: string;
  system?: string;
  task?: Task;
  images?: AskImage[];
  onDelta?: (text: string) => void | Promise<void>;
  /** #107: ontvangt per brein-tool-call één regel (toolnaam + argumenten);
   * alleen relevant bij task="agentic" — server.ts geeft ze als `steps`
   * terug zodat rb-api ze in AskTrace.BrainSteps kan vastleggen. */
  onBrainStep?: (step: string) => void;
  signal?: AbortSignal;
  /** Model-sweep (#174): expliciete modeloverride voor déze call — alleen
   * gezet door een benchmarkrun (rb-api's AskOptions.Model reist hier
   * ongewijzigd doorheen). Undefined = het bestaande gedrag (MODEL[task]). */
  model?: string;
  /** Test-seam (#300): de SDK-aanroep van het KOUDE pad. Productie laat dit weg
   * en krijgt `query`; zie {@link AskQueryRunner}. */
  runQuery?: AskQueryRunner;
  /** Account-router seam for routing/failover tests. Supplying it also routes
   * an injected runner, while the historical runQuery-only seam stays a
   * single-account SDK simulation for existing tests. */
  accountRouter?: ClaudeAccountRouter;
  /** Test-seam (#300): de warme pool. Productie laat dit weg en krijgt de
   * module-singleton {@link warmPool}.
   *
   * Waarom deze naad er nu is: het warme pad kon tot dusver alleen met een echt
   * SDK-subprocess doorlopen worden, dus geen enkele test kwam er ooit — en
   * precies daar zat het gat dat deze PR opruimt (een warme claim die met een
   * fout-result eindigde gaf een leeg antwoord terug waar het koude pad een
   * AiRunError gooide). Een gedragstest kan per definitie niet zien dat er een
   * TWEEDE pad bestaat dat ze nooit aanroept (#292); met deze naad roept ze het
   * wél aan. */
  pool?: WarmPool;
}): Promise<AskAnswer> {
  const {
    prompt, system, task = "cheap", images = [], onDelta, onBrainStep, signal, model,
    runQuery: injectedRunQuery,
    accountRouter: injectedAccountRouter,
    pool = warmPool,
  } = opts;
  const productionRouting = injectedRunQuery === undefined || opts.accountRouter !== undefined;
  const generationLease = injectedRunQuery === undefined && !injectedAccountRouter
    ? runtimeManager.lease()
    : undefined;
  const accountRouter = injectedAccountRouter
    ?? generationLease?.generation.claudeRouter
    ?? getFallbackClaudeAccountRouter();
  const runQuery = injectedRunQuery ?? query as unknown as AskQueryRunner;
  const research = task === "research";
  const agentic = task === "agentic";

  // Server-side addendum per taak (nooit door de aanroeper te omzeilen):
  // research krijgt het bronnen-contract, agentic de brein-voorrangregels.
  const addendum = research ? RESEARCH_CONTRACT : agentic ? AGENT_ADDENDUM : undefined;
  const systemPrompt = addendum
    ? [system, addendum].filter(Boolean).join("\n\n")
    : system;

  // Eén AbortController voor álle taken (review #31): server.ts koppelt er
  // de client-verbinding aan (`signal`), zodat een weggelopen client de
  // Claude-call afbreekt in plaats van hem op abonnementskosten af te laten
  // maken. De harde timeout geldt alleen voor de multi-turn-taken (research,
  // agentic): de andere taken zijn één beurt en kennen dat risico niet.
  const timeoutMs = research
    ? RESEARCH_TIMEOUT_MS
    : agentic
      ? AGENTIC_TIMEOUT_MS
      : undefined;
  const controller = new AbortController();
  let timedOut = false;
  const timer = timeoutMs
    ? setTimeout(() => {
        timedOut = true;
        controller.abort();
      }, timeoutMs)
    : undefined;
  const onAbort = () => controller.abort();
  if (signal?.aborted) controller.abort();
  else signal?.addEventListener("abort", onAbort, { once: true });

  const includePartialMessages = Boolean(onDelta);
  // SDK-interne retries (#281): dezelfde meting als op het extract-pad, zodat
  // een /ask-timeout die in werkelijkheid een aanhoudende API-fout was ook hier
  // als zodanig in de log belandt.
  let retries = new RetryTracker();
  // Stderr van het subprocess (#300). De KOUDE sessie krijgt deze buffer; een
  // warme claim brengt zijn eigen mee (aangelegd bij zijn boot).
  let coldStderr = new StderrTail();
  /** Welke staart de uitval van DEZE aanroep verklaart.
   *
   * ATTRIBUTIE IS HIER HET HELE PUNT, en fout toewijzen is erger dan geen
   * staart: dan diagnosticeer je met stelligheid de verkeerde sessie. De regel
   * is simpel omdat de ontwerpgrens van de pool simpel is (één sessie = één
   * call): de staart van de sessie die de run daadwerkelijk draaide, en niets
   * anders. Valt een warme claim dood terug op koud, dan wordt dit veld
   * expliciet teruggezet — de warme regels horen bij de warme sessie en zijn
   * daar al gemeld, en de koude herstart is een ándere sessie. */
  let failureStderr = coldStderr;
  let release: (() => void) | undefined;
  let unhookWarmAbort: (() => void) | undefined;
  let progress: CollectProgress = { sawOutput: false, deliveredText: false };
  try {
    // Permit vóór elke sessie-start (koud én warm-claim) — de voorverwarmde
    // boot zelf telt niet (idle, geen API-call; de pool-cap begrenst die al).
    // Interactief (#279, de default): dit pad draagt /ask, dus het mag tot aan
    // de volle cap en haalt wachtend mining-werk in.
    release = await aiSemaphore.acquire(agentic ? 2 : 1, {
      signal: controller.signal,
      maxWaitMs: AI_QUEUE_WAIT_MS,
      priority: "interactive",
    });

    if (productionRouting) await accountRouter.refreshQuotas();
    const excludedAccounts = new Set<number>();
    let lastAccountFailure: AiFailure | undefined;
    const routeModel = model ?? MODEL[task];

    while (true) {
      const lease: ClaudeAccountLease | undefined = productionRouting
        ? accountRouter.acquire(routeModel, excludedAccounts) ?? undefined
        : undefined;
      if (productionRouting && !lease)
        throw new AiRunError(lastAccountFailure ?? {
          reason: accountRouter.configured() ? "api_error" : "auth",
          detail: accountRouter.configured()
            ? "geen Claude-account met gebruiksruimte beschikbaar"
            : "geen Claude-accounts geconfigureerd",
        });
      if (lease) excludedAccounts.add(lease.ordinal);

      retries = new RetryTracker();
      coldStderr = new StderrTail();
      failureStderr = coldStderr;
      progress = { sawOutput: false, deliveredText: false };
      let accountSignal: ClaudeRateLimitSignal | undefined;
      try {
        // Warm sessions are credential-bound. The module pool is disabled for
        // multi-account production; injected test pools retain their old seam.
        if (
          task === "cheap"
          && !model
          && pool.isEnabled()
          && (!productionRouting || accountRouter.accountCount() <= 1)
        ) {
          const sig: WarmSignature = { systemPrompt, includePartialMessages };
          pool.observe(sig);
          const claimed = controller.signal.aborted ? null : pool.claim(sig);
          if (claimed) {
            failureStderr = claimed.stderr;
            const killWarm = () => claimed.kill();
            controller.signal.addEventListener("abort", killWarm, { once: true });
            unhookWarmAbort = () =>
              controller.signal.removeEventListener("abort", killWarm);
            try {
              claimed.send(buildUserMessage(prompt, images));
              const res = await collectAnswer(claimed.messages(), onDelta, progress, retries);
              accountSignal = res.accountSignal;
              if (lease) accountRouter.observeSignal(lease, accountSignal);
              if (progress.sawOutput) {
                const completed = finishAskRun(res, claimed.stderr, retries);
                if (lease && !completed.failure) accountRouter.markSuccess(lease);
                return completed;
              }
              logEvent("warmpool_fallback", {
                stage: "dood bij claim",
                stderr: stderrDigestLine(claimed.stderr) || undefined,
              });
            } catch (e) {
              if (progress.sawOutput || controller.signal.aborted || timedOut) throw e;
              const failure = describeThrown(e);
              logEvent("warmpool_fallback", {
                stage: "faalde vóór output",
                reason: failure.reason,
                detail: failure.detail,
                stderr: stderrDigestLine(claimed.stderr) || undefined,
              });
            }
            pool.noteDeadClaim();
            progress.sawOutput = false;
            failureStderr = coldStderr;
          }
        }

        const options = buildQueryOptions({
          task,
          systemPrompt,
          includePartialMessages,
          controller,
          onBrainStep,
          model,
          stderr: (data: string) => coldStderr.append(data),
          accountEnvironment: lease?.environment,
        });
        const arg = {
          prompt: images.length > 0 ? userMessage(prompt, images) : prompt,
          options,
        };
        const res = await collectAnswer(runQuery(arg), onDelta, progress, retries);
        accountSignal = res.accountSignal;
        if (lease) accountRouter.observeSignal(lease, accountSignal);
        const completed = finishAskRun(res, coldStderr, retries);
        if (lease && !completed.failure) accountRouter.markSuccess(lease);
        return completed;
      } catch (e) {
        const accountFailure = e instanceof AiRunError
          ? failureOf(e)
          : withStderrDigest(withRetries(describeThrown(e), retries), failureStderr);
        const canFailOver = Boolean(
          lease
          && !controller.signal.aborted
          && !timedOut
          && isClaudeAccountLocalFailure(accountFailure, accountSignal)
          && !progress.deliveredText,
        );
        if (!canFailOver || !lease) throw e;
        lastAccountFailure = accountFailure;
        accountRouter.markAccountFailure(lease, accountFailure, accountSignal);
      } finally {
        unhookWarmAbort?.();
        unhookWarmAbort = undefined;
        lease?.release();
      }
    }
  } catch (e) {
    // Capaciteitsgrens (#155) onvertaald doorgeven: server.ts maakt er een
    // 429 met machine-leesbare reden van.
    if (e instanceof ConcurrencyLimitError) throw e;
    // AL geclassificeerd door `finishAskRun` (#300-review): niet opnieuw door
    // `describeThrown` halen. Dat zou de reden HERclassificeren op de tekst van
    // de al-gebouwde melding — `max_turns`/`permission_denied` werden zo
    // `unknown` (`describeThrown` kent geen AiRunError-special-case, alleen
    // `failureOf` doet dat), of toevallig `spawn` omdat "exited with code 137"
    // uit de stderr-staart in de message stond. Plus: de stderr-digest werd een
    // TWEEDE keer aangeplakt en er kwam een `AiRunError:`-prefix als ruis bij.
    // De reason is precies de knop die de beheerder afleest — dit is #281
    // opnieuw. De enige AiRunError die hier aankomt komt uit `finishAskRun`;
    // `collectAnswer`/de warme claim gooien rauwe fouten.
    if (e instanceof AiRunError) throw e;
    // Timeout en client-abort herkenbaar maken voor de aanroeper (run_log);
    // overige fouten ongewijzigd doorgeven — server.ts vertaalt ze naar een
    // nette 500 of een error-frame.
    if (timedOut && timeoutMs) {
      throw new AiRunError(withStderrDigest(
        withRetries(
          {
            reason: "timeout",
            detail: `${task}-call afgebroken na ${timeoutMs / 1000}s (harde timeout)`,
          },
          retries,
        ),
        failureStderr,
      ));
    }
    if (controller.signal.aborted) {
      // Bewust ZONDER stderr-staart: de client liep weg, er is niets aan de
      // machine te diagnosticeren, en dit is het pad dat het vaakst vuurt —
      // diagnostiek die niets verklaart is hier alleen ruis en lekoppervlak.
      throw new AiRunError({
        reason: "aborted",
        detail: "aanroep afgebroken: client heeft de verbinding gesloten",
      });
    }
    // Overige fouten geclassificeerd doorgeven (#281): de melding blijft
    // leesbaar (AiRunError.message = "reden: detail") en server.ts kan de reden
    // eruit lezen in plaats van hem uit een string te moeten raden. De staart
    // van de sessie die dit veroorzaakte gaat mee (#300) — dit is het pad waar
    // een omgevallen subprocess uitkomt, en juist daar was `reason: spawn`
    // voorheen alles wat er stond.
    throw new AiRunError(withStderrDigest(withRetries(describeThrown(e), retries), failureStderr));
  } finally {
    if (timer) clearTimeout(timer);
    signal?.removeEventListener("abort", onAbort);
    unhookWarmAbort?.();
    generationLease?.release();
    release?.();
  }
}
