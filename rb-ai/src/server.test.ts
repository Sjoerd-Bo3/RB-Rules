// Endpoint-gedragstests op de BEDRADING van de vocabulaire-poort (#312-review).
//
// WAAROM DIT BESTAND BESTAAT. De narekening (`enforce*Vocabulary`) is puur en
// uitputtend getest — maar mutatie-bewezen: vervang de aanroep op de call-site
// in server.ts door een pass-through en álle tests bleven groen. Dat is de
// #292-klasse (een gedragstest op de poort ziet niet dat de call-site hem
// overslaat), en hij ontstond precies door #312 zelf: in het enum-tijdperk kón
// de handler het vocabulaire niet overslaan, want zonder request bestond er
// geen schema. Deze tests draaien daarom de ÉCHTE node:http-handler (PORT=0,
// echte fetch) met een gestubde extractie (deps-seam) en toetsen beide
// richtingen op de HTTP-grens: een out-of-vocab-item komt de respons niet in,
// en een geldig item overleeft ongeschonden (#295-les: een weiger-test zonder
// overleef-assert bewijst niets).
import assert from "node:assert/strict";
import { once } from "node:events";
import type { AddressInfo } from "node:net";
import { after, before, test } from "node:test";
import type { ExtractOutcome } from "./ai.js";

// Vóór de import: server.ts leest PORT bij module-load en gaat meteen
// luisteren; 0 = een vrije efemere poort, zodat de suite nergens mee botst.
process.env.PORT = "0";
const { deps, server } = await import("./server.js");
const realExtract = deps.extractWithTool;

let base = "";
before(async () => {
  if (!server.listening) await once(server, "listening");
  base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});
after(() => {
  deps.extractWithTool = realExtract;
  server.close();
});

/** Stub de extractie: de handler krijgt precies deze items terug, en de test
 * krijgt de opts te zien waarmee de handler de extractie aanriep — zo bewaken
 * dezelfde tests óók de prompt-bedrading (reist het vocabulaire echt mee?). */
function stubExtract(items: unknown[]): Array<Record<string, unknown>> {
  const calls: Array<Record<string, unknown>> = [];
  deps.extractWithTool = (async (opts: Record<string, unknown>) => {
    calls.push(opts);
    return { items } as ExtractOutcome;
  }) as typeof deps.extractWithTool;
  return calls;
}

const post = async (path: string, body: unknown) => {
  const res = await fetch(`${base}${path}`, {
    method: "POST",
    body: JSON.stringify(body),
  });
  return { status: res.status, body: (await res.json()) as Record<string, unknown> };
};

const interactionRequest = {
  system: "systeemprompt",
  text: "Deflect prevents Assault damage during a showdown.",
  refs: [
    { ref: "mechanic:Deflect", label: "Deflect" },
    { ref: "mechanic:Assault", label: "Assault" },
  ],
  kinds: ["COUNTERS", "MODIFIES"],
  conditionKinds: ["WINDOW", "STATUS", "COST"],
  roles: ["agent", "patient"],
  windowLexicon: ["Showdown"],
  statusLexicon: ["Exhausted"],
};

test("POST /extract/interactions: de narekening zit ECHT in de bedrading", async () => {
  // Het out-of-vocab-item staat bewust VOORAAN: bij een pass-through zou het
  // als eerste in de respons verschijnen en faalt de deepEqual gegarandeerd.
  const calls = stubExtract([
    { from: "mechanic:Deflect", to: "mechanic:Verzonnen", kind: "COUNTERS", interacts: true },
    {
      from: "mechanic:Deflect",
      to: "mechanic:Assault",
      kind: "COUNTERS",
      interacts: true,
      explanation: "Deflect counters Assault damage.",
    },
  ]);

  const { status, body } = await post("/extract/interactions", interactionRequest);

  assert.equal(status, 200);
  assert.deepEqual(body.interactions, [
    {
      from: "mechanic:Deflect",
      to: "mechanic:Assault",
      kind: "COUNTERS",
      interacts: true,
      explanation: "Deflect counters Assault damage.",
    },
  ]);
  // En de prompt-bedrading: het vocabulaire reist als invoer mee (#312) — een
  // handler die `parsed.request.text` kaal zou doorgeven maakt dit rood.
  assert.equal(calls.length, 1);
  assert.match(String(calls[0]?.text), /Aangeboden vocabulaire/);
  assert.match(String(calls[0]?.text), /mechanic:Deflect \(Deflect\)/);
  assert.match(String(calls[0]?.text), /Deflect prevents Assault damage/);
});

test("POST /extract/predicates: de narekening zit ECHT in de bedrading", async () => {
  const calls = stubExtract([
    { predicate: "enables", object: "exhaust" },
    { predicate: "prevents", object: "exhaust" },
  ]);

  const { status, body } = await post("/extract/predicates", {
    text: "Accelerate: your units do not exhaust when moving to a showdown.",
    subjectRef: "mechanic:Accelerate",
    subjectLabel: "Accelerate",
    predicates: ["triggers_on", "prevents", "grants", "requires_target"],
  });

  assert.equal(status, 200);
  assert.deepEqual(body.predicates, [{ predicate: "prevents", object: "exhaust" }]);
  assert.equal(calls.length, 1);
  assert.match(String(calls[0]?.text), /Toegestane predicaten/);
  assert.match(String(calls[0]?.text), /Accelerate \(mechanic:Accelerate\)/);
});

test("POST /extract/interactions: uitval blijft een 500 met reden — de gate eet geen fouten", async () => {
  // De seam mag het bestaande faalcontract niet veranderen: items=null blijft
  // de AI-uitval-degradatie van rb-api voeden (#281).
  deps.extractWithTool = (async () => ({
    items: null,
    failure: { reason: "no_tool_call" as const, detail: "run afgerond zonder tool-call" },
  })) as unknown as typeof deps.extractWithTool;

  const { status, body } = await post("/extract/interactions", interactionRequest);

  assert.equal(status, 500);
  assert.equal(body.reason, "no_tool_call");
});
