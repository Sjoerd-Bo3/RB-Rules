import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { type Options } from "@anthropic-ai/claude-agent-sdk";
import {
  askClaude,
  buildQueryOptions,
  buildUserMessage,
  collectAnswer,
  noteBrainStep,
  warmBootOptions,
  type AskQueryRunner,
  type CollectProgress,
} from "./ai.js";
import { failureOf, logCall, StderrTail } from "./failure.js";
import { pushableInput, WarmPool, type WarmBootHandle } from "./warmpool.js";

async function* stream(...messages: unknown[]) {
  for (const m of messages) yield m;
}

/** Wegwerp-sink: elke aanroep hoort zijn eigen staart te hebben, dus een test
 * die opties bouwt maakt er ook telkens een verse. */
const sink = () => (_data: string) => {};

// ── Contract warm ⇔ koud (#154): byte-voor-byte dezelfde query-opties ──────
// De warme pool boot met warmBootOptions; het koude pad bouwt via
// buildQueryOptions. Drift tussen die twee zou betekenen dat een warme claim
// een ándere call doet dan het koude pad zou doen — deze tests zijn de rem.

test("warm en koud bouwen byte-gelijke opties voor dezelfde cheap-signatuur", () => {
  const controller = new AbortController();
  for (const sig of [
    { systemPrompt: "rewrite-systeemprompt", includePartialMessages: false },
    { systemPrompt: "antwoord-systeemprompt", includePartialMessages: true },
    { systemPrompt: undefined, includePartialMessages: false },
  ]) {
    // In productie zijn dit twee VERSCHILLENDE staarten (elke sessie de zijne,
    // #300) — dus de test geeft ze hier ook expres niet dezelfde functie mee.
    const warm = warmBootOptions(sig, controller, sink());
    const cold = buildQueryOptions({
      task: "cheap",
      systemPrompt: sig.systemPrompt,
      includePartialMessages: sig.includePartialMessages,
      controller,
      stderr: sink(),
    });

    // De VELDEN moeten identiek zijn: welke opties er wél en niet gezet worden
    // bepaalt hoe het subprocess gespawnd wordt en welke API-call eruit volgt.
    // Dit is de kern van het contract en het deel dat #300 niet mocht raken.
    assert.deepEqual(Object.keys(warm).sort(), Object.keys(cold).sort());

    // De WAARDEN byte-voor-byte, op de twee velden na die per sessie uniek
    // ZIJN en dat ook horen te zijn: de AbortController (al zo sinds #154) en
    // sinds #300 de stderr-sink. Zou die laatste gedeeld worden, dan schreven
    // warm en koud in dezelfde buffer en was elke staart bij gelijktijdige
    // calls onbruikbaar — gelijkheid is hier dus juist de BUG.
    const normalise = (o: object) =>
      JSON.stringify({ ...o, abortController: undefined, stderr: undefined });
    assert.equal(normalise(warm), normalise(cold));
    assert.deepEqual(
      { ...warm, abortController: undefined, stderr: undefined },
      { ...cold, abortController: undefined, stderr: undefined },
    );

    // Aanwezig én per sessie eigen. Zonder de eerste assert spawnt de SDK het
    // subprocess met stderr op "ignore" en is er niets te diagnosticeren;
    // zonder de tweede is de attributie stuk.
    assert.equal(typeof warm.stderr, "function", "warme boot mist een stderr-sink");
    assert.equal(typeof cold.stderr, "function", "koud pad mist een stderr-sink");
    assert.notEqual(warm.stderr, cold.stderr, "warm en koud delen één stderr-buffer");
  }
});

test("cheap-opties: exact het vertrouwde koude pad (golden)", () => {
  const controller = new AbortController();
  const stderr = sink();
  assert.deepEqual(
    buildQueryOptions({ task: "cheap", includePartialMessages: false, controller, stderr }),
    {
      model: "claude-sonnet-4-6",
      maxTurns: 1,
      tools: [],
      abortController: controller,
      stderr,
    },
  );
  assert.deepEqual(
    buildQueryOptions({
      task: "cheap",
      systemPrompt: "S",
      includePartialMessages: true,
      controller,
      stderr,
    }),
    {
      model: "claude-sonnet-4-6",
      maxTurns: 1,
      tools: [],
      abortController: controller,
      systemPrompt: "S",
      includePartialMessages: true,
      stderr,
    },
  );
});

