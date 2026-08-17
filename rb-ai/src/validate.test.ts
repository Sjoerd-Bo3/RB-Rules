import { test } from "node:test";
import assert from "node:assert/strict";
import { parseAskRequest } from "./validate.js";

test("prompt is verplicht", () => {
  assert.deepEqual(parseAskRequest({}), { ok: false, error: "prompt vereist" });
  assert.equal(parseAskRequest({ prompt: "   " }).ok, false);
  assert.equal(parseAskRequest(null).ok, false);
  assert.equal(parseAskRequest("geen object").ok, false);
});

test("task valt terug op cheap; research/agentic alleen expliciet (tools zijn opt-in, #64/#106)", () => {
  const cases: Array<[unknown, string]> = [
    [undefined, "cheap"],
    ["cheap", "cheap"],
    ["hard", "hard"],
    ["research", "research"],
    ["agentic", "agentic"],
    // Nooit stilzwijgend tool-toegang: onbekende/afwijkende waarden → cheap.
    ["web", "cheap"],
    ["Research", "cheap"],
    ["Agentic", "cheap"],
    ["brain", "cheap"],
    [true, "cheap"],
  ];
  for (const [task, expected] of cases) {
    const r = parseAskRequest({ prompt: "vraag", task });
    assert.equal(r.ok, true, `task=${String(task)}`);
    if (r.ok) assert.equal(r.request.task, expected, `task=${String(task)}`);
  }
});

test("system: alleen niet-lege string gaat mee", () => {
  const met = parseAskRequest({ prompt: "v", system: "wees kort" });
  if (met.ok) assert.equal(met.request.system, "wees kort");
  const zonder = parseAskRequest({ prompt: "v", system: "  " });
  if (zonder.ok) assert.equal(zonder.request.system, undefined);
});

test("afbeeldingen: max 2, alleen toegestane mediaTypes en maat", () => {
  const img = { mediaType: "image/png", data: "abc" };

  const teVeel = parseAskRequest({ prompt: "v", images: [img, img, img] });
  assert.equal(teVeel.ok, true);
  if (teVeel.ok) assert.equal(teVeel.request.images.length, 2);

  const foutType = parseAskRequest({
    prompt: "v",
    images: [{ mediaType: "image/tiff", data: "abc" }],
  });
  assert.deepEqual(foutType, {
    ok: false,
    error: "mediaType niet ondersteund: image/tiff",
  });

  const leeg = parseAskRequest({
    prompt: "v",
    images: [{ mediaType: "image/png", data: "" }],
  });
  assert.equal(leeg.ok, false);

  const teGroot = parseAskRequest({
    prompt: "v",
    images: [{ mediaType: "image/png", data: "x".repeat(8_000_001) }],
  });
  assert.equal(teGroot.ok, false);

  const kapot = parseAskRequest({ prompt: "v", images: ["geen object"] });
  assert.equal(kapot.ok, false);
});

// Model-sweep (#174): puur doorgeefluik — geen allowlist, alleen een
// niet-lege string gaat mee; zonder override blijft het veld afwezig (het
// bestaande gedrag, MODEL[task] in ai.ts).
test("model: alleen niet-lege string gaat mee, anders afwezig (#174)", () => {
  const met = parseAskRequest({ prompt: "v", model: "claude-opus-4-8" });
  assert.equal(met.ok, true);
  if (met.ok) assert.equal(met.request.model, "claude-opus-4-8");

  const zonder = parseAskRequest({ prompt: "v" });
  assert.equal(zonder.ok, true);
  if (zonder.ok) assert.equal(zonder.request.model, undefined);

  const leeg = parseAskRequest({ prompt: "v", model: "   " });
  assert.equal(leeg.ok, true);
  if (leeg.ok) assert.equal(leeg.request.model, undefined);

  const getrimd = parseAskRequest({ prompt: "v", model: "  claude-sonnet-5  " });
  assert.equal(getrimd.ok, true);
  if (getrimd.ok) assert.equal(getrimd.request.model, "claude-sonnet-5");
});
