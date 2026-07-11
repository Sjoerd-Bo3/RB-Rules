import { query, type Options } from "@anthropic-ai/claude-agent-sdk";

// Auth: CLAUDE_CODE_OAUTH_TOKEN (abonnement) of ANTHROPIC_API_KEY.
// Laat ANTHROPIC_API_KEY leeg bij abonnementsgebruik — die wint stilletjes.
export type Task = "cheap" | "hard" | "research";

export interface AskImage {
  mediaType: string; // image/jpeg | image/png | image/webp | image/gif
  data: string; // base64
}

const MODEL: Record<Task, string> = {
  cheap: "claude-sonnet-4-6",
  hard: "claude-opus-4-8",
  research: "claude-sonnet-4-6", // web-werk is zoek+samenvat: Sonnet volstaat (kosten, #42)
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

/** Streaming-input met content-blocks — nodig zodra er afbeeldingen meegaan. */
async function* userMessage(prompt: string, images: AskImage[]) {
  yield {
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

/** Stuur één prompt (optioneel met afbeeldingen) naar Claude.
 *
 * Met `onDelta` (#31, streaming) levert de Agent SDK naast de gewone
 * berichten ook partial-message-events (`includePartialMessages`); elke
 * text-delta gaat direct naar de callback zodat de aanroeper het antwoord
 * woord-voor-woord kan doorsturen. De return-waarde blijft in beide gevallen
 * het volledige eindantwoord — de niet-streamende route verandert niet. */
export async function askClaude(opts: {
  prompt: string;
  system?: string;
  task?: Task;
  images?: AskImage[];
  onDelta?: (text: string) => void | Promise<void>;
  signal?: AbortSignal;
}): Promise<string> {
  const { prompt, system, task = "cheap", images = [], onDelta, signal } = opts;
  const research = task === "research";

  const systemPrompt = research
    ? [system, RESEARCH_CONTRACT].filter(Boolean).join("\n\n")
    : system;

  // Eén AbortController voor álle taken (review #31): server.ts koppelt er
  // de client-verbinding aan (`signal`), zodat een weggelopen client de
  // Claude-call afbreekt in plaats van hem op abonnementskosten af te laten
  // maken. De harde timeout blijft research-only: de andere taken zijn één
  // beurt en kennen dat risico niet.
  const controller = new AbortController();
  let timedOut = false;
  const timer = research
    ? setTimeout(() => {
        timedOut = true;
        controller.abort();
      }, RESEARCH_TIMEOUT_MS)
    : undefined;
  const onAbort = () => controller.abort();
  if (signal?.aborted) controller.abort();
  else signal?.addEventListener("abort", onAbort, { once: true });

  const options: Options = {
    model: MODEL[task],
    maxTurns: research ? RESEARCH_MAX_TURNS : 1,
    // Basis-toolset: leeg voor cheap/hard (puur prompt→tekst), alleen de
    // web-tools voor research.
    tools: research ? RESEARCH_TOOLS : [],
    ...(research
      ? {
          // Headless: web-tools vooraf goedkeuren; al het overige wordt
          // geweigerd in plaats van op een prompt te blijven hangen.
          allowedTools: RESEARCH_TOOLS,
          permissionMode: "dontAsk" as const,
        }
      : {}),
    abortController: controller,
    ...(systemPrompt ? { systemPrompt } : {}),
    // Streaming (#31): partial messages alleen aanzetten als er een
    // delta-afnemer is — anders blijft het berichtenverkeer zoals het was.
    ...(onDelta ? { includePartialMessages: true } : {}),
  };

  const arg = {
    prompt: images.length > 0 ? userMessage(prompt, images) : prompt,
    options,
  };

  // De Agent SDK levert dezelfde tekst twee keer: als streaming 'assistant'-
  // berichten én als afsluitend 'result'-bericht. Tel ze NIET op — verzamel
  // apart en geef het 'result' terug; val terug op assistant-tekst zonder result.
  let assistantText = "";
  let resultText = "";
  try {
    for await (const message of query(arg as Parameters<typeof query>[0])) {
      const m = message as {
        type: string;
        text?: string;
        result?: string;
        message?: { content?: Array<{ type: string; text?: string }> };
        event?: { type?: string; delta?: { type?: string; text?: string } };
      };
      if (m.type === "stream_event") {
        // Partial-message-event: alleen echte text-deltas doorgeven; het
        // volledige antwoord komt daarnaast gewoon als assistant/result
        // binnen (dus hier NIET aan assistantText/resultText toevoegen).
        const ev = m.event;
        if (
          onDelta &&
          ev?.type === "content_block_delta" &&
          ev.delta?.type === "text_delta" &&
          ev.delta.text
        )
          await onDelta(ev.delta.text);
      } else if (m.type === "assistant" && Array.isArray(m.message?.content)) {
        for (const block of m.message.content) {
          if (block.type === "text" && block.text) assistantText += block.text;
        }
      } else if (m.type === "text" && m.text) {
        assistantText += m.text;
      } else if (m.type === "result" && m.result) {
        resultText += m.result;
      }
    }
  } catch (e) {
    // Timeout en client-abort herkenbaar maken voor de aanroeper (run_log);
    // overige fouten ongewijzigd doorgeven — server.ts vertaalt ze naar een
    // nette 500 of een error-frame.
    if (timedOut) {
      throw new Error(
        `research-call afgebroken na ${RESEARCH_TIMEOUT_MS / 1000}s (harde timeout)`,
      );
    }
    if (controller.signal.aborted) {
      throw new Error("aanroep afgebroken: client heeft de verbinding gesloten");
    }
    throw e;
  } finally {
    if (timer) clearTimeout(timer);
    signal?.removeEventListener("abort", onAbort);
  }
  return (resultText || assistantText).trim();
}