test("research-opties: web-tools, dontAsk en 16 beurten (golden)", () => {
  const controller = new AbortController();
  const stderr = sink();
  assert.deepEqual(
    buildQueryOptions({ task: "research", includePartialMessages: false, controller, stderr }),
    {
      model: "claude-sonnet-4-6",
      maxTurns: 16,
      tools: ["WebSearch", "WebFetch"],
      allowedTools: ["WebSearch", "WebFetch"],
      permissionMode: "dontAsk",
      abortController: controller,
      stderr,
    },
  );
});

test("agentic-opties: brein-MCP, allowlist en 8 beurten", () => {
  const controller = new AbortController();
  const o = buildQueryOptions({
    task: "agentic",
    includePartialMessages: false,
    controller,
    stderr: sink(),
  }) as Record<string, unknown>;
  assert.equal(o.model, "claude-sonnet-4-6");
  assert.equal(o.maxTurns, 8);
  assert.deepEqual(o.tools, []);
  assert.equal(o.permissionMode, "dontAsk");
  assert.ok(o.mcpServers && typeof o.mcpServers === "object");
  assert.ok(Array.isArray(o.allowedTools) && (o.allowedTools as string[]).length > 0);
  // Ook het agentic pad krijgt stderr (#300): juist daar loopt de langste run
  // met de meeste kans op een omgevallen subprocess.
  assert.equal(typeof o.stderr, "function");
});

test("elke taak bouwt opties MÉT stderr-sink — geen taak spawnt blind (#300)", () => {
  // Het gat van #300 was taak-onafhankelijk: buildQueryOptions zette de optie
  // voor niemand. Deze test loopt daarom alle taken langs in plaats van de
  // cheap-golden te vertrouwen.
  const controller = new AbortController();
  for (const task of ["cheap", "hard", "research", "agentic"] as const) {
    const o = buildQueryOptions({
      task,
      includePartialMessages: false,
      controller,
      stderr: sink(),
    });
    assert.equal(typeof o.stderr, "function", `task=${task} spawnt zonder stderr-opvang`);
  }
});

test("lege systemPrompt (undefined) zet het veld niet — signatuur-semantiek", () => {
  const controller = new AbortController();
  const o = buildQueryOptions({
    task: "cheap",
    includePartialMessages: false,
    controller,
    stderr: sink(),
  });
  assert.equal("systemPrompt" in o, false);
  assert.equal("includePartialMessages" in o, false);
});

// ── Model-sweep (#174): expliciete modeloverride op elke taak ──────────────
// De benchmark-sweep vraagt "zelfde retrieval + prompt, ander model" — de
// override moet dus MODEL[task] vervangen voor elk taaktype, niet alleen
// "cheap", en zonder override blijft het bestaande MODEL[task]-gedrag intact.

test("model-override vervangt MODEL[task] op elk taaktype (#174)", () => {
  const controller = new AbortController();
  for (const task of ["cheap", "hard", "research", "agentic"] as const) {
    const o = buildQueryOptions({
      task,
      includePartialMessages: false,
      controller,
      model: "claude-sonnet-5",
      stderr: sink(),
    });
    assert.equal(o.model, "claude-sonnet-5", `task=${task}`);
  }
});

test("zonder model-override blijft MODEL[task] ongewijzigd (#174)", () => {
  const controller = new AbortController();
  const o = buildQueryOptions({
    task: "hard",
    includePartialMessages: false,
    controller,
    stderr: sink(),
  });
  assert.equal(o.model, "claude-opus-4-8");
});

// ── buildUserMessage: het bericht dat de warme pool bij de claim pusht ─────

test("buildUserMessage: zelfde content-vorm als het koude streaming-input-pad", () => {
  const m = buildUserMessage("vraag", [{ mediaType: "image/png", data: "AAA" }]);
  assert.deepEqual(m, {
    type: "user",
    message: {
      role: "user",
      content: [
        { type: "image", source: { type: "base64", media_type: "image/png", data: "AAA" } },
        { type: "text", text: "vraag" },
      ],
    },
    parent_tool_use_id: null,
    session_id: "rb-ai",
  });
});

// ── collectAnswer: de gedeelde leeslus (koud én warm) ──────────────────────

test("collectAnswer: result wint van assistant-tekst en draagt de usage", async () => {
  const res = await collectAnswer(
    stream(
      { type: "system", subtype: "init" },
      {
        type: "assistant",
        message: { content: [{ type: "text", text: "assistent-tekst" }] },
      },
      {
        type: "result",
        result: "eindantwoord",
        usage: { input_tokens: 10, output_tokens: 5 },
      },
    ),
  );
  assert.equal(res.answer, "eindantwoord");
  assert.deepEqual(res.usage, { inputTokens: 10, outputTokens: 5 });
});

