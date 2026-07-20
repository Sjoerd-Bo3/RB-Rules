// GEDRAGSTESTS op het timeout-pad van de brein-extractie (#281-review).
//
// WAAROM DIT BESTAND BESTAAT. De eerste versie van deze PR toetste dit pad met
// `readFileSync` + regexes op de eigen broncode ("staat `timedOut: true` er?",
// "staat `? 504 : 500` er?"). Zulke tests toetsen dat de TEKENS er staan, niet
// dat het WERKT — en ze vangen hun eigen bug niet: haal één `timedOut = true`
// uit de setTimeout-callback en alle tests blijven groen terwijl elke
// extractie-timeout weer als generieke 500 naar buiten komt. Precies het gat
// dat deze PR moest dichten. Hier draait echte code.
//
// Twee lagen:
//  1. `decideExtractOutcome` — de pure beslissing, uitputtend, zonder SDK.
//  2. `extractWithTool` met een geïnjecteerde `runQuery` (de test-seam) en een
//     korte tijdslimiet: de manieren waarop een afgebroken SDK-run kán aflopen.
//
// Dat die manieren met meer dan één zijn, is geen theoretische volledigheid
// maar de kern van deze PR: **de Agent SDK gooit niet noodzakelijk bij een
// mislukte run**. Een iterator kan na de abort ook gewoon leeglopen of nog een
// fout-result afgeven. Hing de 504 alleen aan het gooi-pad, dan kwam diezelfde
// timeout er langs de andere routes uit als `no_tool_call` of `sdk_error` met
// een 500 — actief misleidend, want dat stuurt de beheerder naar de prompt in
// plaats van naar de tijdslimiet.
import assert from "node:assert/strict";
import { test } from "node:test";
import { decideExtractOutcome, extractWithTool, type QueryRunner } from "./ai.js";

/** Moet gelijk zijn aan AI_EXTRACT_TIMEOUT_MS uit het npm-script: ai.ts leest
 * die bij module-load, dus de test kan hem niet meer zetten. */
const TIMEOUT_MS = Number(process.env.AI_EXTRACT_TIMEOUT_MS ?? "400");

/** Wacht tot `extractWithTool` zijn eigen AbortController afvuurt — precies wat
 * de echte SDK doet, die `options.abortController` observeert. Een nep-iterator
 * die dat NIET doet hangt eeuwig; de suite moet de productie-lus nabootsen, niet
 * omzeilen. */
const untilAbort = (signal: AbortSignal) =>
  new Promise<void>((resolve) => {
    if (signal.aborted) return resolve();
    signal.addEventListener("abort", () => resolve(), { once: true });
  });

function extract(runQuery: QueryRunner) {
  return extractWithTool({
    toolName: "emit_interactions",
    description: "test",
    schema: {} as never, // geen enkele mode laat de echte SDK-validatie lopen
    resultKey: "interactions",
    addendum: "test",
    text: "kaarttekst",
    runQuery,
  });
}

// ── Laag 1: de pure beslissing ────────────────────────────────────────────

const base = { toolName: "emit_interactions", timeoutMs: 90_000 };
const items = [{ from: "card:a", to: "mechanic:b" }];

test("beslissing: geldige vangst wint van elke faalreden", () => {
  const o = decideExtractOutcome({
    ...base, captured: items, timedOut: false, aborted: false,
    runFailure: { reason: "sdk_error", detail: "iets" },
  });
  assert.deepEqual(o.items, items, "goed werk weggooien mag nooit");
  assert.equal(o.timedOut, undefined);
});

test("beslissing: PARTIËLE vangst behoudt de kandidaten én meldt de afkapping", () => {
  // Zonder deze melding vallen juist de traagste kaarten uit de schaalklip-meting.
  const o = decideExtractOutcome({ ...base, captured: items, timedOut: true, aborted: false });
  assert.deepEqual(o.items, items);
  assert.equal(o.timedOut, true);
  assert.equal(o.failure?.reason, "timeout");
  assert.match(o.failure!.detail, /resultaat behouden/);
});

test("beslissing: afkapping zonder vangst → timeout, timedOut gezet", () => {
  const o = decideExtractOutcome({ ...base, captured: null, timedOut: true, aborted: false });
  assert.equal(o.items, null);
  assert.equal(o.timedOut, true);
  assert.equal(o.failure?.reason, "timeout");
  assert.match(o.failure!.detail, /90s/);
});

test("beslissing: afkapping WINT van een gemelde SDK-fout", () => {
  // Wij hakten af — dat is wat er gebeurde. `sdk_error` zou hier de verkeerde
  // knop aanwijzen.
  const o = decideExtractOutcome({
    ...base, captured: null, timedOut: true, aborted: false,
    runFailure: { reason: "sdk_error", detail: "stream ended" },
  });
  assert.equal(o.failure?.reason, "timeout");
  assert.equal(o.timedOut, true);
});

