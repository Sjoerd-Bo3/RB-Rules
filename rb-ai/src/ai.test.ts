import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import {
  buildQueryOptions,
  buildUserMessage,
  collectAnswer,
  noteBrainStep,
  warmBootOptions,
  type CollectProgress,
} from "./ai.js";

async function* stream(...messages: unknown[]) {
  for (const m of messages) yield m;
}

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
    const warm = warmBootOptions(sig, controller);
    const cold = buildQueryOptions({
      task: "cheap",
      systemPrompt: sig.systemPrompt,
      includePartialMessages: sig.includePartialMessages,
      controller,
    });
    assert.deepEqual(warm, cold);
    // Byte-voor-byte, exclusief de (per definitie unieke) AbortController.
    const json = (o: object) =>
      JSON.stringify({ ...o, abortController: undefined });
    assert.equal(json(warm), json(cold));
  }
});

test("cheap-opties: exact het vertrouwde koude pad (golden)", () => {
  const controller = new AbortController();
  assert.deepEqual(
    buildQueryOptions({ task: "cheap", includePartialMessages: false, controller }),
    {
      model: "claude-sonnet-4-6",
      maxTurns: 1,
      tools: [],
      abortController: controller,
    },
  );
  assert.deepEqual(
    buildQueryOptions({
      task: "cheap",
      systemPrompt: "S",
      includePartialMessages: true,
      controller,
    }),
    {
      model: "claude-sonnet-4-6",
      maxTurns: 1,
      tools: [],
      abortController: controller,
      systemPrompt: "S",
      includePartialMessages: true,
    },
  );
});

test("research-opties: web-tools, dontAsk en 16 beurten (golden)", () => {
  const controller = new AbortController();
  assert.deepEqual(
    buildQueryOptions({ task: "research", includePartialMessages: false, controller }),
    {
      model: "claude-sonnet-4-6",
      maxTurns: 16,
      tools: ["WebSearch", "WebFetch"],
      allowedTools: ["WebSearch", "WebFetch"],
      permissionMode: "dontAsk",
      abortController: controller,
    },
  );
});

test("agentic-opties: brein-MCP, allowlist en 8 beurten", () => {
  const controller = new AbortController();
  const o = buildQueryOptions({
    task: "agentic",
    includePartialMessages: false,
    controller,
  }) as Record<string, unknown>;
  assert.equal(o.model, "claude-sonnet-4-6");
  assert.equal(o.maxTurns, 8);
  assert.deepEqual(o.tools, []);
  assert.equal(o.permissionMode, "dontAsk");
  assert.ok(o.mcpServers && typeof o.mcpServers === "object");
  assert.ok(Array.isArray(o.allowedTools) && (o.allowedTools as string[]).length > 0);
});

test("lege systemPrompt (undefined) zet het veld niet — signatuur-semantiek", () => {
  const controller = new AbortController();
  const o = buildQueryOptions({ task: "cheap", includePartialMessages: false, controller });
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
    });
    assert.equal(o.model, "claude-sonnet-5", `task=${task}`);
  }
});

test("zonder model-override blijft MODEL[task] ongewijzigd (#174)", () => {
  const controller = new AbortController();
  const o = buildQueryOptions({ task: "hard", includePartialMessages: false, controller });
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
// (bootWarmCheapSession roept buildQueryOptions({task:"cheap"}) zonder
// model aan) — een cheap-call MET override die alsnog een warme claim pakt
// zou de override stilzwijgend negeren. Zelfde bedradings-toets als
// hierboven: geen SDK-subprocess nodig, alleen bewijs dat de guard er staat.
test("model-override slaat de warme-pool-claim over (#174, bedrading)", () => {
  const src = readFileSync(fileURLToPath(new URL("./ai.ts", import.meta.url)), "utf8");
  const claimGuard = src.match(/if \(task === "cheap"[^)]*warmPool\.isEnabled\(\)\)/);
  assert.ok(claimGuard, "de warme-pool-claimguard moet op task===\"cheap\" && warmPool.isEnabled() checken");
  assert.match(
    claimGuard![0],
    /!model/,
    "de guard moet een model-override uitsluiten van de warme-pool-claim",
  );
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
