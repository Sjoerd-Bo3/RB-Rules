import { test } from "node:test";
import assert from "node:assert/strict";
import {
  brainToolAllowlist,
  compactJson,
  createBrainSession,
  formatNeighbors,
  formatPath,
  formatSearch,
} from "./brain-tools.js";

// Fixtures spiegelen de contractvormen uit docs/BRAIN.md §2.3 (deelissue 2
// bestaat nog niet; dít is de vorm waartegen gebouwd is).

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

/** Mock-fetch die elke aanroep vastlegt en per aanroep een verse Response
 * maakt (een Response-body is maar één keer leesbaar). */
function capturingFetch(makeResponse: () => Response) {
  const urls: string[] = [];
  const impl: typeof fetch = async (input) => {
    urls.push(String(input));
    return makeResponse();
  };
  return { urls, impl };
}

const searchItems = [
  {
    ref: "section:CR/7.4.2",
    layer: "rules",
    title: "Deflect",
    snippet: "Deflect vermindert schade …",
    score: 0.912,
    trustLabel: "officieel",
  },
  {
    ref: "claim:42",
    layer: "claims",
    title: "Deflect en multi-hit",
    snippet: "Volgens de community …",
    score: 0.71,
    trustLabel: "community (corroboratie 2)",
  },
];

test("formatSearch: elk item draagt laag- en trust-label (§2.3/§2.4)", () => {
  const out = formatSearch(searchItems);
  assert.match(out, /\[laag=rules \| trust=officieel\] section:CR\/7\.4\.2 — Deflect/);
  assert.match(out, /\[laag=claims \| trust=community \(corroboratie 2\)\] claim:42/);
  assert.match(out, /score=0\.912/);
  // Wrapper-vorm {results:[…]} geeft hetzelfde resultaat (contract-aanname).
  assert.equal(formatSearch({ results: searchItems }), out);
});

test("formatSearch: leeg en onbekende vorm degraderen zonder informatieverlies", () => {
  assert.equal(formatSearch([]), "geen brein-resultaten voor deze zoekvraag.");
  // Onbekende vorm → compacte JSON-fallback, geen crash en niets weggooien.
  const raar = { totaal: 3, opmerking: "onverwachte wrapper" };
  assert.equal(formatSearch(raar), compactJson(raar));
});

test("formatNeighbors: richting/edge/ref per regel; NL 'richting' én 'direction'", () => {
  const out = formatNeighbors([
    { ref: "mechanic:Deflect", name: "Deflect", edge: "HAS_MECHANIC", richting: "uit" },
    { ref: "card:OGN-042", name: "Yasuo", edge: "INTERACTS_WITH", direction: "in", props: { note: "x" } },
  ]);
  assert.match(out, /- uit \[HAS_MECHANIC\] mechanic:Deflect \(Deflect\)/);
  assert.match(out, /- in \[INTERACTS_WITH\] card:OGN-042 \(Yasuo\) \{"note":"x"\}/);
  assert.equal(formatNeighbors([]), "geen buren gevonden voor deze ref.");
});

test("formatPath: keten [node, edge, node, …] wordt een leesbare bewijsketen (§2.3)", () => {
  const out = formatPath([
    { ref: "card:OGN-042", name: "Yasuo" },
    "HAS_MECHANIC",
    { ref: "mechanic:Deflect" },
    { edge: "EXPLAINS" },
    { ref: "concept:combat" },
  ]);
  assert.equal(
    out,
    "card:OGN-042 (Yasuo) -[HAS_MECHANIC]-> mechanic:Deflect -[EXPLAINS]-> concept:combat",
  );
  assert.equal(formatPath({ path: [] }), "geen pad gevonden tussen deze refs.");
});

test("compactJson: nulls weg, lange strings en lijsten afgekapt", () => {
  const out = compactJson({
    a: null,
    b: "x".repeat(500),
    c: Array.from({ length: 30 }, (_, i) => i),
  });
  assert.ok(!out.includes('"a"'), "null-velden verdwijnen");
  assert.ok(out.includes("…"), "lange string afgekapt");
  assert.ok(out.includes("(+5 meer)"), "lijst afgekapt met teller");
});

test("allowlist: exact de zes brein-tools in mcp__brain__-vorm (§2.4)", () => {
  assert.deepEqual(brainToolAllowlist(), [
    "mcp__brain__semantic_search",
    "mcp__brain__get_node",
    "mcp__brain__neighbors",
    "mcp__brain__path",
    "mcp__brain__evidence",
    "mcp__brain__contradictions",
  ]);
});

