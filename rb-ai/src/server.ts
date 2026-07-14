// rb-ai: interne AI-sidecar. Draait de Claude Agent SDK op het abonnement
// (CLAUDE_CODE_OAUTH_TOKEN) zodat rb-api (.NET) geen per-token API-key nodig
// heeft. Alleen bereikbaar binnen het compose-netwerk — nooit publiek exposen.
import { createServer } from "node:http";
import { askClaude, warmPool } from "./ai.js";
import { aiSemaphore, ConcurrencyLimitError } from "./concurrency.js";
import { splitRelationProposals } from "./relations.js";
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
      // capacity (#155) en warm (#154): tellers om de cap en de pool op
      // echte cijfers bij te stellen — rb-api leest /health al best-effort.
      return send(200, {
        status: "ok",
        service: "rb-ai",
        configured,
        capacity: aiSemaphore.snapshot(),
        warm: warmPool.stats(),
      });
    }

    if (req.method === "POST" && req.url === "/prewarm") {
      // Voorverwarmsignaal (#154), gestuurd bij het laden van de /ask-pagina
      // (rb-web → rb-api → hier). Idempotent en altijd direct een 202: het
      // opent/verlengt het activiteitsvenster en boot hooguit één warme
      // sessie op de achtergrond — er wordt nooit op de boot gewacht.
      const result = warmPool.prewarm();
      return send(202, { status: "accepted", ...result });
    }

    if (req.method === "POST" && req.url === "/ask") {
      // task="research" is de enige taak met web-toegang (WebSearch/WebFetch,
      // opt-in per call — #64); task="agentic" (#106) krijgt alléén de interne
      // brein-tools (MCP → rb-api, zie ai.ts/brain-tools.ts).
      const parsed = parseAskRequest(await readJson(req));
      if (!parsed.ok) return send(400, { error: parsed.error });
      // Brein-stappen (#107): alléén bij task="agentic" gaan de tool-calls
      // als `steps` mee terug (rb-api legt ze vast in AskTrace.BrainSteps).
      // Elke taak krijgt daarnaast `usage` (#121): de echte token-tellingen
      // van de run (null als de SDK ze niet meegaf) — rb-api leest ze
      // best-effort, dus een oudere aanroeper negeert het veld gewoon.
      const agentic = parsed.request.task === "agentic";
      const steps: string[] = [];
      try {
        const { answer, usage } = await askClaude({
          ...parsed.request,
          signal: abort.signal,
          ...(agentic ? { onBrainStep: (s: string) => steps.push(s) } : {}),
        });
        if (!agentic) return send(200, { answer, usage });
        // Relatievoorstellen (#120): het addendum laat de agent ontdekte
        // verbanden ná het antwoord melden; dat blok gaat als eigen veld
        // `relations` naast `steps` mee (rb-api parseert en valideert het,
        // gedeelde LlmJson) en verdwijnt uit het antwoord dat de gebruiker
        // ziet. Zonder blok is de respons byte-gelijk aan voorheen.
        const split = splitRelationProposals(answer);
        return send(200, {
          answer: split.answer,
          steps,
          usage,
          ...(split.relations ? { relations: split.relations } : {}),
        });
      } catch (e) {
        // Capaciteitsgrens (#155): nette 429 met machine-leesbare reden —
        // rb-api's RbAiClient behandelt elke non-success als "AI weg" en
        // degradeert naar de bestaande vriendelijke melding.
        if (e instanceof ConcurrencyLimitError)
          return send(429, { error: e.message, code: e.code });
        // Agentic faalt/timeout (#107): de tool-calls die vóór de uitval al
        // gedaan waren gaan mee in de fout-body — juist de hangende run wil
        // de beheerder in de trace kunnen inspecteren. Overige taken volgen
        // het bestaande pad (outer catch → 500 {error}).
        if (agentic) return send(500, { error: String(e), steps });
        throw e;
      }
    }

    if (req.method === "POST" && req.url === "/ask/stream") {
      // Streaming-variant (#31): NDJSON — één JSON-object per regel. Chunked
      // is het eenvoudigste dat node:http én de Agent SDK van nature doen;
      // geen SSE-framing nodig omdat de afnemer (rb-api) geen EventSource is.
      // Frames: {type:"delta",text} … {type:"done",answer,usage} | {type:"error",error}.
      // Het slotframe draagt de token-usage van de run (#121, null bij
      // ontbreken) — dezelfde best-effort-doorgifte als op /ask.
      const parsed = parseAskRequest(await readJson(req));
      if (!parsed.ok) return send(400, { error: parsed.error });
      // De 200 + NDJSON-header gaat pas de deur uit bij het eerste frame
      // (#155): zo kan een capaciteits-afwijzing — die altijd vóór de eerste
      // delta valt — nog als echte 429 terug, het pad dat RbAiClient al als
      // degradatie herkent. Ná de eerste byte blijft het error-frame-contract
      // ongewijzigd.
      let headSent = false;
      const frame = (obj: unknown) => {
        if (res.destroyed) return;
        if (!headSent) {
          headSent = true;
          res.writeHead(200, { "content-type": "application/x-ndjson" });
        }
        res.write(JSON.stringify(obj) + "\n");
      };
      try {
        const { answer, usage } = await askClaude({
          ...parsed.request,
          signal: abort.signal,
          onDelta: (text) => {
            frame({ type: "delta", text });
          },
        });
        frame({ type: "done", answer, usage });
      } catch (e) {
        if (!headSent && e instanceof ConcurrencyLimitError)
          return send(429, { error: e.message, code: e.code });
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
    if (e instanceof ConcurrencyLimitError)
      return send(429, { error: e.message, code: e.code });
    return send(500, { error: String(e) });
  }
});

server.listen(PORT, () => {
  console.log(`rb-ai luistert op :${PORT}`);
});
