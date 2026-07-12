import assert from "node:assert/strict";
import { test } from "node:test";
import { usageFromSdk } from "./usage.js";

// Vormen gespiegeld aan wat de Agent SDK in het result-bericht meestuurt
// (BetaUsage, snake_case) — niet versimpeld verzonnen.

test("volledige SDK-usage: cache-tokens tellen mee in input", () => {
  const usage = usageFromSdk({
    input_tokens: 12,
    cache_creation_input_tokens: 3_000,
    cache_read_input_tokens: 45_000,
    output_tokens: 890,
    service_tier: "standard",
  });
  assert.deepEqual(usage, { inputTokens: 48_012, outputTokens: 890 });
});

test("usage zonder cache-velden (oudere respons) blijft bruikbaar", () => {
  assert.deepEqual(usageFromSdk({ input_tokens: 100, output_tokens: 20 }), {
    inputTokens: 100,
    outputTokens: 20,
  });
});

test("één bruikbaar getal is genoeg — de rest telt als 0", () => {
  assert.deepEqual(usageFromSdk({ output_tokens: 7 }), {
    inputTokens: 0,
    outputTokens: 7,
  });
});

test("onbruikbare invoer degradeert naar null, nooit een exception", () => {
  assert.equal(usageFromSdk(undefined), null);
  assert.equal(usageFromSdk(null), null);
  assert.equal(usageFromSdk("usage"), null);
  assert.equal(usageFromSdk({}), null);
  assert.equal(usageFromSdk({ input_tokens: "12", output_tokens: null }), null);
});

test("negatieve of niet-eindige getallen tellen niet", () => {
  assert.equal(usageFromSdk({ input_tokens: -5, output_tokens: NaN }), null);
  assert.deepEqual(
    usageFromSdk({ input_tokens: 10, cache_read_input_tokens: -1, output_tokens: 2 }),
    { inputTokens: 10, outputTokens: 2 },
  );
});
