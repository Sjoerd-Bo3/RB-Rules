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
  resultFailure,
  RetryTracker,
  StderrTail,
  withRetries,
  withStderr,
  type AiFailure,
} from "./failure.js";
import { RELATIONS_MARKER } from "./relations.js";
import { usageFromSdk, type AskUsage } from "./usage.js";
import {
  pushableInput,
  WarmPool,
  type WarmBootHandle,
  type WarmSignature,
} from "./warmpool.js";

// Auth: CLAUDE_CODE_OAUTH_TOKEN (abonnement) of ANTHROPIC_API_KEY.
// Laat ANTHROPIC_API_KEY leeg bij abonnementsgebruik — die wint stilletjes.
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

/** In-process MCP-server met de zes brein-tools (§2.4, createSdkMcpServer).
 * Per aanroep een verse sessie zodat de tool-call-cap per vraag telt. De
 * tool-call-log op stdout maakt agent-stappen zichtbaar in de containerlog
 * (verificatiepad #106); via `onStep` gaan dezelfde regels naar de aanroeper
 * zodat /ask ze als `steps` kan teruggeven — deelissue 4 (#107) maakt ze zo
 * meetbaar in AskTrace.BrainSteps.
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
        const step = `${t.name} ${compactJson(a).slice(0, 200)}`;
        console.log(`[agentic] ${step}`);
        onStep?.(step);
        // session.run gooit nooit: fouten (rb-api plat, timeout, cap) komen
        // als leesbaar toolresultaat terug — fouten zijn data (§2.3).
        return { content: [{ type: "text" as const, text: await session.run(t.name, a) }] };
      }),
    ),
  });
}

// Brein-extractie (#226, §3.1): tool-forced structured output. Eén beurt om de
// tool te roepen, één om af te ronden — ruim gehouden op 3. Harde timeout net als
// research/agentic zodat een hangende run nooit op abonnementskosten blijft draaien.
const EXTRACT_MAX_TURNS = 3;

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
export type QueryRunner = (arg: {
  prompt: string;
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
 * de agentic brein-tools) waarvan het zod-schema de enum-poorten dichttimmert: het
 * model KAN geen ref/kind/window buiten het aangeboden vocabulaire noemen. De
 * tool-handler VANGT de gevalideerde argumenten in een closure en geeft een ack
 * terug; de daadwerkelijke kandidaten reizen dus niet via de antwoordtekst maar via
 * de tool-input. Puur SDK-gedreven en dus, net als askClaude, niet los unit-getest;
 * de vocabulaire→schema-vertaling in extract.ts en de faalvertaling in failure.ts
 * zijn dat wél.
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
    runQuery = query as unknown as QueryRunner,
  } = opts;
  const serverName = "extract";

  let captured: unknown[] | null = null;
  const extractServer = createSdkMcpServer({
    name: serverName,
    version: "1.0.0",
    tools: [
      tool(toolName, description, schema, async (args) => {
        const value = (args as Record<string, unknown>)[resultKey];
        // Alleen een echte array telt; alles anders is een lege vangst (de .NET-
        // parser is de tweede muur, maar we leveren nooit rommel op).
        captured = Array.isArray(value) ? value : [];
        return { content: [{ type: "text" as const, text: "ok" }] };
      }),
    ],
  });

  const controller = new AbortController();
  let timedOut = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const onAbort = () => controller.abort();
  if (signal?.aborted) controller.abort();
  else signal?.addEventListener("abort", onAbort, { once: true });

  const systemPrompt = [system, addendum].filter(Boolean).join("\n\n");
  // Stderr van het subprocess meelezen (#281): stil bij succes, doorslaggevend
  // bij uitval — zie StderrTail.
  const stderr = new StderrTail();
  // SDK-interne retries meetellen (#281): een aanhoudende 429/529 kost via de
  // exponentiële backoff meer dan de hele 90s-begroting en verscheen daardoor
  // als onze timeout in plaats van als de API-fout die het was.
  const retries = new RetryTracker();
  /** Elke uitval krijgt dezelfde context mee: wat het subprocess naar stderr
   * schreef en hoe vaak de SDK intern opnieuw probeerde. Eén poort, zodat geen
   * enkel faalpad half-geïnstrumenteerd kan achterblijven. */
  const enrich = (failure: AiFailure): AiFailure =>
    withRetries(withStderr(failure, stderr), retries);
  /** Beide uitgangen — na de leeslus én uit het catch-blok — lopen hier
   * doorheen, zodat ze per constructie dezelfde beslissing nemen. De
   * stderr-staart en de SDK-retries worden er hier overheen gelegd. */
  const finish = (): ExtractOutcome => {
    const outcome = decideExtractOutcome({
      captured,
      timedOut,
      aborted: controller.signal.aborted,
      runFailure,
      toolName,
      timeoutMs: EXTRACT_TIMEOUT_MS,
    });
    return outcome.failure ? { ...outcome, failure: enrich(outcome.failure) } : outcome;
  };
  let release: (() => void) | undefined;
  let runFailure: AiFailure | undefined;
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
    const options: Options = {
      model: MODEL.cheap,
      maxTurns: EXTRACT_MAX_TURNS,
      tools: [],
      mcpServers: { [serverName]: extractServer },
      allowedTools: [`mcp__${serverName}__${toolName}`],
      permissionMode: "dontAsk" as const,
      abortController: controller,
      systemPrompt,
      stderr: (data: string) => stderr.append(data),
    };
    // De berichten leeglezen zodat de tool-call daadwerkelijk vuurt; het
    // tekstantwoord interesseert ons niet — captured draagt het resultaat.
    // Het afsluitende result-bericht lezen we WEL (#281): daar — en nergens
    // anders — meldt de SDK dat de run mislukte.
    for await (const message of runQuery({ prompt: text, options })) {
      retries.observe(message);
      runFailure = resultFailure(message) ?? runFailure;
    }
    // De leeslus is afgelopen — één beslispunt, gedeeld met het catch-blok.
    return finish();
  } catch (e) {
    if (e instanceof ConcurrencyLimitError) throw e;
    // Timeout/uitval is verwacht pad: null → rb-api degradeert. Wat de tool vóór
    // de uitval al ving blijft geldig; anders null MET de reden.
    // Een geworpen fout is alleen de oorzaak als er geen afkapping was; anders
    // wint de afkapping (dezelfde beslissing als hierboven).
    if (!timedOut && !controller.signal.aborted && captured === null)
      return { items: null, failure: enrich(runFailure ?? describeThrown(e)) };
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
}): Options {
  const { task, systemPrompt, includePartialMessages, controller, onBrainStep, model } = input;
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
  };
}

