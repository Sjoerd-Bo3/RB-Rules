// GEDRAGSTESTS op de batch-extractie (#323), zelfde snit als
// extract-timeout.test.ts: echte code, geen bron-greps.
//
// Drie lagen:
//  1. `scaledExtractTimeoutMs` en `captureBatchToolCall` — puur, met
//     UITGESCHREVEN literals (#286/#293-les: een assertie tegen de constante
//     die ze bewaakt schuift mee).
//  2. `decideBatchExtractOutcome` — de partial-salvage-beslissing, uitputtend.
//  3. `extractBatchWithTool` met een geïnjecteerde `runQuery`: de wiring van
//     model, maxTurns en de MET K MEESCHALENDE timeout (les #311: een timeout
//     die niet meeschaalt meet alleen zijn eigen plafond). Draait met
//     AI_EXTRACT_TIMEOUT_MS=1000 en AI_EXTRACT_PER_CARD_MS=1100 uit het
//     npm-script — ai.ts leest die bij module-load (en de per-kaart-knop kent
//     een ondergrens van 1000 ms, vandaar 1100 en niet iets kleiners).
import assert from "node:assert/strict";
import { test } from "node:test";
import {
  captureBatchToolCall,
  decideBatchExtractOutcome,
  extractBatchWithTool,
  scaledExtractTimeoutMs,
  type BatchCaptureState,
  type QueryRunner,
} from "./ai.js";

const BASE_MS = Number(process.env.AI_EXTRACT_TIMEOUT_MS ?? "1000");
const PER_CARD_MS = Number(process.env.AI_EXTRACT_PER_CARD_MS ?? "1100");

// ── Laag 1a: de timeout-schaling, met literals ─────────────────────────────

test("timeout schaalt mee met K: basis + (K−1) × per-kaart (LETTERLIJK)", () => {
  // Mutatie-anker voor verificatiepunt 4: zet de schaling uit (return baseMs)
  // en deze asserts gaan rood. Bewust uitgeschreven waarden, geen herberekening
  // met dezelfde formule.
  assert.equal(scaledExtractTimeoutMs(8, 90_000, 180_000), 1_350_000);
  assert.equal(scaledExtractTimeoutMs(2, 90_000, 180_000), 270_000);
  assert.equal(scaledExtractTimeoutMs(1, 90_000, 180_000), 90_000, "K=1 = exact de basis");
  assert.equal(scaledExtractTimeoutMs(0, 90_000, 180_000), 90_000, "nooit ónder de basis");
});

// ── Laag 1b: de kruisbesmettingspoort (vangst per kaartcode) ───────────────

const keySet = new Set(["ogn-001", "ogn-002"]);
const capture = (state: BatchCaptureState, args: Record<string, unknown>) =>
  captureBatchToolCall(state, keySet, "card", "interactions", args);

test("unknown_code: een call met een code buiten de set wordt geweigerd en geteld", () => {
  // Mutatie-anker voor verificatiepunt 1: haal de keySet-poort weg en de
  // vangst bevat ineens "ogn-999" — beide asserts gaan dan rood.
  const state: BatchCaptureState = { perKey: new Map(), unknownKeys: 0 };
  const r = capture(state, { card: "ogn-999", interactions: [{ from: "x" }] });
  assert.equal(state.perKey.size, 0, "een onbekende code mag NOOIT gevangen worden");
  assert.equal(state.unknownKeys, 1);
  assert.match(r.ack, /geweigerd/);
  assert.equal(r.accepted, undefined, "een geweigerde call mag geen heartbeat voeden");

  // …en de overleef-kant (#295-les): een geldige code komt wél binnen.
  const ok = capture(state, { card: "ogn-001", interactions: [{ from: "a" }] });
  assert.deepEqual(state.perKey.get("ogn-001"), [{ from: "a" }]);
  assert.equal(state.unknownKeys, 1, "een geldige call telt niet als unknown");
  assert.match(ok.ack, /ok \(1\/2\)/);
  assert.equal(ok.accepted, "ogn-001", "de geaccepteerde code voedt de heartbeat");
});