test("collectAnswer: zonder result valt hij terug op assistant-tekst, usage null", async () => {
  const res = await collectAnswer(
    stream({
      type: "assistant",
      message: { content: [{ type: "text", text: " deel-antwoord " }] },
    }),
  );
  assert.equal(res.answer, "deel-antwoord");
  assert.equal(res.usage, null);
});

test("collectAnswer: text-deltas gaan naar onDelta, niet dubbel in het antwoord", async () => {
  const deltas: string[] = [];
  const res = await collectAnswer(
    stream(
      {
        type: "stream_event",
        event: { type: "content_block_delta", delta: { type: "text_delta", text: "Ja" } },
      },
      {
        type: "stream_event",
        event: { type: "content_block_delta", delta: { type: "text_delta", text: ", mag" } },
      },
      { type: "result", result: "Ja, mag" },
    ),
    (t) => {
      deltas.push(t);
    },
  );
  assert.deepEqual(deltas, ["Ja", ", mag"]);
  assert.equal(res.answer, "Ja, mag");
});

test("collectAnswer: sawOutput blijft false bij alleen systeemberichten (dode warme sessie)", async () => {
  const progress: CollectProgress = { sawOutput: false };
  const res = await collectAnswer(
    stream({ type: "system", subtype: "init" }),
    undefined,
    progress,
  );
  assert.equal(progress.sawOutput, false, "geen output ⇒ ai.ts mag veilig koud herstarten");
  assert.equal(res.answer, "");
  const progress2: CollectProgress = { sawOutput: false };
  await collectAnswer(stream({ type: "result", result: "x" }), undefined, progress2);
  assert.equal(progress2.sawOutput, true);
});

// ── collectAnswer leest de SDK-uitval mee (#281) ───────────────────────────
// Dit is precies het gat waardoor 22 mislukte mining-aanroepen géén logregel
// achterlieten: de Agent SDK gooit niet bij een mislukte run, ze eindigt met
// een result-bericht met subtype/is_error/api_error_status/errors[]. Werd dat
// bericht alleen op `result`/`usage` gelezen, dan bleef er een leeg antwoord
// over zonder enig spoor van de oorzaak.

test("collectAnswer: een fout-result levert een failure op, geen stil leeg antwoord", async () => {
  const res = await collectAnswer(
    stream({
      type: "result",
      subtype: "error_max_turns",
      is_error: true,
      num_turns: 3,
      errors: [],
    }),
  );
  assert.equal(res.answer, "");
  assert.equal(res.failure?.reason, "max_turns");
  assert.match(res.failure!.detail, /turns=3/);
});

test("collectAnswer: een geslaagd result draagt géén failure", async () => {
  const res = await collectAnswer(
    stream({ type: "result", subtype: "success", is_error: false, result: "ok" }),
  );
  assert.equal(res.answer, "ok");
  assert.equal(res.failure, undefined);
});

test("collectAnswer: een API-fout op een run mét tekst blijft bruikbaar én zichtbaar", async () => {
  // Deelantwoord + foutstatus: het antwoord telt (askClaude gooit alleen bij
  // een LEGE uitkomst), maar de oorzaak gaat wél mee naar de logregel.
  const res = await collectAnswer(
    stream(
      { type: "assistant", message: { content: [{ type: "text", text: "deel" }] } },
      { type: "result", subtype: "success", is_error: true, api_error_status: 529 },
    ),
  );
  assert.equal(res.answer, "deel");
  assert.equal(res.failure?.reason, "api_error");
});

