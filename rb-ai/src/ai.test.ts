import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import {
  buildQueryOptions,
  buildUserMessage,
  collectAnswer,
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
test("prewarm-boot raakt de concurrency-cap niet aan: precies één acquire-call-site, en die zit in askClaude", () => {
  const src = readFileSync(fileURLToPath(new URL("./ai.ts", import.meta.url)), "utf8");
  const acquireCalls = src.match(/aiSemaphore\.acquire\(/g) ?? [];
  assert.equal(
    acquireCalls.length,
    1,
    "er hoort precies één plek te zijn die een permit verwerft — anders raakt de boot van de warme pool de cap aan",
  );
  const askClaudeStart = src.indexOf("export async function askClaude");
  const acquireIndex = src.indexOf("aiSemaphore.acquire(");
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
