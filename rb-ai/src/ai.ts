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
const EXTRACT_TIMEOUT_MS = 90_000;

/** Tool-forced brein-extractie (#226, docs/ARCHITECTURE brein-epic §3.1). Draait
 * één geforceerde in-process MCP-tool (createSdkMcpServer/tool, zelfde mechaniek als
 * de agentic brein-tools) waarvan het zod-schema de enum-poorten dichttimmert: het
 * model KAN geen ref/kind/window buiten het aangeboden vocabulaire noemen. De
 * tool-handler VANGT de gevalideerde argumenten in een closure en geeft een ack
 * terug; de daadwerkelijke kandidaten reizen dus niet via de antwoordtekst maar via
 * de tool-input. Retourneert de gevangen array (mogelijk leeg) of <c>null</c> als de
 * tool niet werd geroepen / de run faalde — de aanroeper (rb-api) degradeert daarop
 * netjes (null → geen half feit). Puur SDK-gedreven en dus, net als askClaude, niet
 * los unit-getest; de vocabulaire→schema-vertaling in extract.ts is dat wél. */
export async function extractWithTool(opts: {
  toolName: string;
  description: string;
  schema: Parameters<typeof tool>[2];
  resultKey: string;
  system?: string;
  addendum: string;
  text: string;
  signal?: AbortSignal;
}): Promise<unknown[] | null> {
  const { toolName, description, schema, resultKey, system, addendum, text, signal } = opts;
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
  const timer = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, EXTRACT_TIMEOUT_MS);
  const onAbort = () => controller.abort();
  if (signal?.aborted) controller.abort();
  else signal?.addEventListener("abort", onAbort, { once: true });

  const systemPrompt = [system, addendum].filter(Boolean).join("\n\n");
  let release: (() => void) | undefined;
  try {
    release = await aiSemaphore.acquire(1, {
      signal: controller.signal,
      maxWaitMs: AI_QUEUE_WAIT_MS,
    });
    const options: Options = {
      model: MODEL.cheap,
      maxTurns: EXTRACT_MAX_TURNS,
      tools: [],
      mcpServers: { [serverName]: extractServer },
      allowedTools: [`mcp__${serverName}__${toolName}`],
      permissionMode: "dontAsk" as const,
      abortController: controller,
      systemPrompt,
    };
    // De berichten leeglezen zodat de tool-call daadwerkelijk vuurt; het
    // tekstantwoord interesseert ons niet — captured draagt het resultaat.
    for await (const _ of query({ prompt: text, options })) {
      // no-op: de closure hierboven vangt de tool-input.
    }
    return captured;
  } catch (e) {
    if (e instanceof ConcurrencyLimitError) throw e;
    // Timeout/uitval is verwacht pad: null → rb-api degradeert. Wat de tool vóór
    // de uitval al ving blijft geldig; anders null.
    if (timedOut) return captured;
    if (controller.signal.aborted) return captured;
    throw e;
  } finally {
    clearTimeout(timer);
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
 * geen meegaf — de aanroeper behandelt dat als "onbekend", nooit als 0. */
export interface AskAnswer {
  answer: string;
  usage: AskUsage | null;
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
): Promise<AskAnswer> {
  let assistantText = "";
  let resultText = "";
  let usage: AskUsage | null = null;
  for await (const message of messages) {
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
  return { answer: (resultText || assistantText).trim(), usage };
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
  let release: (() => void) | undefined;
  let unhookWarmAbort: (() => void) | undefined;
  const progress: CollectProgress = { sawOutput: false };
  try {
    // Permit vóór elke sessie-start (koud én warm-claim) — de voorverwarmde
    // boot zelf telt niet (idle, geen API-call; de pool-cap begrenst die al).
    release = await aiSemaphore.acquire(agentic ? 2 : 1, {
      signal: controller.signal,
      maxWaitMs: AI_QUEUE_WAIT_MS,
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
          const res = await collectAnswer(claimed.messages(), onDelta, progress);
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
    return await collectAnswer(
      query(arg as Parameters<typeof query>[0]),
      onDelta,
      progress,
    );
  } catch (e) {
    // Capaciteitsgrens (#155) onvertaald doorgeven: server.ts maakt er een
    // 429 met machine-leesbare reden van.
    if (e instanceof ConcurrencyLimitError) throw e;
    // Timeout en client-abort herkenbaar maken voor de aanroeper (run_log);
    // overige fouten ongewijzigd doorgeven — server.ts vertaalt ze naar een
    // nette 500 of een error-frame.
    if (timedOut && timeoutMs) {
      throw new Error(
        `${task}-call afgebroken na ${timeoutMs / 1000}s (harde timeout)`,
      );
    }
    if (controller.signal.aborted) {
      throw new Error("aanroep afgebroken: client heeft de verbinding gesloten");
    }
    throw e;
  } finally {
    if (timer) clearTimeout(timer);
    signal?.removeEventListener("abort", onAbort);
    unhookWarmAbort?.();
    release?.();
  }
}