test("laatste geldige call per kaart wint; niet-array is een lege vangst", () => {
  const state: BatchCaptureState = { perKey: new Map(), unknownKeys: 0 };
  capture(state, { card: "ogn-001", interactions: [{ from: "a" }] });
  capture(state, { card: "ogn-001", interactions: [{ from: "b" }] });
  assert.deepEqual(state.perKey.get("ogn-001"), [{ from: "b" }]);
  capture(state, { card: "ogn-002", interactions: "none" });
  assert.deepEqual(state.perKey.get("ogn-002"), []);
});

test("een ontbrekende of niet-string code telt als unknown, nooit als vangst", () => {
  const state: BatchCaptureState = { perKey: new Map(), unknownKeys: 0 };
  capture(state, { interactions: [] });
  capture(state, { card: 42, interactions: [] });
  assert.equal(state.perKey.size, 0);
  assert.equal(state.unknownKeys, 2);
});

// ── Laag 2: de partial-salvage-beslissing ──────────────────────────────────

const base = { toolName: "emit_interactions", timeoutMs: 90_000, unknownKeys: 0 };
const twee = () =>
  new Map<string, unknown[]>([
    ["ogn-001", [{ from: "a" }]],
    ["ogn-002", []],
  ]);

test("salvage: sessie sterft op de timeout na 2 van 3 → de 2 blijven, reden = timeout", () => {
  // Mutatie-anker voor verificatiepunt 3 (rb-ai-kant): wie hier bij een
  // timeout de vangst weggooit (perKey: new Map()) maakt beide asserts rood.
  const o = decideBatchExtractOutcome({
    ...base, perKey: twee(), expected: 3, timedOut: true, aborted: false,
  });
  assert.equal(o.perKey.size, 2, "goed werk weggooien mag nooit");
  assert.equal(o.timedOut, true);
  assert.equal(o.failure?.reason, "timeout");
  assert.match(o.failure!.detail, /2\/3/);
});

test("salvage: run-fout na gedeeltelijke vangst → vangst blijft, reden = de run-fout", () => {
  const o = decideBatchExtractOutcome({
    ...base, perKey: twee(), expected: 3, timedOut: false, aborted: false,
    runFailure: { reason: "max_turns", detail: "subtype=error_max_turns" },
  });
  assert.equal(o.perKey.size, 2);
  assert.equal(o.failure?.reason, "max_turns");
});

test("nette afloop met ontbrekende kaarten → no_tool_call voor de rest", () => {
  const o = decideBatchExtractOutcome({
    ...base, perKey: twee(), expected: 3, timedOut: false, aborted: false,
  });
  assert.equal(o.failure?.reason, "no_tool_call");
  assert.match(o.failure!.detail, /2\/3/);
});

test("alles gevangen → geen faalreden; niets gevangen + timeout → timeout", () => {
  const compleet = decideBatchExtractOutcome({
    ...base, perKey: twee(), expected: 2, timedOut: false, aborted: false,
  });
  assert.equal(compleet.failure, undefined);
  assert.equal(compleet.perKey.size, 2);

  const leeg = decideBatchExtractOutcome({
    ...base, perKey: new Map(), expected: 3, timedOut: true, aborted: false,
  });
  assert.equal(leeg.perKey.size, 0);
  assert.equal(leeg.timedOut, true);
  assert.equal(leeg.failure?.reason, "timeout");
});

test("client-abort met ontbrekende kaarten blijft aborted (geen timeout)", () => {
  const o = decideBatchExtractOutcome({
    ...base, perKey: twee(), expected: 3, timedOut: false, aborted: true,
  });
  assert.equal(o.failure?.reason, "aborted");
  assert.equal(o.timedOut, undefined);
});

// ── Laag 3: de echte functie, via de runQuery-naad ─────────────────────────

const untilAbort = (signal: AbortSignal) =>
  new Promise<void>((resolve) => {
    if (signal.aborted) return resolve();
    signal.addEventListener("abort", () => resolve(), { once: true });
  });

