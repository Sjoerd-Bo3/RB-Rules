import { z } from "zod";

// Brein-tools voor task="agentic" (#106, docs/BRAIN.md §2.3–§2.4): zes dunne
// clients op de brein-API van rb-api. Dit bestand bevat de tooldefinities én
// de fetch-laag, bewust ZONDER Agent SDK — ai.ts verpakt ze met
// createSdkMcpServer/tool; alles hier is unit-testbaar met een gemockte fetch.
//
// LET OP — de brein-API (deelissue 2, #105) bestaat nog niet: alles hieronder
// is exact gebouwd tegen het contract in docs/BRAIN.md §2.3. Elk aangenomen
// detail dat §2.3 niet letterlijk vastlegt staat bij de betreffende regel in
// een comment met §-verwijzing, zodat de integratie straks een no-op is (of
// een bewuste, vindbare afwijking).

export type BrainToolName =
  | "semantic_search"
  | "get_node"
  | "neighbors"
  | "path"
  | "evidence"
  | "contradictions";

/** MCP-servernaam; bepaalt de allowlist-vorm `mcp__brain__<tool>` (§2.4). */
export const BRAIN_SERVER_NAME = "brain";

export interface BrainToolDef {
  name: BrainToolName;
  description: string;
  /** Zod raw shape — de Agent SDK's tool() valideert hiermee de invoer. */
  schema: z.ZodRawShape;
  /** Bouwt pad+query voor de brein-API; refs worden URL-ge-encodeerd. */
  request: (args: Record<string, unknown>) => BrainRequest;
  format: (payload: unknown) => string;
}

interface BrainRequest {
  path: string;
  query?: Record<string, string | undefined>;
}

// ---------------------------------------------------------------------------
// Compacte weergave: de agent krijgt per §2.4 "compacte, gelabelde JSON" —
// laag-/trust-labels komen uit de brein-API zelf en blijven altijd zichtbaar.
// Voor lijstvormen (search/neighbors/path) renderen we regels i.p.v. rauwe
// JSON: minder tokens, labels vooraan. Onbekende vormen vallen terug op
// compacte JSON zodat een contract-afwijking nooit informatie weggooit.

const MAX_ITEMS = 30; // plafond per lijst-resultaat (token-budget)
const MAX_SNIPPET = 300;
const MAX_STRING = 400;
const MAX_ARRAY = 25;

function truncate(s: string, max: number): string {
  return s.length > max ? `${s.slice(0, max)}…` : s;
}

function cleanForJson(v: unknown): unknown {
  if (v === null || v === undefined) return undefined;
  if (typeof v === "string") return truncate(v, MAX_STRING);
  if (Array.isArray(v)) {
    const out = v
      .slice(0, MAX_ARRAY)
      .map(cleanForJson)
      .filter((x) => x !== undefined);
    if (v.length > MAX_ARRAY) out.push(`… (+${v.length - MAX_ARRAY} meer)`);
    return out;
  }
  if (typeof v === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, val] of Object.entries(v as Record<string, unknown>)) {
      const c = cleanForJson(val);
      if (c !== undefined) out[k] = c;
    }
    return out;
  }
  return v;
}

/** JSON zonder nulls, met afgekapte strings/lijsten — nooit een muur tekst. */
export function compactJson(payload: unknown): string {
  return JSON.stringify(cleanForJson(payload)) ?? "null";
}

function asRecord(v: unknown): Record<string, unknown> {
  return typeof v === "object" && v !== null ? (v as Record<string, unknown>) : {};
}

function str(v: unknown): string {
  return typeof v === "string" ? v : "";
}

/** Vind de resultatenlijst: §2.3 beschrijft de items, niet de wrapper.
 * Aanname: deelissue 2 levert óf een platte JSON-array, óf `{<key>: [...]}`
 * (bv. {results: [...]}); beide accepteren maakt de integratie een no-op. */
function itemList(payload: unknown, key: string): unknown[] | undefined {
  if (Array.isArray(payload)) return payload;
  const wrapped = asRecord(payload)[key];
  return Array.isArray(wrapped) ? wrapped : undefined;
}

/** §2.3 search: items `{ref, layer, title, snippet, score, trustLabel}`.
 * Elke regel begint met het laag-/trust-label zodat de kennispiramide in de
 * agent-loop zichtbaar blijft (§2.4). */