test("beslissing: client-abort blijft aborted (géén timeout, géén 504)", () => {
  const o = decideExtractOutcome({ ...base, captured: null, timedOut: false, aborted: true });
  assert.equal(o.failure?.reason, "aborted");
  assert.equal(o.timedOut, undefined, "een weggelopen client is geen tijdslimiet");
});

test("beslissing: nette afloop zonder tool-call blijft no_tool_call", () => {
  const o = decideExtractOutcome({ ...base, captured: null, timedOut: false, aborted: false });
  assert.equal(o.failure?.reason, "no_tool_call");
  assert.equal(o.timedOut, undefined);
});

test("beslissing: gemelde SDK-fout zonder afkapping blijft die fout", () => {
  const o = decideExtractOutcome({
    ...base, captured: null, timedOut: false, aborted: false,
    runFailure: { reason: "max_turns", detail: "subtype=error_max_turns" },
  });
  assert.equal(o.failure?.reason, "max_turns");
});

test("beslissing: een LEGE vangst is een geldig resultaat, geen uitval", () => {
  const o = decideExtractOutcome({ ...base, captured: [], timedOut: false, aborted: false });
  assert.deepEqual(o.items, []);
  assert.equal(o.failure, undefined);
});

// ── Laag 2: de echte functie, alle afloopvormen van een afgebroken run ────

test("iterator die GOOIT na de abort → timeout", async () => {
  // Wat de echte ProcessTransport vandaag doet (`if (this.exitError) throw`).
  const o = await extract((async function* (arg: { options: { abortController: AbortController } }) {
    await untilAbort(arg.options.abortController.signal);
    throw Object.assign(new Error("Claude Code process aborted by user"), {
      name: "AbortError",
    });
  }) as unknown as QueryRunner);
  assert.equal(o.items, null);
  assert.equal(o.timedOut, true);
  assert.equal(o.failure?.reason, "timeout");
});

test("iterator die na de abort LEEG loopt → óók timeout, NIET no_tool_call", async () => {
  // De regressie die #281-review bewees.
  const o = await extract((async function* (arg: { options: { abortController: AbortController } }) {
    await untilAbort(arg.options.abortController.signal);
    // en dan gewoon klaar, zónder te gooien
  }) as unknown as QueryRunner);
  assert.equal(o.timedOut, true, "een stil afgelopen iterator is óók een afkapping");
  assert.equal(o.failure?.reason, "timeout");
  assert.notEqual(
    o.failure?.reason,
    "no_tool_call",
    "no_tool_call wijst naar de prompt terwijl het de tijdslimiet was",
  );
});

test("iterator die een FOUT-RESULT geeft en dan eindigt → timeout wint", async () => {
  // Letterlijk de vorm waar deze PR om draait: de SDK meldt een mislukte run
  // als bericht in plaats van te gooien.
  const o = await extract((async function* (arg: { options: { abortController: AbortController } }) {
    await untilAbort(arg.options.abortController.signal);
    yield {
      type: "result", subtype: "error_during_execution",
      is_error: true, errors: ["stream ended"],
    };
  }) as unknown as QueryRunner);
  assert.equal(o.timedOut, true);
  assert.equal(o.failure?.reason, "timeout");
});

test("zonder afkapping blijft het normale pad ongewijzigd", async () => {
  const nietGeroepen = await extract((async function* () {
    yield { type: "system", subtype: "init" };
    yield { type: "result", subtype: "success", is_error: false, result: "klaar" };
  }) as unknown as QueryRunner);
  assert.equal(nietGeroepen.timedOut, undefined, "hier is niets afgekapt");
  assert.equal(nietGeroepen.failure?.reason, "no_tool_call");

  const foutResult = await extract((async function* () {
    yield {
      type: "result", subtype: "error_during_execution",
      is_error: true, errors: ["stream ended"],
    };
  }) as unknown as QueryRunner);
  assert.equal(foutResult.timedOut, undefined);
  assert.equal(foutResult.failure?.reason, "sdk_error");
});

test("SDK-retries kleuren de timeout naar de upstream-oorzaak", async () => {
  // De afkapping blijft staan, maar de REDEN wijst naar de knop die ertoe doet:
  // een run die op een aanhoudende 429 stond te wachten is geen trage LLM.
  const o = await extract((async function* (arg: { options: { abortController: AbortController } }) {
    yield {
      type: "system", subtype: "api_retry", attempt: 1, max_retries: 10,
      retry_delay_ms: 540, error_status: 429, error: "rate_limit_error",
    };
    await untilAbort(arg.options.abortController.signal);
  }) as unknown as QueryRunner);
  assert.equal(o.timedOut, true, "het blijft een afkapping");
  assert.equal(o.failure?.reason, "api_error", "maar de oorzaak zit upstream");
  assert.match(o.failure!.detail, /harde timeout/);
  assert.match(o.failure!.detail, /1 SDK-retries/);
});