/** De warme-boot-opties zijn per constructie het koude cheap-pad met
 * dezelfde signatuur — apart benoemd zodat de contract-test elke toekomstige
 * special-casing van het warme pad ziet. */
export function warmBootOptions(sig: WarmSignature, controller: AbortController): Options {
  return buildQueryOptions({
    task: "cheap",
    systemPrompt: sig.systemPrompt,
    includePartialMessages: sig.includePartialMessages,
    controller,
  });
}

function bootWarmCheapSession(sig: WarmSignature): WarmBootHandle {
  const controller = new AbortController();
  const input = pushableInput<ReturnType<typeof buildUserMessage>>();
  const q = query({
    prompt: input.iterable as Parameters<typeof query>[0]["prompt"],
    options: warmBootOptions(sig, controller),
  });
  return {
    messages: q,
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
  enabled: !["0", "false", "off"].includes(
    (process.env.AI_WARM_POOL ?? "1").toLowerCase(),
  ),
  ttlMs: (() => {
    const parsed = Number.parseInt(process.env.AI_WARM_TTL_MS ?? "", 10);
    return Number.isFinite(parsed) && parsed >= 1_000 ? parsed : 600_000; // 10 min
  })(),
});

/** Voortgangsvlag voor de leeslus: heeft de sessie echt output geleverd
 * (assistant/result/tekst-delta)? Bij een warme sessie die dood bleek bij de
 * claim blijft dit false — dan is er gegarandeerd geen API-call gedaan en
 * mag ai.ts transparant koud opnieuw starten. */