export function formatSearch(payload: unknown): string {
  const items = itemList(payload, "results");
  if (!items) return compactJson(payload);
  if (items.length === 0) return "geen brein-resultaten voor deze zoekvraag.";
  const lines = items.slice(0, MAX_ITEMS).map((raw) => {
    const it = asRecord(raw);
    const layer = str(it.layer) || "?";
    const trust = str(it.trustLabel) || "?";
    const title = str(it.title);
    const snippet = str(it.snippet);
    const score = typeof it.score === "number" ? ` (score=${it.score.toFixed(3)})` : "";
    return (
      `- [laag=${layer} | trust=${trust}] ${str(it.ref) || "?"}` +
      (title ? ` — ${title}` : "") +
      (snippet ? `: ${truncate(snippet, MAX_SNIPPET)}` : "") +
      score
    );
  });
  if (items.length > MAX_ITEMS) lines.push(`… (+${items.length - MAX_ITEMS} meer)`);
  return lines.join("\n");
}

/** §2.3 neighbors: items `{ref, name, edge, richting, props}`. Het veld heet
 * in §2.3 letterlijk "richting" (NL); we accepteren ook "direction" voor het
 * geval deelissue 2 het contract-Engels trekt. Relaties met een kind-property
 * (#116: RELATES_TO, INTERACTS_WITH) tonen kind en uitleg expliciet in de
 * regel — het dynamische vocabulaire is juist de informatie. */
export function formatNeighbors(payload: unknown): string {
  const items = itemList(payload, "neighbors");
  if (!items) return compactJson(payload);
  if (items.length === 0) return "geen buren gevonden voor deze ref.";
  const lines = items.slice(0, MAX_ITEMS).map((raw) => {
    const it = asRecord(raw);
    const richting = str(it.richting) || str(it.direction) || "?";
    const name = str(it.name);
    const props = asRecord(it.props);
    const kind = str(props.kind);
    const explanation = str(props.explanation);
    const rest = cleanForJson(
      Object.fromEntries(
        Object.entries(props).filter(([k]) => k !== "kind" && k !== "explanation"),
      ),
    );
    const restJson =
      rest && typeof rest === "object" && Object.keys(rest).length > 0
        ? ` ${JSON.stringify(rest)}`
        : "";
    return (
      `- ${richting} [${str(it.edge) || "?"}${kind ? `:${kind}` : ""}] ${str(it.ref) || "?"}` +
      (name ? ` (${name})` : "") +
      (explanation ? ` — ${truncate(explanation, MAX_SNIPPET)}` : "") +
      restJson
    );
  });
  if (items.length > MAX_ITEMS) lines.push(`… (+${items.length - MAX_ITEMS} meer)`);
  return lines.join("\n");
}

/** §2.3 path: "kortste pad als keten [node, edge, node, …]" — de bewijsketen.
 * Knopen zijn objecten met minimaal {ref} (en evt. name); edges zijn strings
 * of objecten met {edge} of {type} — sinds #116 dragen kind-dragende edges
 * (RELATES_TO) ook {kind}, dat als `EDGE:kind` in de keten verschijnt.
 * Andere vormen → compacte JSON. */
export function formatPath(payload: unknown): string {
  const chain = itemList(payload, "path");
  if (!chain) return compactJson(payload);
  if (chain.length === 0) return "geen pad gevonden tussen deze refs.";
  const parts: string[] = [];
  for (const el of chain) {
    if (typeof el === "string") {
      parts.push(`-[${el}]->`);
      continue;
    }
    const o = asRecord(el);
    const ref = str(o.ref);
    if (ref) {
      const name = str(o.name);
      parts.push(name ? `${ref} (${name})` : ref);
    } else {
      const edge = str(o.edge) || str(o.type);
      const kind = str(o.kind);
      if (edge) parts.push(`-[${edge}${kind ? `:${kind}` : ""}]->`);
      else return compactJson(payload); // onbekende ketenvorm: niets weggooien
    }
  }
  return parts.join(" ");
}

/** node/evidence/contradictions: §2.3 geeft de velden, niet de exacte vorm —
 * compacte JSON geeft alles (laag, trust, status, bronnen+citaat) door zonder
 * aannames over veldnamen. */
export function formatJson(payload: unknown): string {
  return compactJson(payload);
}

// ---------------------------------------------------------------------------
// De zes tooldefinities — paden en parameters letterlijk uit §2.3.

