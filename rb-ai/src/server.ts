// rb-ai: interne AI-sidecar. Bezit de Claude Agent SDK- en Codex SDK/CLI-
// runtimes plus hun geïsoleerde credentials; rb-api (.NET) ziet alleen dit
// smalle HTTP-contract. Alleen compose-intern bereikbaar — nooit publiek.
import { createServer } from "node:http";
import { askClaude, extractWithTool, providerRegistry, warmPool } from "./ai.js";
import { aiSemaphore, ConcurrencyLimitError } from "./concurrency.js";
import {
  extractFailureResponse,
  failureOf,
  logCall,
  logEvent,
  safeDetail,
  type AiFailure,
} from "./failure.js";
import { buildAuditExtraction, parseInteractionAuditRequest } from "./audit.js";
import {
  buildInteractionToolShape,
  buildPredicateToolShape,
  enforceInteractionVocabulary,
  enforcePredicateVocabulary,
  INTERACTION_TOOL_ADDENDUM,
  interactionPromptText,
  interactionToolDescription,
  parseInteractionExtractRequest,
  parsePredicateExtractRequest,
  PREDICATE_TOOL_ADDENDUM,
  predicatePromptText,
  predicateToolDescription,
} from "./extract.js";
import { splitRelationProposals } from "./relations.js";
import { parseAskRequest } from "./validate.js";
import { controlHttpHandler } from "./control/http.js";

/** Test-seam (#312-review): de SDK-gedreven extractie als vervangbaar veld.
 *
 * Waarom dit bestaat: de vocabulaire-narekening (`enforce*Vocabulary`) is puur
 * en uitputtend getest, maar tot deze seam draaide geen enkele test de
 * BEDRADING — vervang de narekening op de call-site door een pass-through en
 * alles bleef groen. Dat is de #292-klasse: een gedragstest op de poort ziet
 * per definitie niet dat de call-site hem overslaat. In het enum-tijdperk kón
 * dat niet (`buildInteractionToolShape(parsed.request)` bestond niet zonder
 * request); sinds de vaste tool-vorm hangt de gesloten-vraag-regel aan één
 * regel in dit bestand, en die regel hoort bewaakt. server.test.ts stubt dit
 * veld en draait de échte handler — zie daar voor de twee richtingen. */
export const deps = { extractWithTool };

const PORT = Number(process.env.PORT ?? 8090);

/** Endpoints die één ai_call-regel per aanroep verdienen (#281). `/health` en
 * `/prewarm` staan er bewust NIET bij: die worden geregeld gepolld en zouden
 * het signaal onder duizenden regels ruis begraven — het gaat om de aanroepen
 * die een LLM-run doen. */
const LOGGED_PATHS = new Set([
  "/ask",
  "/ask/stream",
  "/extract/interactions",
  "/extract/predicates",
  "/audit/interaction",
]);

/** De request-body als JSON én de GROOTTE ervan in bytes (#281). De grootte is
 * diagnostisch goud en verklapt niets: ze zegt hoe zwaar een aanroep was zonder
 * één teken prompt-inhoud te loggen (werkafspraak 7). Zonder deze meting is de
 * vraag "vallen juist de grote payloads om?" alleen achteraf te reconstrueren
 * uit de rb-api-kant; met de meting staat het antwoord in de logregel zelf. */
async function readJson(
  req: import("node:http").IncomingMessage,
): Promise<{ value: unknown; bytes: number }> {
  const chunks: Buffer[] = [];
  for await (const c of req) chunks.push(c as Buffer);
  const raw = Buffer.concat(chunks);
  try {
    return { value: JSON.parse(raw.toString("utf8")) as unknown, bytes: raw.length };
  } catch {
    return { value: {}, bytes: raw.length };
  }
}

