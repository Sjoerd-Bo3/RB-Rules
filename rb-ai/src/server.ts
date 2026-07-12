// rb-ai: interne AI-sidecar. Draait de Claude Agent SDK op het abonnement
// (CLAUDE_CODE_OAUTH_TOKEN) zodat rb-api (.NET) geen per-token API-key nodig
// heeft. Alleen bereikbaar binnen het compose-netwerk — nooit publiek exposen.
import { createServer } from "node:http";
import { askClaude } from "./ai.js";
import { parseAskRequest } from "./validate.js";

const PORT = Number(process.env.PORT ?? 8090);

async function readJson(req: import("node:http").IncomingMessage): Promise<unknown> {
  const chunks: Buffer[] = [];
  for await (const c of req) chunks.push(c as Buffer);
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8")) as unknown;
  } catch {
    return {};
  }
}

const server = createServer(async (req, res) => {
  const send = (status: number, body: unknown) => {
    if (res.destroyed) return res; // client al weg — niets meer te sturen
    res.writeHead(status, { "content-type": "application/json" });
    return res.end(JSON.stringify(body));
  };

  // Weggelopen client = Claude-call afbreken (review #31): zonder deze
  // koppeling maakt de sidecar elke geannuleerde vraag gewoon af en schrijft
  // de deltas het niets in — een volledig gelekte LLM-call per afgebroken
  // stream. 'close' vuurt óók na een normale afronding; alleen een respons
  // die nog niet netjes geëindigd is (writableEnded=false) betekent een
  // voortijdig vertrokken client.
  const abort = new AbortController();
  res.on("close", () => {
    if (!res.writableEnded) abort.abort();
  });

  try {
    if (req.method === "GET" && req.url === "/health") {
      const configured = Boolean(
        process.env.CLAUDE_CODE_OAUTH_TOKEN || process.env.ANTHROPIC_API_KEY,
      );
      return send(200, { status: "ok", service: "rb-ai", configured });
    }

    if (req.method === "POST" && req.url === "/ask") {
      // task="research" is de enige taak met web-toegang (WebSearch/WebFetch,
      // opt-in per call — #64); task="agentic" (#106) krijgt alléén de interne
      // brein-tools (MCP → rb-api, zie ai.ts/brain-tools.ts).
      const parsed = parseAskRequest(await readJson(req));
      if (!parsed.ok) return send(400, { error: parsed.error });
      // Brein-stappen (#107): alléén bij task="agentic" gaan de tool-calls
      // als `steps` mee terug (rb-api legt ze vast in AskTrace.BrainSteps);
      // de respons van alle andere taken blijft byte-gelijk ({answer}).
      const agentic = parsed.request.task === "agentic";
      const steps: string[] = [];
      const answer = await askClaude({
        ...parsed.request,
        signal: abort.signal,
        ...(agentic ? { onBrainStep: (s: string) => steps.push(s) } : {}),
      });
      return send(200, agentic ? { answer, steps } : { answer });
    }

    if (req.method === "POST" && req.url === "/ask/stream") {
      // Streaming-variant (#31): NDJSON — één JSON-object per regel. Chunked
      // is het eenvoudigste dat node:http én de Agent SDK van nature doen;
      // geen SSE-framing nodig omdat de afnemer (rb-api) geen EventSource is.
      // Frames: {type:"delta",text} … {type:"done",answer} | {type:"error",error}.
      const parsed = parseAskRequest(await readJson(req));
      if (!parsed.ok) return send(400, { error: parsed.error });
      res.writeHead(200, { "content-type": "application/x-ndjson" });
      const frame = (obj: unknown) => res.write(JSON.stringify(obj) + "\n");
      try {
        const answer = await askClaude({
          ...parsed.request,
          signal: abort.signal,
          onDelta: (text) => {
            frame({ type: "delta", text });
          },
        });
        frame({ type: "done", answer });
      } catch (e) {
        // Uitval mídden in de stream: de 200 is al weg, dus de fout gaat als
        // frame mee — de aanroeper (rb-api) degradeert daarop netjes. Is de
        // client zelf weggelopen (abort), dan is het frame een no-op.
        frame({ type: "error", error: String(e) });
      }
      return res.end();
    }

    return send(404, { error: "not found" });
  } catch (e) {
    // Na writeHead kan er geen JSON-foutstatus meer; dan alleen netjes sluiten.
    if (res.headersSent) return res.end();
    return send(500, { error: String(e) });
  }
});

server.listen(PORT, () => {
  console.log(`rb-ai luistert op :${PORT}`);
});