export const BRAIN_TOOLS: BrainToolDef[] = [
  {
    name: "semantic_search",
    description:
      "Zoek semantisch over alle kennislagen van het brein (rules, cards, claims, primer, rulings). " +
      "Resultaten dragen laag- en trust-labels: officieel > geverifieerde rulings > primer > community.",
    schema: {
      q: z.string().describe("Zoekvraag in natuurlijke taal"),
      layers: z
        .string()
        .optional()
        // §2.3: pgvector per laag "(rules, cards, claims, primer, rulings)".
        .describe("Optioneel kommagescheiden laagfilter: rules,cards,claims,primer,rulings"),
      take: z.number().int().min(1).max(20).optional().describe("Max resultaten per laag"),
    },
    request: (a) => ({
      path: "/api/brain/search",
      query: {
        q: str(a.q),
        layers: str(a.layers) || undefined,
        take: typeof a.take === "number" ? String(a.take) : undefined,
      },
    }),
    format: formatSearch,
  },
  {
    name: "get_node",
    description:
      "Haal één brein-knoop op via zijn ref: eigenschappen, kennislaag en provenance (trust/status/§-refs). " +
      "Ref-vormen (§2.1): card:<riftboundId>, mechanic:<naam>, concept:<topic>, section:<bron>/<code>, " +
      "claim:<id>, ruling:<id>, erratum:<id>, change:<id>, source:<id>, set:<id>, domain:<naam>, tag:<naam>.",
    schema: { ref: z.string().describe("BrainRef, bv. card:OGN-123 of section:CR/1.2.3") },
    // §2.3: GET /api/brain/node/{ref}. Refs bevatten ':' en (bij section:)
    // '/', dus encodeURIComponent; deelissue 2 moet de route daarop matchen
    // (catch-all of decode) — zie §2.1 voor de ref-grammatica.
    request: (a) => ({ path: `/api/brain/node/${encodeURIComponent(str(a.ref))}` }),
    format: formatJson,
  },
  {
    name: "neighbors",
    description:
      "Buren van een knoop in de kennisgraaf, optioneel gefilterd op edge-types " +
      "(bv. HAS_MECHANIC, INTERACTS_WITH, ABOUT, EXPLAINS, SUPPORTED_BY, SUPERSEDES, AFFECTS, PART_OF, RELATES_TO) " +
      "en op kind (#116: de relatiesoort van dynamische relaties, bv. counters of enables).",
    schema: {
      ref: z.string().describe("BrainRef van de startknoop"),
      edges: z.string().optional().describe("Optioneel kommagescheiden edge-typefilter"),
      kind: z
        .string()
        .optional()
        .describe("Optioneel kind-filter op relaties met een kind-property, bv. counters"),
      take: z.number().int().min(1).max(50).optional().describe("Max buren"),
    },
    request: (a) => ({
      path: `/api/brain/neighbors/${encodeURIComponent(str(a.ref))}`,
      query: {
        edges: str(a.edges) || undefined,
        kind: str(a.kind) || undefined,
        take: typeof a.take === "number" ? String(a.take) : undefined,
      },
    }),
    format: formatNeighbors,
  },
  {
    name: "path",
    description:
      "Kortste pad tussen twee refs in de kennisgraaf — de bewijsketen die twee begrippen verbindt. " +
      "Met kind-filter volgen kind-dragende schakels (RELATES_TO) alleen dat kind.",
    schema: {
      from: z.string().describe("BrainRef van het startpunt"),
      to: z.string().describe("BrainRef van het eindpunt"),
      // §2.3: maxLen=4 is de default in het contract; wij cappen op 6.
      maxLen: z.number().int().min(1).max(6).optional().describe("Max padlengte (default 4)"),
      kind: z
        .string()
        .optional()
        .describe("Optioneel kind-filter op relaties met een kind-property, bv. counters"),
    },
    request: (a) => ({
      path: "/api/brain/path",
      query: {
        from: str(a.from),
        to: str(a.to),
        maxLen: typeof a.maxLen === "number" ? String(a.maxLen) : undefined,
        kind: str(a.kind) || undefined,
      },
    }),
    format: formatPath,
  },
  {
    name: "evidence",
    description:
      "Bewijs voor één community-claim: statement, corroboratie, trust-score, officiële toets " +
      "en de bronnen met citaat en URL. Community blijft ondergeschikt aan officiële lagen.",
    schema: { claimRef: z.string().describe("claim:<id>-ref") },
    request: (a) => ({ path: `/api/brain/evidence/${encodeURIComponent(str(a.claimRef))}` }),
    format: formatJson,
  },
  {
    name: "contradictions",
    description:
      "Open conflicten en weerlegde/vervangen claims rond een topic — om community-beweringen te toetsen. " +
      "Weerlegde claims zijn géén kennis; noem ze hooguit expliciet als weerlegd.",
    schema: { topic: z.string().describe("Topic-ref of onderwerp, bv. mechanic:Deflect") },
    request: (a) => ({ path: "/api/brain/contradictions", query: { topic: str(a.topic) } }),
    format: formatJson,
  },
];

/** Allowlist-namen voor de Agent SDK (§2.4): alléén de brein-tools, in de
 * MCP-naamvorm `mcp__<server>__<tool>` — géén web, géén bash. */
export function brainToolAllowlist(): string[] {
  return BRAIN_TOOLS.map((t) => `mcp__${BRAIN_SERVER_NAME}__${t.name}`);
}