test("run: bouwt §2.3-paden en -parameters, refs URL-ge-encodeerd", async () => {
  const { urls, impl } = capturingFetch(() => jsonResponse(searchItems));
  const session = createBrainSession({ baseUrl: "http://stub:1/", fetchImpl: impl });

  await session.run("semantic_search", { q: "hoe werkt deflect", layers: "rules,claims", take: 5 });
  await session.run("get_node", { ref: "section:CR/7.4.2" });
  await session.run("neighbors", { ref: "card:OGN-042", edges: "INTERACTS_WITH", take: 10 });
  await session.run("path", { from: "card:OGN-042", to: "mechanic:Deflect", maxLen: 4 });
  await session.run("evidence", { claimRef: "claim:42" });
  await session.run("contradictions", { topic: "mechanic:Deflect" });

  assert.deepEqual(urls, [
    // §2.3: GET /api/brain/search?q=&layers=&take= (trailing slash van baseUrl gestript)
    "http://stub:1/api/brain/search?q=hoe+werkt+deflect&layers=rules%2Cclaims&take=5",
    // §2.3: GET /api/brain/node/{ref} — section-refs bevatten '/', dus %2F
    "http://stub:1/api/brain/node/section%3ACR%2F7.4.2",
    "http://stub:1/api/brain/neighbors/card%3AOGN-042?edges=INTERACTS_WITH&take=10",
    "http://stub:1/api/brain/path?from=card%3AOGN-042&to=mechanic%3ADeflect&maxLen=4",
    "http://stub:1/api/brain/evidence/claim%3A42",
    "http://stub:1/api/brain/contradictions?topic=mechanic%3ADeflect",
  ]);
  assert.equal(session.callCount(), 6);
});

test("run: rb-api onbereikbaar → 'brein niet beschikbaar' als toolresultaat, geen exception", async () => {
  const session = createBrainSession({
    baseUrl: "http://stub:1",
    fetchImpl: async () => {
      throw new TypeError("fetch failed: ECONNREFUSED");
    },
  });
  const out = await session.run("semantic_search", { q: "x" });
  assert.match(out, /^brein niet beschikbaar: fetch failed: ECONNREFUSED/);
});

test("run: Problem-responses worden leesbare, gelabelde resultaten (§2.3-degradatie)", async () => {
  const cases: Array<[unknown, number, RegExp]> = [
    [{ title: "Not Found", detail: "onbekende ref card:NOPE" }, 404, /^niet gevonden \(HTTP 404\): onbekende ref card:NOPE/],
    [{ detail: "ref-formaat ongeldig" }, 400, /^ongeldige brein-aanvraag \(HTTP 400\): ref-formaat ongeldig/],
    // Neo4j-uitval: de Problem-detail gaat 1-op-1 mee zodat de agent weet dat
    // alleen de graph plat ligt en semantisch verder kan (§2.3).
    [{ title: "Service Unavailable", detail: "graph niet beschikbaar (Neo4j)" }, 503, /^brein niet beschikbaar \(HTTP 503\): graph niet beschikbaar \(Neo4j\)/],
  ];
  for (const [body, status, expect] of cases) {
    const session = createBrainSession({
      baseUrl: "http://stub:1",
      fetchImpl: async () => jsonResponse(body, status),
    });
    assert.match(await session.run("neighbors", { ref: "card:NOPE" }), expect, `HTTP ${status}`);
  }
});

test("run: geen geldige JSON → duidelijk resultaat i.p.v. crash", async () => {
  const session = createBrainSession({
    baseUrl: "http://stub:1",
    fetchImpl: async () => new Response("<html>oops</html>", { status: 200 }),
  });
  assert.equal(
    await session.run("get_node", { ref: "card:OGN-042" }),
    "brein gaf geen geldige JSON terug (HTTP 200).",
  );
});

test("run: zonder RB_API_URL melden de tools de misconfiguratie expliciet", async () => {
  const session = createBrainSession({ baseUrl: undefined });
  assert.match(await session.run("semantic_search", { q: "x" }), /RB_API_URL is niet geconfigureerd/);
});

test("cap-gedrag: laatste call krijgt afrond-hint, daarboven geen fetch meer (§2.4)", async () => {
  const { urls, impl } = capturingFetch(() => jsonResponse(searchItems));
  const session = createBrainSession({ baseUrl: "http://stub:1", fetchImpl: impl, maxCalls: 2 });

  const eerste = await session.run("semantic_search", { q: "a" });
  assert.ok(!eerste.includes("laatste beschikbare brein-call"), "eerste call zonder hint");
  const tweede = await session.run("semantic_search", { q: "b" });
  assert.match(tweede, /laatste beschikbare brein-call/);
  const derde = await session.run("semantic_search", { q: "c" });
  assert.match(derde, /tool-call-limiet bereikt \(2\)/);

  assert.equal(urls.length, 2, "boven de cap wordt rb-api niet meer aangeroepen");
  assert.equal(session.callCount(), 3);
});

test("timeout: hangende rb-api → binnen de per-call-timeout een net resultaat", async () => {
  const hangend: typeof fetch = (_input, init) =>
    new Promise((_resolve, reject) => {
      init?.signal?.addEventListener("abort", () => reject(new Error("aborted")));
    });
  const session = createBrainSession({
    baseUrl: "http://stub:1",
    fetchImpl: hangend,
    timeoutMs: 20,
  });
  const out = await session.run("semantic_search", { q: "x" });
  assert.equal(out, "brein niet beschikbaar: geen antwoord van rb-api binnen 0.02s.");
});