export interface CollectProgress {
  sawOutput: boolean;
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
  for await (const message of messages) {
    // Mislukte run (#281): de SDK meldt die met een result-bericht, niet met
    // een exception. Vóór deze regel viel zo'n run stil terug op een leeg
    // antwoord — de directe oorzaak van 22 spoorloze 5xx'en. `api_retry`-
    // berichten gaan naar de tracker: die maken zichtbaar dat de SDK intern
    // al minutenlang op een 429/529 zat te wachten.
    retries?.observe(message);
    failure = resultFailure(message) ?? failure;
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
        if (onDelta) await onDelta(ev.delta.text);
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
  };
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
}): Promise<AskAnswer> {
  const { prompt, system, task = "cheap", images = [], onDelta, onBrainStep, signal, model } = opts;
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
  const retries = new RetryTracker();
  let release: (() => void) | undefined;
  let unhookWarmAbort: (() => void) | undefined;
  const progress: CollectProgress = { sawOutput: false };
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

    if (task === "cheap" && !model && warmPool.isEnabled()) {
      const sig: WarmSignature = { systemPrompt, includePartialMessages };
      warmPool.observe(sig);
      const claimed = controller.signal.aborted ? null : warmPool.claim(sig);
      if (claimed) {
        const killWarm = () => claimed.kill();
        controller.signal.addEventListener("abort", killWarm, { once: true });
        unhookWarmAbort = () =>
          controller.signal.removeEventListener("abort", killWarm);
        try {
          claimed.send(buildUserMessage(prompt, images));
          const res = await collectAnswer(claimed.messages(), onDelta, progress, retries);
          if (progress.sawOutput) return res;
          // Sessie eindigde zonder één output-bericht: subprocess was dood
          // bij de claim — er is geen API-call gedaan, dus koud is veilig.
          console.log("[warmpool] warme sessie dood bij claim — transparant koud gestart");
        } catch (e) {
          if (progress.sawOutput || controller.signal.aborted || timedOut) throw e;
          console.log(
            `[warmpool] warme sessie faalde vóór output — transparant koud gestart: ${String(e).slice(0, 200)}`,
          );
        }
        warmPool.noteDeadClaim();
        progress.sawOutput = false;
      }
    }

    const options = buildQueryOptions({
      task,
      systemPrompt,
      includePartialMessages,
      controller,
      onBrainStep,
      model,
    });
    const arg = {
      prompt: images.length > 0 ? userMessage(prompt, images) : prompt,
      options,
    };
    const res = await collectAnswer(
      query(arg as Parameters<typeof query>[0]),
      onDelta,
      progress,
      retries,
    );
    // Mislukte run ZONDER antwoord (#281): de SDK gooit hier niet, dus dit
    // eindigde voorheen als een 200 met een leeg antwoord — rb-api degradeerde
    // dan wel correct naar null, maar de reden was nergens te zien. Met een
    // antwoord erbij is de run bruikbaar en telt de fout niet.
    if (res.failure && !res.answer) throw new AiRunError(withRetries(res.failure, retries));
    return res;
  } catch (e) {
    // Capaciteitsgrens (#155) onvertaald doorgeven: server.ts maakt er een
    // 429 met machine-leesbare reden van.
    if (e instanceof ConcurrencyLimitError) throw e;
    // Timeout en client-abort herkenbaar maken voor de aanroeper (run_log);
    // overige fouten ongewijzigd doorgeven — server.ts vertaalt ze naar een
    // nette 500 of een error-frame.
    if (timedOut && timeoutMs) {
      throw new AiRunError(withRetries(
        {
          reason: "timeout",
          detail: `${task}-call afgebroken na ${timeoutMs / 1000}s (harde timeout)`,
        },
        retries,
      ));
    }
    if (controller.signal.aborted) {
      throw new AiRunError({
        reason: "aborted",
        detail: "aanroep afgebroken: client heeft de verbinding gesloten",
      });
    }
    // Overige fouten geclassificeerd doorgeven (#281): de melding blijft
    // leesbaar (AiRunError.message = "reden: detail") en server.ts kan de reden
    // eruit lezen in plaats van hem uit een string te moeten raden.
    throw new AiRunError(withRetries(describeThrown(e), retries));
  } finally {
    if (timer) clearTimeout(timer);
    signal?.removeEventListener("abort", onAbort);
    unhookWarmAbort?.();
    release?.();
  }
}