// ---------------------------------------------------------------------------
// Fetch-laag met tool-call-cap (§2.4: "cap ~12 calls") en per-call-timeout.
// run() gooit NOOIT: elke fout wordt een leesbaar toolresultaat — fouten zijn
// data (CONVENTIONS.md), de agent redeneert er semantisch mee verder (§2.3).

export interface BrainSessionOptions {
  /** RB_API_URL (compose-intern, bv. http://rb-api:8080). Ontbreekt hij, dan
   * melden de tools dat expliciet i.p.v. naar een gegokte host te fetchen. */
  baseUrl: string | undefined;
  /** Injecteerbaar voor tests; default de globale fetch. */
  fetchImpl?: typeof fetch;
  /** Per-HTTP-call-timeout: een hangende rb-api mag de harde agentic-timeout
   * (ai.ts) niet in zijn eentje opsouperen. */
  timeoutMs?: number;
  maxCalls?: number;
}

export interface BrainSession {
  run(name: BrainToolName, args: Record<string, unknown>): Promise<string>;
  callCount(): number;
}

const DEFAULT_HTTP_TIMEOUT_MS = 10_000;
const DEFAULT_MAX_CALLS = 12; // §2.4

function errText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

function buildUrl(baseUrl: string, req: BrainRequest): string {
  const base = baseUrl.replace(/\/+$/, "");
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(req.query ?? {})) {
    if (v !== undefined && v !== "") params.set(k, v);
  }
  const qs = params.toString();
  return `${base}${req.path}${qs ? `?${qs}` : ""}`;
}

/** Probeer een Problem-response (§2.3: "nette Problem-response met detail")
 * leesbaar te maken; anders het (afgekapte) rauwe body-fragment. */
function problemDetail(body: string): string {
  try {
    const p = asRecord(JSON.parse(body));
    const detail = str(p.detail) || str(p.title) || str(p.error);
    if (detail) return detail;
  } catch {
    // geen JSON — val terug op het rauwe fragment hieronder
  }
  return truncate(body.trim(), 200);
}

export function createBrainSession(opts: BrainSessionOptions): BrainSession {
  const fetchImpl = opts.fetchImpl ?? fetch;
  const timeoutMs = opts.timeoutMs ?? DEFAULT_HTTP_TIMEOUT_MS;
  const maxCalls = opts.maxCalls ?? DEFAULT_MAX_CALLS;
  let calls = 0;

  async function run(name: BrainToolName, args: Record<string, unknown>): Promise<string> {
    // Cap éérst: ook mislukte calls kosten een beurt, anders is de rem lek.
    calls += 1;
    if (calls > maxCalls) {
      return `tool-call-limiet bereikt (${maxCalls}): doe geen nieuwe tool-calls en formuleer nu je eindantwoord met wat je al weet.`;
    }
    const def = BRAIN_TOOLS.find((t) => t.name === name);
    if (!def) return `onbekende brein-tool: ${name}`;
    if (!opts.baseUrl) {
      return "brein niet beschikbaar: RB_API_URL is niet geconfigureerd op de rb-ai-container.";
    }

    const url = buildUrl(opts.baseUrl, def.request(args));
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    let status: number;
    let body: string;
    try {
      const res = await fetchImpl(url, {
        signal: controller.signal,
        headers: { accept: "application/json" },
      });
      status = res.status;
      body = await res.text();
    } catch (e) {
      // rb-api onbereikbaar of te traag → toolresultaat, geen exception: de
      // agent kan verder met wat hij heeft (issue #106-verificatiepad).
      return controller.signal.aborted
        ? `brein niet beschikbaar: geen antwoord van rb-api binnen ${timeoutMs / 1000}s.`
        : `brein niet beschikbaar: ${errText(e)}`;
    } finally {
      clearTimeout(timer);
    }

    if (status === 404) return `niet gevonden (HTTP 404): ${problemDetail(body) || "onbekende ref"}`;
    if (status >= 400 && status < 500)
      return `ongeldige brein-aanvraag (HTTP ${status}): ${problemDetail(body)}`;
    if (status >= 500)
      // §2.3-degradatie: Neo4j-uitval geeft een Problem-response met detail op
      // neighbors/path; die detail-tekst gaat 1-op-1 mee zodat de agent weet
      // wát er niet beschikbaar is (bv. alleen de graph).
      return `brein niet beschikbaar (HTTP ${status}): ${problemDetail(body)}`;

    let payload: unknown;
    try {
      payload = JSON.parse(body);
    } catch {
      return `brein gaf geen geldige JSON terug (HTTP ${status}).`;
    }
    const result = def.format(payload);
    return calls === maxCalls
      ? `${result}\n\n(let op: dit was de laatste beschikbare brein-call — rond je antwoord nu af)`
      : result;
  }

  return { run, callCount: () => calls };
}
