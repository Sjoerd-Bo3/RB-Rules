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
import { z } from "zod";
import type { ExtractOutcome } from "./ai.js";

// Vóór de import: server.ts leest PORT bij module-load en gaat meteen
// luisteren; 0 = een vrije efemere poort, zodat de suite nergens mee botst.
process.env.PORT = "0";
const { deps, server } = await import("./server.js");
const realExtract = deps.extractWithTool;
const realBatchExtract = deps.extractBatchWithTool;

let base = "";
before(async () => {
  if (!server.listening) await once(server, "listening");
  base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});
after(() => {
  deps.extractWithTool = realExtract;
  deps.extractBatchWithTool = realBatchExtract;
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

test("POST /extract/interactions: sections → governed_by, end-to-end door de handler (#315)", async () => {
  // De NAAD, niet alleen de pure functie: #315 was precies een bug die élke
  // unit-test miste — parse, prompt en narekening klopten stuk voor stuk, maar
  // het `sections`-veld werd op de request-grens nooit gelezen, dus het model
  // kon geen anker emitten en de muur nulde alles. Dit geval loopt daarom van
  // JSON-request tot JSON-respons door de échte handler.
  const calls = stubExtract([
    {
      from: "mechanic:Deflect",
      to: "mechanic:Assault",
      kind: "COUNTERS",
      interacts: true,
      governed_by: "section:core-4.2b",
    },
    {
      from: "mechanic:Assault",
      to: "mechanic:Deflect",
      kind: "MODIFIES",
      interacts: true,
      governed_by: "section:verzonnen",
    },
  ]);

  const { status, body } = await post("/extract/interactions", {
    ...interactionRequest,
    sections: ["section:core-4.2b"],
  });

  assert.equal(status, 200);
  assert.deepEqual(body.interactions, [
    {
      from: "mechanic:Deflect",
      to: "mechanic:Assault",
      kind: "COUNTERS",
      interacts: true,
      // De aangeboden sectie overleeft als anker (overleef-richting, #295-les)…
      governed_by: "section:core-4.2b",
    },
    {
      from: "mechanic:Assault",
      to: "mechanic:Deflect",
      kind: "MODIFIES",
      interacts: true,
      // …en de verzonnen sectie is ge-nuld terwijl het item bleef (muur-semantiek).
    },
  ]);

  // Prompt-bedrading: de aangeboden secties reizen als vocabulaire-regel mee.
  // Wordt `sections` niet meer geparset, dan verdwijnt deze regel én wordt het
  // geldige anker hierboven ge-nuld — beide asserts gaan dan rood.
  assert.equal(calls.length, 1);
  assert.match(String(calls[0]?.text), /governed_by \(sectie-refs\): section:core-4\.2b/);

  // Schema-bedrading: de vorm die de handler aan de SDK geeft moet governed_by
  // KENNEN, anders stript de zod-parse het veld vóór de narekening het ooit
  // ziet (de tweede helft van de #315-bug).
  const shape = z.object(calls[0]?.schema as z.ZodRawShape);
  const doorDeVorm = shape.parse({
    interactions: [
      { from: "a", to: "b", kind: "K", interacts: true, governed_by: "section:x" },
    ],
  }) as { interactions: Array<{ governed_by?: string | null }> };
  assert.equal(doorDeVorm.interactions[0]?.governed_by, "section:x");
});

test("POST /extract/interactions: zonder sections blijft het contract intact (pre-#286 rb-api)", async () => {
  // Backwards compatible: een aanroeper die geen sections stuurt krijgt exact
  // het oude gedrag — 200, en een toch geëmit anker wordt ge-nuld (dat deed de
  // .NET-muur met een lege sectionSet ook al).
  stubExtract([
    {
      from: "mechanic:Deflect",
      to: "mechanic:Assault",
      kind: "COUNTERS",
      interacts: true,
      governed_by: "section:core-4.2b",
    },
  ]);

  const { status, body } = await post("/extract/interactions", interactionRequest);

  assert.equal(status, 200);
  assert.deepEqual(body.interactions, [
    { from: "mechanic:Deflect", to: "mechanic:Assault", kind: "COUNTERS", interacts: true },
  ]);
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

// ── #323: model-alias en batch-endpoint, door de ECHTE handler ──────────────

import type { BatchExtractOutcome } from "./ai.js";

function stubBatch(
  outcome: BatchExtractOutcome,
  heartbeat?: Array<[string, number, number]>,
): Array<Record<string, unknown>> {
  const calls: Array<Record<string, unknown>> = [];
  deps.extractBatchWithTool = (async (opts: {
    onCapture?: (key: string, done: number, total: number) => void;
  }) => {
    calls.push(opts as Record<string, unknown>);
    // Simuleer binnenkomende tool-calls: de stub vuurt de heartbeat-callback
    // zoals de echte sessie dat per geaccepteerde kaart doet.
    for (const [key, done, total] of heartbeat ?? []) opts.onCapture?.(key, done, total);
    return outcome;
  }) as typeof deps.extractBatchWithTool;
  return calls;
}

/** POST die een NDJSON-antwoord als frames leest (batch-endpoint, #323). Bij
 * een non-200 blijft het gewoon één JSON-body — die komt dan als enig frame. */
const postNdjson = async (path: string, body: unknown) => {
  const res = await fetch(`${base}${path}`, {
    method: "POST",
    body: JSON.stringify(body),
  });
  const text = await res.text();
  const frames = text
    .split("\n")
    .filter((l) => l.trim())
    .map((l) => JSON.parse(l) as Record<string, unknown>);
  return { status: res.status, frames };
};

const batchRequest = {
  kinds: ["COUNTERS"],
  conditionKinds: ["WINDOW", "STATUS", "COST"],
  roles: ["agent", "patient"],
  windowLexicon: ["Showdown"],
  statusLexicon: ["Exhausted"],
  cards: [
    {
      code: "ogn-001",
      text: "Deflect prevents Assault damage.",
      refs: [
        { ref: "mechanic:Deflect", label: "Deflect" },
        { ref: "mechanic:Assault", label: "Assault" },
      ],
      sections: [],
    },
    {
      code: "ogn-002",
      text: "Tank reduces Snipe damage.",
      refs: [
        { ref: "mechanic:Tank", label: "Tank" },
        { ref: "mechanic:Snipe", label: "Snipe" },
      ],
      sections: [],
    },
    {
      code: "ogn-003",
      text: "Barrier hides a unit from Vision.",
      refs: [
        { ref: "mechanic:Barrier", label: "Barrier" },
        { ref: "mechanic:Vision", label: "Vision" },
      ],
      sections: [],
    },
  ],
};

test("POST /extract/interactions: onbekende model-alias → 400, vóór enige extractie", async () => {
  const calls = stubExtract([]);
  const { status } = await post("/extract/interactions", {
    ...interactionRequest,
    model: "gpt-5",
  });
  assert.equal(status, 400);
  assert.equal(calls.length, 0, "een geweigerde alias mag nooit een SDK-run kosten");
});

test("POST /extract/interactions: alias fable → LETTERLIJK claude-fable-5 in de extractie-opts", async () => {
  // Verificatiepunt 5: de alias-vertaling zit echt in de bedrading. Haal de
  // mapping weg (of geef de alias rauw door) en deze literal-assert gaat rood.
  const calls = stubExtract([]);
  const { status } = await post("/extract/interactions", {
    ...interactionRequest,
    model: "fable",
  });
  assert.equal(status, 200);
  assert.equal(calls[0]?.model, "claude-fable-5");
});

test("POST /extract/interactions/batch: onbekende alias → 400; te veel kaarten → 400", async () => {
  const calls = stubBatch({ perKey: new Map(), unknownKeys: 0, usage: null });
  const bad = await post("/extract/interactions/batch", { ...batchRequest, model: "gpt-5" });
  assert.equal(bad.status, 400);
  // 251 kaarten: één boven de uitgeschreven 250-grens (expliciete productkeuze).
  const teVeel = await post("/extract/interactions/batch", {
    ...batchRequest,
    cards: Array.from({ length: 251 }, (_, i) => ({
      ...batchRequest.cards[0]!, code: `ogn-${1000 + i}`,
    })),
  });
  assert.equal(teVeel.status, 400);
  assert.equal(calls.length, 0);
});

test("POST /extract/interactions/batch: partial salvage — 2 ok terug, 3e gefaald met reden", async () => {
  // Verificatiepunt 3, endpoint-kant: de sessie stierf (timeout) na 2 van de 3
  // kaarten. De 2 gaan als ok terug (rb-api watermarkt alléén die), de derde
  // draagt de sessie-reden. Wie hier K als resultaat zou melden (alle 3 ok)
  // maakt de per-kaart-asserts rood.
  stubBatch({
    perKey: new Map([
      ["ogn-001", [{ from: "mechanic:Deflect", to: "mechanic:Assault", kind: "COUNTERS", interacts: true }]],
      ["ogn-002", []],
    ]),
    unknownKeys: 1,
    usage: { inputTokens: 5200, outputTokens: 830 },
    timedOut: true,
    failure: { reason: "timeout", detail: "sessie afgekapt" },
  });

  const { status, frames } = await postNdjson("/extract/interactions/batch", batchRequest);

  assert.equal(status, 200);
  const done = frames.at(-1)!;
  assert.equal(done.type, "done");
  const results = done.results as Array<Record<string, unknown>>;
  assert.equal(results.length, 3);
  assert.deepEqual(results.map((r) => r.ok), [true, true, false]);
  assert.equal(results[2]?.code, "ogn-003");
  assert.equal(results[2]?.reason, "timeout");
  assert.equal((results[0]?.interactions as unknown[]).length, 1);
  assert.deepEqual(results[1]?.interactions, []);
  assert.equal(done.unknownCode, 1);
  // Kosten-doorvoer (#323): de sessie-usage reist mee zodat rb-api kan boeken
  // wat een batch van K kost.
  assert.deepEqual(done.usage, { inputTokens: 5200, outputTokens: 830 });
});

test("POST /extract/interactions/batch: de narekening draait PER KAART — kruisbesmetting wordt gepakt", async () => {
  // Verificatiepunt 2, endpoint-kant: de vangst van kaart ogn-001 bevat een
  // paar uit het vocabulaire van ogn-002. Een handler die tegen de UNIE van
  // vocabulaires zou narekenen laat het door; per-kaart narekenen weigert het.
  stubBatch({
    perKey: new Map([
      ["ogn-001", [
        { from: "mechanic:Tank", to: "mechanic:Snipe", kind: "COUNTERS", interacts: true },
        { from: "mechanic:Deflect", to: "mechanic:Assault", kind: "COUNTERS", interacts: true },
      ]],
      ["ogn-002", [
        { from: "mechanic:Tank", to: "mechanic:Snipe", kind: "COUNTERS", interacts: true },
      ]],
      ["ogn-003", []],
    ]),
    unknownKeys: 0,
    usage: null,
  });

  const { status, frames } = await postNdjson("/extract/interactions/batch", batchRequest);

  assert.equal(status, 200);
  const results = frames.at(-1)!.results as Array<Record<string, unknown>>;
  // Kaart 1: alleen het EIGEN paar overleeft; het besmette paar is geweigerd.
  assert.deepEqual(results[0]?.interactions, [
    { from: "mechanic:Deflect", to: "mechanic:Assault", kind: "COUNTERS", interacts: true },
  ]);
  // Kaart 2: hetzelfde Tank/Snipe-paar is dáár wél geldig — de overleef-kant
  // (#295-les): per-kaart weigeren mag geen geldige vangst elders kosten.
  assert.deepEqual(results[1]?.interactions, [
    { from: "mechanic:Tank", to: "mechanic:Snipe", kind: "COUNTERS", interacts: true },
  ]);
});

test("POST /extract/interactions/batch: niets gevangen → zelfde 504/500-vertaling als het losse pad", async () => {
  stubBatch({
    perKey: new Map(),
    unknownKeys: 0,
    usage: null,
    timedOut: true,
    failure: { reason: "timeout", detail: "batch-extractie afgebroken na 25s" },
  });
  const timeout = await post("/extract/interactions/batch", batchRequest);
  assert.equal(timeout.status, 504);
  assert.equal(timeout.body.code, "extract_timeout");
  assert.equal(timeout.body.reason, "timeout");

  stubBatch({
    perKey: new Map(),
    unknownKeys: 0,
    usage: null,
    failure: { reason: "no_tool_call", detail: "run afgerond met 0/3 aanroepen" },
  });
  const kaal = await post("/extract/interactions/batch", batchRequest);
  assert.equal(kaal.status, 500);
  assert.equal(kaal.body.reason, "no_tool_call");
});

test("POST /extract/interactions/batch: de prompt draagt de kaartkoppen en het model reist mee", async () => {
  const calls = stubBatch({
    perKey: new Map([["ogn-001", []], ["ogn-002", []], ["ogn-003", []]]),
    unknownKeys: 0,
    usage: null,
  });
  const { status } = await postNdjson("/extract/interactions/batch", {
    ...batchRequest,
    model: "opus",
  });
  assert.equal(status, 200);
  assert.equal(calls.length, 1);
  assert.equal(calls[0]?.model, "claude-opus-4-8");
  assert.deepEqual(calls[0]?.keys, ["ogn-001", "ogn-002", "ogn-003"]);
  assert.match(String(calls[0]?.text), /Kaart 1 van 3 — code: ogn-001/);
  assert.match(String(calls[0]?.text), /Kaart 3 van 3 — code: ogn-003/);
});

test("POST /extract/interactions/batch: heartbeat-frames per kaart komen vóór het done-frame", async () => {
  // Een K=250-sessie kan uren duren; de heartbeat is wat de job-voortgang in
  // beheer levend houdt. De stub vuurt onCapture zoals de echte sessie dat per
  // geaccepteerde tool-call doet — de frames moeten 1-op-1 op de stream staan.
  stubBatch(
    {
      perKey: new Map([["ogn-001", []], ["ogn-002", []]]),
      unknownKeys: 0,
      usage: null,
      failure: { reason: "timeout", detail: "sessie afgekapt" },
      timedOut: true,
    },
    [["ogn-001", 1, 3], ["ogn-002", 2, 3]],
  );

  const { status, frames } = await postNdjson("/extract/interactions/batch", batchRequest);

  assert.equal(status, 200);
  assert.deepEqual(frames.slice(0, 2), [
    { type: "card", code: "ogn-001", done: 1, total: 3 },
    { type: "card", code: "ogn-002", done: 2, total: 3 },
  ]);
  assert.equal(frames.at(-1)?.type, "done");
});