// ── Prewarm telt niet mee voor de concurrentie-cap (#154/#155) ────────────
// De boot die `/prewarm` triggert (bootWarmCheapSession, aangeroepen door
// WarmPool los van askClaude) mag nooit een permit uit `aiSemaphore`
// verwerven: het is een idle subprocess zonder API-call, en de pool-cap (1
// warme sessie) begrenst 'm al (zie concurrency.ts). Een échte end-to-end
// test zou een SDK-subprocess moeten spawnen — dat maakt de testsuite traag
// en afhankelijk van CI-netwerktoegang voor iets dat puur een bedradings-
// vraag is. In plaats daarvan bewaakt deze test de bedrading zelf: er is in
// heel ai.ts precies één call-site die een permit verwerft, en die zit in
// askClaude (de functie die zowel het koude pad als een warme claim gate't)
// — dus per constructie NOOIT in bootWarmCheapSession/de WarmPool-boot.
test("prewarm-boot raakt de concurrency-cap niet aan: acquire zit alleen in de echte LLM-run-functies, nooit in de warme-boot", () => {
  const src = readFileSync(fileURLToPath(new URL("./ai.ts", import.meta.url)), "utf8");
  const acquireCalls = [...src.matchAll(/aiSemaphore\.acquire\(/g)];
  // Elke permit-verwervende functie is een echte LLM-run onder de cap: askClaude
  // (koud pad + warme claim) en extractWithTool (#226, tool-forced extractie). De
  // boot van de warme pool hoort er NOOIT bij. Het aantal call-sites is het aantal
  // run-functies (niet vast op 1) — maar élke call-site MOET binnen een run-functie
  // uit de allowlist zitten. Zonder deze structuur-check zou een stray acquire in een
  // niet-run-helper (bv. createBrainMcpServer) stil een permit dubbeltellen (#226-review).
  assert.ok(
    acquireCalls.length >= 1,
    "er hoort ten minste één plek te zijn die een permit verwerft",
  );
  const runFunctions = new Set(["askClaude", "extractWithTool"]);
  const declRe = /(?:export\s+)?(?:async\s+)?function\*?\s+([A-Za-z0-9_]+)/g;
  const decls = [...src.matchAll(declRe)].map((m) => ({ name: m[1], index: m.index ?? 0 }));
  for (const call of acquireCalls) {
    const at = call.index ?? 0;
    const enclosing = decls.filter((d) => d.index < at).at(-1);
    assert.ok(
      enclosing && runFunctions.has(enclosing.name),
      `aiSemaphore.acquire op index ${at} zit in '${enclosing?.name ?? "<top-level>"}', ` +
        "geen echte LLM-run-functie (askClaude/extractWithTool)",
    );
  }
  const askClaudeStart = src.indexOf("export async function askClaude");
  const acquireIndex = src.indexOf("aiSemaphore.acquire(", askClaudeStart);
  assert.ok(askClaudeStart >= 0 && acquireIndex > askClaudeStart, "de acquire-call moet in askClaude staan");
  const bootFnStart = src.indexOf("function bootWarmCheapSession");
  const bootFnEnd = src.indexOf("\nexport const warmPool", bootFnStart);
  assert.ok(bootFnStart >= 0 && bootFnEnd > bootFnStart, "bootWarmCheapSession moet bestaan");
  const bootFnBody = src.slice(bootFnStart, bootFnEnd);
  assert.doesNotMatch(
    bootFnBody,
    /aiSemaphore/,
    "de warme-boot-functie mag de semaphore nooit aanraken (#154/#155-ontwerpgrens)",
  );
});

// ── De extract-timeout is een LLM-begroting, geen wachtrij-begroting (#281) ─
// De 90 s-timer stond vóór `aiSemaphore.acquire`, dus een extractie die eerst
// in de achtergrond-wachtrij stond (tot AI_QUEUE_WAIT_MS = 30 s) begon aan zijn
// LLM-run met een derde van het budget al op — en strandde daarna als
// "timeout", wat naar de LLM lijkt te wijzen terwijl de call simpelweg te
// weinig tijd kreeg. Sinds de mining parallel draait (#279) is die wachttijd de
// regel. Bedradings-toets, want een echte test zou een SDK-subprocess vragen.
test("de extract-timeout start ná de permit, niet bij binnenkomst (#281)", () => {
  const src = readFileSync(fileURLToPath(new URL("./ai.ts", import.meta.url)), "utf8");
  const start = src.indexOf("export async function extractWithTool");
  const end = src.indexOf("\n/** Bericht-vorm voor streaming input", start);
  assert.ok(start >= 0 && end > start, "extractWithTool moet te isoleren zijn");
  const body = src.slice(start, end);
  const acquireAt = body.indexOf("aiSemaphore.acquire(");
  const timerAt = body.indexOf("EXTRACT_TIMEOUT_MS)");
  assert.ok(acquireAt > 0, "extractWithTool hoort een permit te verwerven");
  assert.ok(timerAt > 0, "extractWithTool hoort een harde timeout te zetten");
  assert.ok(
    timerAt > acquireAt,
    "de setTimeout op EXTRACT_TIMEOUT_MS moet ná aiSemaphore.acquire staan, " +
      "anders eet de wachtrij het LLM-budget op",
  );
});

// De twee bron-grep-tests die hier stonden (#281) zijn vervangen door echte
// gedragstests in extract-timeout.test.ts. Ze checkten met regexes op deze
// broncode dát `timedOut: true` en `? 504 : 500` erin voorkwamen — en toen een
// pure refactor die tekens verplaatste zonder één gedragswijziging, faalden ze,
// terwijl ze de omgekeerde mutatie (`timedOut = true` weghalen, waardoor elke
// timeout weer een generieke 500 werd) juist NIET zagen. Precies verkeerd om.

// ── Model-override slaat de warme pool over (#174) ─────────────────────────
// De voorverwarmde sessie is altijd op MODEL.cheap gebootstrapt
// (bootWarmCheapSession roept buildQueryOptions({task:"cheap"}) zonder model
// aan) — een cheap-call MET override die alsnog een warme claim pakt zou de
// override stilzwijgend negeren.
//
// Dit was een bron-grep op de guard-regel, en die betrapte precies waar
// CLAUDE.md voor waarschuwt: hij ging rood toen `warmPool.isEnabled()` in
// #300 `pool.isEnabled()` werd — een hernoeming zonder één gedragswijziging —
// terwijl hij het weghalen van de guard alleen zou zien als de nieuwe vorm
// toevallig ook niet meer matchte. Sinds er een pool-naad is, is de vraag
// gewoon op gedrag te stellen: pakt hij de warme sessie, ja of nee.
// Beide richtingen, want "hij claimt niet" bewijst niets als hij nooit claimt.

test("model-override slaat de warme-pool-claim over (#174)", async () => {
  const { pool, claimed } = armWarmPool("S");
  let coldModel: string | undefined;
  const runQuery: AskQueryRunner = ({ options }) => {
    coldModel = options.model;
    return stream({ type: "result", subtype: "success", is_error: false, result: "koud" });
  };

  const res = await askClaude({
    prompt: "vraag",
    system: "S",
    model: "claude-sonnet-5",
    pool,
    runQuery,
  });

  assert.equal(res.answer, "koud", "de override hoort koud te draaien");
  assert.equal(coldModel, "claude-sonnet-5", "de override moet de koude call bereiken");
  // De klaargezette sessie is niet aangeraakt en blijft dus beschikbaar.
  assert.ok(pool.claim({ systemPrompt: "S", includePartialMessages: false }));
  claimed.end();
});

test("zónder override wordt de warme sessie WÉL geclaimd (tegenproef bij #174)", async () => {
  const { pool, claimed } = armWarmPool("S");
  const run = askClaude({
    prompt: "vraag",
    system: "S",
    pool,
    runQuery: () => assert.fail("er stond een passende warme sessie klaar — koud draaien mag niet"),
  });
  await settle();
  claimed.emit({ type: "result", subtype: "success", is_error: false, result: "warm" });
  claimed.end();

  assert.equal((await run).answer, "warm");
});

// ── Brein-stappen: de vraag hoort in de trace, niet in de containerlog (#292) ─
// Tot #292 logde de tool-handler `[agentic] ${step}` naar stdout, en `step`
// bevat de tool-ARGUMENTEN — bij semantic_search in de praktijk de vraagtekst
// van de gebruiker. Dat is geen secret-probleem (`safeDetail` zou er niets aan
// doen) maar een privacy-probleem: containerlogs worden losser behandeld dan
// `ask_trace`, waar de vraag bewust wél staat, achter de admin-poort.
//
// Deze test is bewust GEDRAG, geen grep: hij roept de functie aan die de
// tool-handler ook aanroept, en kijkt naar wat er echt uit stdout komt.

/** Vang stdout, zodat de test de ECHTE logregel ziet in plaats van een
 * reconstructie ervan. (Dezelfde drie regels als in failure.test.ts — een
 * gedeelde test-helper is hier meer ceremonie dan winst.) */
function captureLog(fn: () => void): string[] {
  const lines: string[] = [];
  const original = console.log;
  console.log = (...args: unknown[]) => {
    lines.push(args.map(String).join(" "));
  };
  try {
    fn();
  } finally {
    console.log = original;
  }
  return lines;
}

test("brein-stap: de vraagtekst gaat naar de trace, nooit naar stdout", () => {
  const vraag = "mag Yasuo blokkeren als mijn buurman Sjoerd hem exhaust";
  const steps: string[] = [];
  const lines = captureLog(() => {
    noteBrainStep("semantic_search", { query: vraag, k: 5 }, (s) => steps.push(s));
  });

  // Kanaal 1 — de aanroeper (server.ts `steps` → AskTrace.BrainSteps): volledig,
  // inclusief argumenten. Dáár hoort de stap, en daar verandert #292 niets aan.
  assert.equal(steps.length, 1);
  assert.match(steps[0], /^semantic_search /);
  assert.ok(steps[0].includes("Yasuo"), steps[0]);

  // Kanaal 2 — de containerlog: toolnaam en MAAT, geen inhoud.
  assert.equal(lines.length, 1);
  const parsed = JSON.parse(lines[0]) as Record<string, unknown>;
  assert.equal(parsed.evt, "brain_step");
  assert.equal(parsed.tool, "semantic_search");
  assert.ok(typeof parsed.bytes === "number" && parsed.bytes > 0, lines[0]);
  for (const woord of ["Yasuo", "Sjoerd", "blokkeren", "buurman", "exhaust"]) {
    assert.equal(
      lines[0].toLowerCase().includes(woord.toLowerCase()),
      false,
      `"${woord}" uit de vraag van de gebruiker staat in de containerlog: ${lines[0]}`,
    );
  }
});

test("brein-stap zonder onStep logt nog steeds inhoudsloos (geen tweede pad)", () => {
  const lines = captureLog(() => {
    noteBrainStep("get_node", { ref: "card:OGN-123", notitie: "geheime-context-xyz" });
  });
  assert.equal(lines.length, 1);
  assert.equal(lines[0].includes("geheime-context-xyz"), false, lines[0]);
  assert.equal(lines[0].includes("OGN-123"), false, lines[0]);
  assert.equal((JSON.parse(lines[0]) as Record<string, unknown>).tool, "get_node");
});

// ── De stderr-staart op het /ask-pad (#300) ────────────────────────────────
//
// #281 bouwde StderrTail en zette hem op de extract-endpoints; `buildQueryOptions`
// kreeg hem nooit. Dat is geen gemiste doorgifte maar een weggegooide stroom: de
// SDK spawnt met `stdio:[…,…, options.stderr ? "pipe" : "ignore"]`, dus zonder de
// optie vangt niemand ooit iets op. Heel /ask — inclusief agentic — miste daardoor
// precies het spoor dat een omgevallen subprocess achterlaat.
//
// Deze tests gaan door askClaude heen (via de runQuery-/pool-naden) in plaats van
// te toetsen dát de optie in de broncode staat: een bron-grep vangt zijn eigen bug
// niet, en dat was hier al twee keer duur.

/** Een pool die uit staat: askClaude slaat de warme tak dan volledig over. */
function noWarmPool(): WarmPool {
  return new WarmPool({
    boot: () => assert.fail("een uitgeschakelde pool hoort nooit te booten"),
    enabled: false,
    ttlMs: 1000,
    log: () => {},
  });
}

/** Warme pool met bestuurbare sessies: elke boot krijgt een eigen staart en een
 * eigen berichtenkraan, zodat een test kan naspelen wat een echte warme sessie
 * doet — inclusief "dood bij claim". */
function fakeWarmPool() {
  const boots: Array<{
    stderr: StderrTail;
    emit: (m: unknown) => void;
    end: () => void;
  }> = [];
  const pool = new WarmPool({
    boot: (): WarmBootHandle => {
      const out = pushableInput<unknown>();
      const stderr = new StderrTail();
      boots.push({ stderr, emit: out.push, end: out.end });
      async function* messages() {
        for await (const m of out.iterable) yield m;
      }
      return {
        messages: messages(),
        stderr,
        push: () => {},
        endInput: () => {},
        kill: () => out.end(),
      };
    },
    enabled: true,
    ttlMs: 60_000,
    log: () => {},
  });
  return { pool, boots };
}

/** Zet een warme sessie klaar voor exact deze signatuur en geef de sessie terug
 * die askClaude straks claimt. (De pool leert signaturen uit echt verkeer, dus
 * eerst het venster openen, dan de signatuur voeren, dan booten.) */
function armWarmPool(systemPrompt: string) {
  const { pool, boots } = fakeWarmPool();
  pool.prewarm();
  pool.observe({ systemPrompt, includePartialMessages: false });
  pool.prewarm();
  assert.equal(boots.length, 1, "er hoort precies één warme sessie klaar te staan");
  return { pool, boots, claimed: boots[0] };
}

const settle = () => new Promise<void>((r) => setTimeout(r, 20));

/** Een mislukte run zoals de SDK hem meldt: géén exception, een gewoon
 * result-bericht (#281). */
const failedResult = {
  type: "result",
  subtype: "error_during_execution",
  is_error: true,
  errors: [],
};

test("/ask koud: de stderr van het subprocess verklaart de uitval (#300)", async () => {
  let seen: Options | undefined;
  const runQuery: AskQueryRunner = ({ options }) => {
    seen = options;
    // Een subprocess dat zijn laatste woorden naar stderr schrijft en dan als
    // mislukte run eindigt — het stilste faalpad dat er is op de krappe VM.
    options.stderr?.("Claude Code process exited with code 137\n");
    return stream(failedResult);
  };

  await assert.rejects(
    askClaude({ prompt: "vraag", pool: noWarmPool(), runQuery }),
    (e: unknown) => {
      const { reason, detail } = failureOf(e);
      assert.match(detail, /exited with code 137/, `stderr-staart ontbreekt: ${detail}`);
      // De REDEN is de knop die de beheerder afleest (#300-review, finding 2):
      // `error_during_execution` zonder spawn-marker in het result IS `sdk_error`.
      // De buitenste catch mag hem niet HERclassificeren op de "exited with code
      // 137" die uit de stderr-staart in de message belandde — dan werd het
      // `spawn`. Een assert op alleen `.detail` (die staat er in beide gevallen)
      // zou die misattributie nooit zien.
      assert.equal(reason, "sdk_error", `reason ge-herclassificeerd: ${reason}`);
      // En geen dubbele aanplak: `finishAskRun` verrijkt één keer, de catch
      // mag hem niet nóg eens door `withStderrDigest` halen.
      assert.equal(
        (detail.match(/exited with code 137/g) ?? []).length,
        1,
        `stderr-staart dubbel aangeplakt: ${detail}`,
      );
      assert.equal(detail.includes("AiRunError:"), false, `ruis-prefix: ${detail}`);
      return true;
    },
  );
  // Expliciet, zodat een gebroken bedrading niet als een vage assert-fout
  // hierboven verschijnt maar als wat het is: er wordt niets opgevangen.
  assert.equal(typeof seen?.stderr, "function", "het koude /ask-pad spawnt zonder stderr-opvang");
});

test("/ask: de al-geclassificeerde reason overleeft de buitenste catch (#300-review)", async () => {
  // Finding 2, de scherpste vorm. `finishAskRun` gooit een AL geclassificeerde
  // AiRunError binnen de try; de buitenste catch ving die en haalde hem opnieuw
  // door `describeThrown` — die kent geen AiRunError-special-case (alleen
  // `failureOf` doet dat), dus `max_turns`/`permission_denied` werden `unknown`.
  // Dat is precies de stille misattributie die deze werklijn moet wegnemen.
  //
  // `max_turns` en `permission_denied` zijn de scherpste bewijzen: `describeThrown`
  // KAN ze niet produceren (ze komen alleen uit `resultFailure` op het
  // result-bericht), dus "reason klopt" bewijst hier ondubbelzinnig dat de catch
  // niet herclassificeerde.
  const cases: Array<{ result: Record<string, unknown>; reason: string }> = [
    {
      result: { type: "result", subtype: "error_max_turns", is_error: true, num_turns: 1, errors: [] },
      reason: "max_turns",
    },
    {
      result: {
        type: "result",
        subtype: "success",
        is_error: true,
        permission_denials: [{ tool: "x" }],
      },
      reason: "permission_denied",
    },
  ];
  for (const { result, reason } of cases) {
    const runQuery: AskQueryRunner = ({ options }) => {
      options.stderr?.("Claude Code process exited with code 137\n");
      return stream(result);
    };
    const failure = await askClaude({ prompt: "vraag", pool: noWarmPool(), runQuery }).then(
      () => assert.fail("een mislukte run zonder antwoord hoort te gooien"),
      (e: unknown) => failureOf(e),
    );
    assert.equal(failure.reason, reason, `reason ge-herclassificeerd voor ${reason}: ${failure.reason}`);
  }
});

test("/ask: de vraag van de bezoeker komt NIET in de logregel, de machineregel WEL", async () => {
  // De hele reden dat /ask een andere leesvorm krijgt dan /extract. Op de
  // extract-endpoints is de invoer publieke Riot-kaarttekst en is het residu
  // aanvaard; hier is de invoer de vraag van een bezoeker, en die hoort in
  // ask_trace achter de admin-poort — niet in `docker logs rb-v2-ai`.
  const vraag = "mag Yasuo blokkeren als mijn buurman Sjoerd hem exhaust";
  const runQuery: AskQueryRunner = ({ options }) => {
    options.stderr?.(`[verbose] prompt=${vraag}\n`);
    options.stderr?.("Claude Code process terminated by signal SIGKILL\n");
    return stream(failedResult);
  };

  const failure = await askClaude({ prompt: vraag, pool: noWarmPool(), runQuery }).then(
    () => assert.fail("een mislukte run zonder antwoord hoort te gooien"),
    (e: unknown) => failureOf(e),
  );

  // Zo komt de toelichting er in productie echt uit: server.ts geeft haar aan
  // logCall. Deze test kijkt dus naar de ECHTE logregel, niet naar een
  // reconstructie ervan.
  const lines = captureLog(() => {
    logCall({
      endpoint: "/ask",
      ms: 1200,
      status: 500,
      outcome: "error",
      reason: failure.reason,
      detail: failure.detail,
    });
  });
  assert.equal(lines.length, 1);

  assert.match(lines[0], /SIGKILL/, `de machine-diagnostiek is weg: ${lines[0]}`);
  for (const woord of ["Yasuo", "Sjoerd", "blokkeren", "buurman", "exhaust"]) {
    assert.equal(
      lines[0].toLowerCase().includes(woord.toLowerCase()),
      false,
      `"${woord}" uit de vraag staat in de containerlog: ${lines[0]}`,
    );
  }
});

test("/ask warm: een mislukte warme run gooit net zo goed als een koude (#300)", async () => {
  // Tot deze PR deed het warme pad `if (progress.sawOutput) return res` en
  // sloeg daarmee de poort over die het koude pad wél had. Exact dezelfde
  // mislukte run leverde koud een AiRunError met reden op, en warm een 200 met
  // een leeg antwoord. Geen enkele test zag dat, want het warme pad was zonder
  // echt subprocess niet te bereiken.
  const { pool, claimed } = armWarmPool("S");

  const run = askClaude({
    prompt: "vraag",
    system: "S",
    pool,
    runQuery: () => assert.fail("de warme sessie leverde output — koud herstarten mag niet"),
  });
  await settle();
  claimed.stderr.append("Claude Code process exited with code 137\n");
  claimed.emit(failedResult);
  claimed.end();

  await assert.rejects(run, (e: unknown) => {
    const { reason, detail } = failureOf(e);
    // …met de juiste reden (niet ge-herclassificeerd door de catch, #300-review)…
    assert.equal(reason, "sdk_error", `reason ge-herclassificeerd: ${reason}`);
    // …en met de staart van de sessie die het écht deed, precies één keer.
    assert.match(detail, /exited with code 137/, `warme staart ontbreekt: ${detail}`);
    assert.equal(
      (detail.match(/exited with code 137/g) ?? []).length,
      1,
      `warme staart dubbel aangeplakt: ${detail}`,
    );
    return true;
  });
});

test("/ask warm: een dode claim geeft zijn staart NIET door aan de koude herstart", async () => {
  // Attributie is hier het hele punt, en fout toewijzen is erger dan geen
  // staart: dan verklaart de beheerder met stelligheid de verkeerde sessie. De
  // warme sessie sterft vóór er output was; de koude herstart is een ándere
  // sessie en mag alleen met haar eigen uitvoer verklaard worden.
  const { pool, claimed } = armWarmPool("S");

  const runQuery: AskQueryRunner = ({ options }) => {
    options.stderr?.("ENOMEM tijdens de KOUDE herstart\n");
    return stream(failedResult);
  };
  const run = askClaude({ prompt: "vraag", system: "S", pool, runQuery });
  await settle();
  claimed.stderr.append("SIGSEGV in de WARME sessie\n");
  claimed.end(); // eindigt zonder één output-bericht ⇒ dood bij claim

  const lines: string[] = [];
  const original = console.log;
  console.log = (...args: unknown[]) => lines.push(args.map(String).join(" "));
  let failure;
  try {
    failure = await run.then(
      () => assert.fail("de koude herstart faalde ook — dat hoort te gooien"),
      (e: unknown) => failureOf(e),
    );
  } finally {
    console.log = original;
  }

  assert.match(failure.detail, /ENOMEM/, `de koude staart ontbreekt: ${failure.detail}`);
  assert.equal(
    failure.detail.includes("SIGSEGV"),
    false,
    `de staart van de dode warme sessie is aan de koude run toegeschreven: ${failure.detail}`,
  );
  // Weggooien mag hij hem niet: op het fallback-moment is dit de enige plek
  // waar die uitvoer nog aan de juiste sessie hangt.
  const fallback = lines.map((l) => JSON.parse(l) as Record<string, unknown>)
    .find((l) => l.evt === "warmpool_fallback");
  assert.ok(fallback, `geen warmpool_fallback-regel: ${lines.join(" | ")}`);
  assert.match(String(fallback.stderr), /SIGSEGV/);
});