export const server = createServer(async (req, res) => {
  const startedAt = Date.now();
  const path = (req.url ?? "").split("?")[0];

  // Eén regel per LLM-aanroep (#281). Dit is de kern van de issue: rb-ai logde
  // sinds de start letterlijk één regel ("rb-ai luistert op :8090") terwijl er
  // 22 aanroepen faalden, dus de oorzaak van 55% uitval was van buitenaf niet
  // vast te stellen. `note` vuurt precies één keer per aanroep, ook als er
  // onderweg meerdere foutpaden langskomen.
  let logged = false;
  // Payload-context van deze aanroep: GROOTTES en AANTALLEN, nooit inhoud
  // (werkafspraak 7). Wordt gevuld zodra de body gelezen en gevalideerd is.
  let shape: {
    bytes?: number;
    refs?: number;
    sections?: number;
    items?: number;
    task?: string;
    rejected?: number;
    rejectedConditions?: number;
    provider?: string;
    model?: string;
    inputTokens?: number;
    outputTokens?: number;
    costUsd?: number;
  } = {};
  const note = (status: number, failure?: AiFailure) => {
    if (logged || !LOGGED_PATHS.has(path)) return;
    logged = true;
    logCall({
      endpoint: path,
      ms: Date.now() - startedAt,
      status,
      outcome: status >= 400 ? "error" : "ok",
      reason: failure?.reason,
      detail: failure?.detail,
      ...shape,
    });
  };

  const send = (status: number, body: unknown, failure?: AiFailure) => {
    note(status, failure);
    if (res.destroyed) return res; // client al weg — niets meer te sturen
    res.writeHead(status, { "content-type": "application/json" });
    return res.end(JSON.stringify(body));
  };

  /** Foutbody mét machine-leesbare reden (#281). rb-api's `RbAiClient` leest
   * `reason` en telt hem mee in de per-oorzaak-uitsplitsing van #251, zodat de
   * oorzaak in het run-detail staat in plaats van alleen in de containerlog.
   * Oudere aanroepers zien gewoon het vertrouwde `error`-veld. */
  const errorBody = (message: string, failure: AiFailure) => ({
    // Ook `error` gaat door de redactie (werkafspraak 7): dit veld draagt bij
    // een geworpen fout de rauwe SDK-melding, en die reist het compose-netwerk
    // over naar rb-api's logger.
    error: safeDetail(message),
    reason: failure.reason,
    detail: failure.detail,
  });

  /** Uitval van een extractie-endpoint. De beslissing zelf woont in
   * `extractFailureResponse` (puur, gedragsgetest); hier alleen de bedrading. */
  const extractMeta = (outcome: {
    provider?: string;
    model?: string;
    usage?: { inputTokens: number; outputTokens: number; unit: "tokens"; costUsd?: number } | null;
  }) => ({
    ...(outcome.provider ? { provider: outcome.provider } : {}),
    ...(outcome.model ? { model: outcome.model } : {}),
    usage: outcome.usage ?? null,
  });

  const noteExtractMeta = (outcome: {
    provider?: string;
    model?: string;
    usage?: { inputTokens: number; outputTokens: number; costUsd?: number } | null;
  }) => {
    shape = {
      ...shape,
      provider: outcome.provider,
      model: outcome.model,
      inputTokens: outcome.usage?.inputTokens,
      outputTokens: outcome.usage?.outputTokens,
      costUsd: outcome.usage?.costUsd,
    };
  };

  const sendExtractFailure = (outcome: {
    failure?: AiFailure;
    timedOut?: boolean;
    provider?: string;
    model?: string;
    usage?: { inputTokens: number; outputTokens: number; unit: "tokens"; costUsd?: number } | null;
  }) => {
    noteExtractMeta(outcome);
    const { status, error, code, failure } = extractFailureResponse(outcome);
    const body = { ...errorBody(error, failure), ...extractMeta(outcome) };
    return send(status, code ? { ...body, code } : body, failure);
  };

  /** Geslaagde extractie. Vuurde de tool nog vóór de tijdslimiet maar werd de
   * run daarna afgekapt, dan gaan de kandidaten gewoon mee (200 — weggooien zou
   * geldig werk vernietigen), maar de logregel MOET de afkapping vermelden
   * (#281-review): anders vallen juist de traagste kaarten uit de schaalklip-
   * meting weg, en dat is precies de meting waarvoor deze PR bestaat.
   *
   * Gevolg voor de logregel: `outcome` blijft `ok` (er kwam bruikbaar werk uit)
   * terwijl `reason` op `timeout` staat. Dat is geen tegenspraak maar twee
   * verschillende vragen, en het maakt beide greps kloppend:
   *   `grep '"outcome":"error"'`  → wat er misging
   *   `grep '"reason":"timeout"'` → alles wat tegen de tijdslimiet aan liep,
   *                                 geslaagd of niet. */
  const sendExtractSuccess = (
    outcome: {
      failure?: AiFailure;
      timedOut?: boolean;
      provider?: string;
      model?: string;
      usage?: { inputTokens: number; outputTokens: number; unit: "tokens"; costUsd?: number } | null;
    },
    body: unknown,
  ) => {
    noteExtractMeta(outcome);
    return send(
      200,
      { ...(body as Record<string, unknown>), ...extractMeta(outcome) },
      outcome.timedOut ? outcome.failure : undefined,
    );
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
    if (await controlHttpHandler(req, res)) return;
    if (req.method === "GET" && req.url === "/health") {
      const configured = providerRegistry.list().some((provider) => provider.configured());
      // capacity (#155) en warm (#154): tellers om de cap en de pool op
      // echte cijfers bij te stellen — rb-api leest /health al best-effort.
      return send(200, {
        status: "ok",
        service: "rb-ai",
        configured,
        capacity: aiSemaphore.snapshot(),
        warm: warmPool.stats(),
        providers: Object.fromEntries(
          providerRegistry.list().map((provider) => [provider.id, provider.configured()]),
        ),
        providerAccounts: Object.fromEntries(
          providerRegistry.list()
            .filter((provider) => provider.health)
            .map((provider) => [provider.id, provider.health!()]),
        ),
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
      const body = await readJson(req);
      const parsed = parseAskRequest(body.value);
      if (!parsed.ok) return send(400, { error: parsed.error });
      shape = { bytes: body.bytes, task: parsed.request.task };
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
          return send(
            429,
            { error: e.message, code: e.code },
            { reason: "concurrency_limit", detail: e.message },
          );
        // Agentic faalt/timeout (#107): de tool-calls die vóór de uitval al
        // gedaan waren gaan mee in de fout-body — juist de hangende run wil
        // de beheerder in de trace kunnen inspecteren. Overige taken volgen
        // het bestaande pad (outer catch → 500 {error}).
        if (agentic) {
          const failure = failureOf(e);
          return send(500, { ...errorBody(String(e), failure), steps }, failure);
        }
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
      const body = await readJson(req);
      const parsed = parseAskRequest(body.value);
      if (!parsed.ok) return send(400, { error: parsed.error });
      shape = { bytes: body.bytes, task: parsed.request.task };
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
        note(200);
      } catch (e) {
        if (!headSent && e instanceof ConcurrencyLimitError)
          return send(
            429,
            { error: e.message, code: e.code },
            { reason: "concurrency_limit", detail: e.message },
          );
        // Uitval mídden in de stream: de 200 is al weg, dus de fout gaat als
        // frame mee — de aanroeper (rb-api) degradeert daarop netjes. Is de
        // client zelf weggelopen (abort), dan is het frame een no-op. De reden
        // gaat mee in het frame én in de logregel (#281): een half-gestreamde
        // uitval was voorheen het slechtst zichtbare pad dat er was.
        const failure = failureOf(e);
        frame({ type: "error", error: safeDetail(String(e)), reason: failure.reason });
        // De head is mogelijk al als 200 de deur uit; de UITKOMST is een fout,
        // en dat is wat de logregel moet vertellen.
        note(500, failure);
      }
      return res.end();
    }

    if (req.method === "POST" && req.url === "/extract/interactions") {
      // Tool-forced brein-extractie (#226, §3.1): gegeven kaart/regel-tekst + het
      // ontologie-vocabulaire levert de agent gestructureerde interactie-kandidaten
      // via een geforceerde tool-call. rb-api (mining-orkestratie) draait ze door de
      // fase-2-promotie-poort. Uitval → 500 → RbAiClient degradeert naar null.
      const body = await readJson(req);
      const parsed = parseInteractionExtractRequest(body.value);
      if (!parsed.ok) return send(400, { error: parsed.error });
      // refs/sections = de omvang van het aangeboden vocabulaire; samen met
      // bytes maakt dat de vraag "vallen juist de grote aanbiedingen om?"
      // direct toetsbaar. sections telt sinds #315 mee: de sectie-refs zijn
      // vocabulaire dat met de kennisbank meegroeit (#281/#288-schaalklip).
      shape = {
        bytes: body.bytes,
        refs: parsed.request.refs.length,
        ...(parsed.request.sections.length > 0
          ? { sections: parsed.request.sections.length }
          : {}),
      };
      try {
        const outcome = await deps.extractWithTool({
          toolName: "emit_interactions",
          // Vaste tool-vorm (#312): description en schema zijn constanten; het
          // vocabulaire reist als prompt-invoer mee en wordt hieronder
          // deterministisch nagerekend (de gesloten-vraag-regel uit CLAUDE.md).
          description: interactionToolDescription(),
          schema: buildInteractionToolShape(),
          resultKey: "interactions",
          system: parsed.request.system,
          addendum: INTERACTION_TOOL_ADDENDUM,
          text: interactionPromptText(parsed.request),
          model: parsed.request.model,
          signal: abort.signal,
        });
        // null = tool niet geroepen / run gefaald: geef een 500 zodat rb-api dit als
        // AI-uitval leest (null, nette degradatie) i.p.v. als "geen kandidaten".
        // Sinds #281 draagt die 500 de REDEN — dit was het endpoint waar 22 van
        // de 40 mining-kaarten spoorloos op strandden.
        if (outcome.items === null) return sendExtractFailure(outcome);
        // De narekening (#312): geen term buiten het aangeboden vocabulaire
        // verlaat rb-ai. De weigeringen gaan als MAAT mee in de logregel —
        // "hoe vaak kleurt het model buiten het lijstje" is precies de meting
        // die de generieke tool-vorm moet bewaken.
        const gate = enforceInteractionVocabulary(outcome.items, parsed.request);
        shape = {
          ...shape,
          items: gate.accepted.length,
          ...(gate.rejected > 0 ? { rejected: gate.rejected } : {}),
          ...(gate.rejectedConditions > 0
            ? { rejectedConditions: gate.rejectedConditions }
            : {}),
        };
        return sendExtractSuccess(outcome, { interactions: gate.accepted });
      } catch (e) {
        if (e instanceof ConcurrencyLimitError)
          return send(
            429,
            { error: e.message, code: e.code },
            { reason: "concurrency_limit", detail: e.message },
          );
        throw e;
      }
    }

    if (req.method === "POST" && req.url === "/extract/predicates") {
      // Tool-forced mechanic-predicaat-extractie (#226/#229, §5): getypeerde
      // (predicate, object) uit de regel-/definitietekst van één mechanic/keyword.
      const body = await readJson(req);
      const parsed = parsePredicateExtractRequest(body.value);
      if (!parsed.ok) return send(400, { error: parsed.error });
      shape = { bytes: body.bytes };
      try {
        const outcome = await deps.extractWithTool({
          toolName: "emit_mechanic_predicates",
          // Vaste tool-vorm (#312), zelfde snit als /extract/interactions:
          // subject + predicatenlijst als prompt-invoer, narekening hieronder.
          description: predicateToolDescription(),
          schema: buildPredicateToolShape(),
          resultKey: "predicates",
          system: parsed.request.system,
          addendum: PREDICATE_TOOL_ADDENDUM,
          text: predicatePromptText(parsed.request),
          model: parsed.request.model,
          signal: abort.signal,
        });
        if (outcome.items === null) return sendExtractFailure(outcome);
        const gate = enforcePredicateVocabulary(outcome.items, parsed.request);
        shape = {
          ...shape,
          items: gate.accepted.length,
          ...(gate.rejected > 0 ? { rejected: gate.rejected } : {}),
        };
        return sendExtractSuccess(outcome, { predicates: gate.accepted });
      } catch (e) {
        if (e instanceof ConcurrencyLimitError)
          return send(
            429,
            { error: e.message, code: e.code },
            { reason: "concurrency_limit", detail: e.message },
          );
        throw e;
      }
    }

    if (req.method === "POST" && req.url === "/audit/interaction") {
      // Steekproef-audit (#255): een STERKER model (task "hard") velt een gesloten,
      // tool-forced oordeel over één gepromoveerde interactie — correct? gedragen
      // door het bewijs? Zelfde semaphore/timeout/failure-discipline als de
      // extract-endpoints (extractWithTool: achtergrond-prioriteit, harde timeout,
      // result-bericht + api_retry gelezen); rb-api legt het oordeel vast als
      // audit-regel met eigen provenance en verandert er nooit zelf een tier mee.
      const body = await readJson(req);
      const parsed = parseInteractionAuditRequest(body.value);
      if (!parsed.ok) return send(400, { error: parsed.error });
      // De volledige aanroep komt uit de PURE builder (#255-review): dáár ligt
      // de task-"hard"-bedrading vast en dáár wordt ze op gedrag getest — een
      // inline optieobject hier was precies het onbewaakte pad waarlangs de
      // audit stil op het cheap-model kon terugvallen met valse provenance.
      const extraction = buildAuditExtraction(parsed.request, abort.signal);
      shape = { bytes: body.bytes, task: extraction.task };
      try {
        const outcome = await deps.extractWithTool(extraction);
        if (outcome.items === null) return sendExtractFailure(outcome);
        shape = { ...shape, items: outcome.items.length };
        return sendExtractSuccess(outcome, { verdicts: outcome.items });
      } catch (e) {
        if (e instanceof ConcurrencyLimitError)
          return send(
            429,
            { error: e.message, code: e.code },
            { reason: "concurrency_limit", detail: e.message },
          );
        throw e;
      }
    }

    return send(404, { error: "not found" });
  } catch (e) {
    const failure = failureOf(e);
    // Na writeHead kan er geen JSON-foutstatus meer; dan alleen netjes sluiten
    // — de logregel gaat wél de deur uit, want juist dit pad was blind.
    if (res.headersSent) {
      note(500, failure);
      return res.end();
    }
    if (e instanceof ConcurrencyLimitError)
      return send(
        429,
        { error: e.message, code: e.code },
        { reason: "concurrency_limit", detail: e.message },
      );
    return send(500, errorBody(String(e), failure), failure);
  }
});

server.listen(PORT, () => {
  // Ook de opstartregel door de poort (#292), zodat élke regel in
  // `docker logs rb-v2-ai` dezelfde parseerbare JSON-vorm heeft en er geen
  // tweede stdout-pad bestaat dat de redactie kan missen.
  logEvent("startup", { port: PORT });
});