function batch(keys: string[], runQuery: QueryRunner, model?: string) {
  return extractBatchWithTool({
    toolName: "emit_interactions",
    description: "test",
    schema: {} as never,
    keyField: "card",
    resultKey: "interactions",
    keys,
    addendum: "test",
    text: "kaartteksten",
    ...(model ? { model } : {}),
    runQuery,
  });
}

test("wiring: model-ID en meeschalende maxTurns bereiken de SDK-options", async () => {
  // Verificatiepunt 5, rb-ai-kant: het opgeloste model-ID staat LETTERLIJK in
  // de options; zonder override het bestaande cheap-model. En maxTurns groeit
  // per extra kaart: K=3 → 5 (basis 3 + 2), uitgeschreven literal.
  let seen: { model?: string; maxTurns?: number } = {};
  const spy = (async function* (arg: { options: { model?: string; maxTurns?: number } }) {
    seen = arg.options;
    yield { type: "result", subtype: "success", is_error: false, result: "klaar" };
  }) as unknown as QueryRunner;

  await batch(["a", "b", "c"], spy, "claude-fable-5");
  assert.equal(seen.model, "claude-fable-5");
  assert.equal(seen.maxTurns, 5);

  await batch(["a"], spy);
  assert.equal(seen.model, "claude-sonnet-4-6", "zonder override: het bestaande cheap-model");
  assert.equal(seen.maxTurns, 3, "K=1 gedraagt zich exact als het losse pad");
});

test("wiring: de harde timeout schaalt ECHT mee met K (zet de schaling uit → rood)", async () => {
  // Basis 1000 ms + 2 × 1100 ms = 3200 ms voor K=3. Een runQuery die pas
  // eindigt bij de abort meet zo de echte timer: zonder schaling was hij na
  // ~1000 ms afgekapt en faalt de ondergrens-assert.
  assert.equal(BASE_MS, 1000, "npm-script hoort AI_EXTRACT_TIMEOUT_MS=1000 te zetten");
  assert.equal(PER_CARD_MS, 1100, "npm-script hoort AI_EXTRACT_PER_CARD_MS=1100 te zetten");
  const hang = (async function* (arg: { options: { abortController: AbortController } }) {
    await untilAbort(arg.options.abortController.signal);
  }) as unknown as QueryRunner;

  const t0 = Date.now();
  const o = await batch(["a", "b", "c"], hang);
  const elapsed = Date.now() - t0;
  assert.equal(o.timedOut, true);
  assert.equal(o.failure?.reason, "timeout");
  assert.ok(elapsed >= 3000, `verwacht ≥3000 ms (1000 + 2×1100), was ${elapsed} ms`);
  assert.ok(elapsed < 8000, `runaway-timer: ${elapsed} ms`);
});

test("een fout-result zonder vangst wordt de faalreden; ConcurrencyLimit bubbelt", async () => {
  const o = await batch(["a", "b"], (async function* () {
    yield {
      type: "result", subtype: "error_during_execution",
      is_error: true, errors: ["stream ended"],
    };
  }) as unknown as QueryRunner);
  assert.equal(o.perKey.size, 0);
  assert.equal(o.failure?.reason, "sdk_error");
  assert.equal(o.timedOut, undefined);
});

test("usage uit het result-bericht reist mee in de batch-uitkomst", async () => {
  // "Wat kost een fable-batch van K?" is alleen te beantwoorden als de echte
  // token-tellingen de uitkomst bereiken; rb-api boekt ze in de metering.
  const o = await batch(["a"], (async function* () {
    yield {
      type: "result", subtype: "success", is_error: false, result: "klaar",
      usage: { input_tokens: 1200, output_tokens: 340 },
    };
  }) as unknown as QueryRunner);
  assert.ok(o.usage, "usage hoort uit het result-bericht gelezen te worden");
  assert.equal(o.usage!.outputTokens, 340);

  const zonder = await batch(["a"], (async function* () {
    yield { type: "result", subtype: "success", is_error: false, result: "klaar" };
  }) as unknown as QueryRunner);
  assert.equal(zonder.usage, null, "geen usage = onbekend (null), nooit 0");
});
