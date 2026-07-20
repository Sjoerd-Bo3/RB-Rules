import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import {
  AiRunError,
  describeThrown,
  failureOf,
  logCall,
  logEvent,
  redactSecrets,
  resultFailure,
  RetryTracker,
  retryNote,
  safeDetail,
  StderrTail,
  stderrDigestLine,
  withRetries,
  withStderr,
  withStderrDigest,
} from "./failure.js";

/** Vang wat `logCall` naar stdout schrijft, zodat een test de ECHTE logregel
 * kan inspecteren in plaats van een reconstructie ervan. */
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

/** Zet env-variabelen voor de duur van één test en herstel ze daarna exact
 * (ook het verschil tussen "leeg" en "afwezig"). */
function withEnv(vars: Record<string, string>, fn: () => void): void {
  const previous = new Map<string, string | undefined>();
  for (const [k, v] of Object.entries(vars)) {
    previous.set(k, process.env[k]);
    process.env[k] = v;
  }
  try {
    fn();
  } finally {
    for (const [k, v] of previous) {
      if (v === undefined) delete process.env[k];
      else process.env[k] = v;
    }
  }
}

// ── Redactie: werkafspraak 7 is hier hard ─────────────────────────────────
// Diagnostiek die het abonnementstoken lekt is erger dan geen diagnostiek. De
// hele reden dat rb-ai zo weinig logde was voorzichtigheid; deze tests maken
// die voorzichtigheid controleerbaar in plaats van impliciet.

test("redactSecrets verwijdert de waarde van elke TOKEN/KEY/SECRET-env", () => {
  withEnv(
    {
      CLAUDE_CODE_OAUTH_TOKEN: "geheim-abonnementstoken-1234567890",
      ANTHROPIC_API_KEY: "sleutelwaarde-abcdefgh",
      RB_API_URL: "http://rb-api:8080",
    },
    () => {
      const out = redactSecrets(
        "auth faalde met geheim-abonnementstoken-1234567890 richting http://rb-api:8080",
      );
      assert.doesNotMatch(out, /geheim-abonnementstoken/);
      assert.match(out, /\[redacted\]/);
      // Niet-geheime env-waarden blijven staan: die zijn juist diagnostisch.
      assert.match(out, /http:\/\/rb-api:8080/);
      assert.doesNotMatch(
        redactSecrets("de sleutel sleutelwaarde-abcdefgh werd geweigerd"),
        /sleutelwaarde-abcdefgh/,
      );
    },
  );
});

test("redactSecrets verwijdert bekende sleutel- en dragervormen", () => {
  const cases = [
    "Error: invalid x-api-key: sk-ant-api03-AAAABBBBCCCCDDDDEEEEFFFF",
    "request failed: Bearer eyJhbGciOiJIUzI1NiJ9abcdefghijkl",
    "authorization: Token abcdef1234567890abcdef",
    "config { api_key: 'sk-abcdefghijklmnopqrstuvwxyz012345' }",
  ];
  for (const raw of cases) {
    const out = redactSecrets(raw);
    assert.match(out, /\[redacted\]/, raw);
    assert.doesNotMatch(out, /sk-ant-api03-AAAA/, raw);
    assert.doesNotMatch(out, /eyJhbGciOiJIUzI1NiJ9abcdefghijkl/, raw);
  }
});

test("redactSecrets laat gewone diagnostiek intact", () => {
  const raw = "subtype=error_max_turns turns=3 api_status=529 (Overloaded)";
  assert.equal(redactSecrets(raw), raw);
});

test("een korte env-waarde wordt niet als secret behandeld (geen ruis-redactie)", () => {
  withEnv({ SOME_KEY: "ab" }, () => {
    // Zou 'ab' als secret gelden, dan werd elk woord met 'ab' erin onleesbaar.
    assert.equal(redactSecrets("abortverzoek van de client"), "abortverzoek van de client");
  });
});

test("safeDetail redacteert VÓÓR het afkapt — anders glipt een half token door", () => {
  // Volgorde-regressie (#281-review): kap je eerst af, dan kan MAX_DETAIL midden
  // door een token snijden en is het restant te kort voor de patronen.
  const token = `sk-ant-oat01-${"Z".repeat(40)}-staart`;
  withEnv({ CLAUDE_CODE_OAUTH_TOKEN: token }, () => {
    const detail = safeDetail(`${"x".repeat(280)} ${token}`);
    assert.doesNotMatch(detail, /sk-ant/);
    assert.equal(detail.includes(token.slice(0, 30)), false);
  });
});

test("describeThrown leest NOOIT e.stack — een stack trace lekt niets mee", () => {
  const token = "sk-ant-oat01-STACKGEHEIM-0123456789";
  withEnv({ CLAUDE_CODE_OAUTH_TOKEN: token }, () => {
    const e = new Error("kapot");
    e.stack = `Error: kapot\n    at auth (${token})\n    at run (/app/src/ai.ts:1:1)`;
    const f = describeThrown(e);
    assert.doesNotMatch(f.detail, /STACKGEHEIM/);
    assert.doesNotMatch(f.detail, /ai\.ts/, "stack-frames horen er sowieso niet in");
  });
});

test("safeDetail kapt lange meldingen af en normaliseert witruimte", () => {
  const detail = safeDetail(`regel1\n   regel2\t${"x".repeat(500)}`);
  assert.match(detail, /^regel1 regel2 x+…$/);
  assert.ok(detail.length <= 301, `detail is ${detail.length} tekens`);
});

test("GEEN ENKELE logregel bevat ooit het token — ook niet via reason/detail", () => {
  const token = "sk-ant-oat01-ZZZZZZZZ-geheim-token-nooit-loggen-99";
  withEnv({ CLAUDE_CODE_OAUTH_TOKEN: token }, () => {
    const lines = captureLog(() => {
      logCall({
        endpoint: "/extract/interactions",
        ms: 1234,
        status: 500,
        outcome: "error",
        reason: "auth",
        detail: `401 unauthorized voor ${token} (Bearer ${token})`,
      });
      logCall({ endpoint: "/ask", ms: 12, status: 200, outcome: "ok" });
    });
    assert.equal(lines.length, 2);
    for (const line of lines) {
      assert.doesNotMatch(line, /geheim-token/, line);
      assert.equal(line.includes(token), false, line);
    }
    // De regel blijft wél bruikbaar: endpoint, duur en oorzaak staan erin.
    const parsed = JSON.parse(lines[0]) as Record<string, unknown>;
    assert.equal(parsed.evt, "ai_call");
    assert.equal(parsed.endpoint, "/extract/interactions");
    assert.equal(parsed.ms, 1234);
    assert.equal(parsed.status, 500);
    assert.equal(parsed.outcome, "error");
    assert.equal(parsed.reason, "auth");
    assert.match(String(parsed.detail), /401 unauthorized/);
    assert.match(String(parsed.detail), /\[redacted\]/);
  });
});

test("logCall schrijft precies één parseerbare JSON-regel per aanroep", () => {
  const lines = captureLog(() => {
    logCall({ endpoint: "/ask/stream", ms: 40.7, status: 200, outcome: "ok" });
  });
  assert.equal(lines.length, 1);
  const parsed = JSON.parse(lines[0]) as Record<string, unknown>;
  assert.equal(parsed.ms, 41, "duur wordt afgerond, niet als float gelogd");
  assert.equal("reason" in parsed, false, "geen lege velden bij een geslaagde call");
  assert.equal("detail" in parsed, false);
  for (const veld of ["bytes", "refs", "items", "task"]) {
    assert.equal(veld in parsed, false, `${veld} hoort weg te blijven als het onbekend is`);
  }
});

test("payload-MATEN worden gelogd, payload-INHOUD nooit", () => {
  // De maat (bytes/refs/items) maakt "vallen juist de grote payloads om?"
  // toetsbaar zonder één teken kaarttekst te loggen (werkafspraak 7).
  const lines = captureLog(() => {
    logCall({
      endpoint: "/extract/interactions",
      ms: 90_000,
      status: 500,
      outcome: "error",
      bytes: 13_412,
      refs: 31,
      reason: "timeout",
      detail: "extractie afgebroken na 90s (harde timeout)",
    });
  });
  const parsed = JSON.parse(lines[0]) as Record<string, unknown>;
  assert.equal(parsed.bytes, 13412);
  assert.equal(parsed.refs, 31);
  // Een payload van 0 bytes is informatie, geen "onbekend" — die mag niet
  // wegvallen door een waarheids-check op het getal.
  const zero = captureLog(() => {
    logCall({ endpoint: "/ask", ms: 1, status: 400, outcome: "error", bytes: 0, items: 0 });
  });
  const parsedZero = JSON.parse(zero[0]) as Record<string, unknown>;
  assert.equal(parsedZero.bytes, 0);
  assert.equal(parsedZero.items, 0);
});

// ── resultFailure: het gat dat #281 blootlegde ────────────────────────────
// De Agent SDK GOOIT niet bij een mislukte run — ze eindigt met een gewoon
// result-bericht. rb-ai las daar alleen `result`/`usage` uit, dus elke
// mislukking werd een lege string en daarna een kale 500 zonder spoor.

test("een geslaagd result levert geen uitval op", () => {
  assert.equal(
    resultFailure({
      type: "result",
      subtype: "success",
      is_error: false,
      result: "antwoord",
      num_turns: 2,
    }),
    null,
  );
});

test("niet-result-berichten worden genegeerd", () => {
  for (const m of [
    { type: "assistant", message: { content: [] } },
    { type: "system", subtype: "init" },
    null,
    "tekst",
    42,
  ]) {
    assert.equal(resultFailure(m), null, JSON.stringify(m));
  }
});

test("error_max_turns wordt max_turns, met beurten in het detail", () => {
  const f = resultFailure({
    type: "result",
    subtype: "error_max_turns",
    is_error: true,
    num_turns: 3,
    errors: [],
  });
  assert.equal(f?.reason, "max_turns");
  assert.match(f!.detail, /subtype=error_max_turns/);
  assert.match(f!.detail, /turns=3/);
});

test("api_error_status wordt api_error — behalve 401/403, dat is auth", () => {
  const overloaded = resultFailure({
    type: "result",
    subtype: "success",
    is_error: true,
    api_error_status: 529,
  });
  assert.equal(overloaded?.reason, "api_error");
  assert.match(overloaded!.detail, /api_status=529/);

  for (const status of [401, 403]) {
    const f = resultFailure({
      type: "result",
      subtype: "success",
      is_error: true,
      api_error_status: status,
    });
    assert.equal(f?.reason, "auth", `status ${status}`);
  }
});

test("error_during_execution met een subprocess-spoor wordt spawn, anders sdk_error", () => {
  const oom = resultFailure({
    type: "result",
    subtype: "error_during_execution",
    is_error: true,
    errors: ["spawn claude ENOMEM"],
  });
  assert.equal(oom?.reason, "spawn");
  assert.match(oom!.detail, /ENOMEM/);

  const generic = resultFailure({
    type: "result",
    subtype: "error_during_execution",
    is_error: true,
    errors: ["stream ended unexpectedly"],
  });
  assert.equal(generic?.reason, "sdk_error");
});

test("een geweigerde tool wordt permission_denied", () => {
  const f = resultFailure({
    type: "result",
    subtype: "success",
    is_error: false,
    permission_denials: [{ tool_name: "mcp__extract__emit_interactions" }],
  });
  assert.equal(f?.reason, "permission_denied");
  assert.match(f!.detail, /permission_denials=1/);
});

test("budget- en structured-output-limieten worden sdk_limit", () => {
  for (const subtype of ["error_max_budget_usd", "error_max_structured_output_retries"]) {
    const f = resultFailure({ type: "result", subtype, is_error: true });
    assert.equal(f?.reason, "sdk_limit", subtype);
  }
});

test("resultFailure redacteert de errors[]-tekst", () => {
  withEnv({ CLAUDE_CODE_OAUTH_TOKEN: "token-dat-nooit-mag-lekken-4242" }, () => {
    const f = resultFailure({
      type: "result",
      subtype: "error_during_execution",
      is_error: true,
      errors: ["auth met token-dat-nooit-mag-lekken-4242 geweigerd"],
    });
    assert.doesNotMatch(f!.detail, /token-dat-nooit-mag-lekken/);
  });
});

// ── SDK-interne retries: de gemeten oorzaak van #281 ──────────────────────
//
// Een probe met een ongeldige sleutel liet zien dat de Agent SDK een mislukte
// API-call zelf tot 10× opnieuw probeert met exponentiële backoff (gemeten:
// 0,5s → 1,0s → 2,3s → 4,5s → 9,6s → 16,4s → 32,1s → …). Na zeven pogingen is
// er 37 seconden verstreken zónder één verwerkt token; poging 8 en 9 duwen het
// totaal ruim voorbij de 90s-extract-timeout. Een aanhoudende 429/529 kwam
// daardoor naar buiten als ONZE timeout — niet als de API-fout die het was, en
// zonder één logregel. Deze tests leggen dat mechanisme vast.

/** Het echte bericht dat de SDK uitzendt (letterlijk overgenomen uit de probe). */
function apiRetry(attempt: number, status: number | null, error: string) {
  return {
    type: "system",
    subtype: "api_retry",
    attempt,
    max_retries: 10,
    retry_delay_ms: 539.9195123044176,
    error_status: status,
    error,
    session_id: "d0225cd9",
  };
}

test("retryNote leest een api_retry-bericht en negeert de rest", () => {
  const note = retryNote(apiRetry(3, 429, "rate_limit_error"));
  assert.deepEqual(note, {
    attempt: 3,
    maxRetries: 10,
    status: 429,
    error: "rate_limit_error",
  });
  for (const other of [
    { type: "system", subtype: "init" },
    { type: "result", subtype: "success" },
    null,
    "tekst",
  ]) {
    assert.equal(retryNote(other), null, JSON.stringify(other));
  }
});

test("een verbindingsfout zonder HTTP-antwoord heeft status null", () => {
  const t = new RetryTracker();
  t.observe(apiRetry(1, null, "connection_error"));
  assert.equal(t.retries, 1);
  assert.match(t.summary(), /geen HTTP-antwoord/);
  assert.equal(t.reason(), "api_error");
});

test("RetryTracker telt alleen retries en vat de laatste samen", () => {
  const t = new RetryTracker();
  assert.equal(t.retries, 0);
  assert.equal(t.summary(), "");
  assert.equal(t.reason(), null);

  t.observe({ type: "assistant", message: { content: [] } });
  assert.equal(t.retries, 0, "gewone berichten tellen niet mee");

  t.observe(apiRetry(1, 529, "overloaded_error"));
  t.observe(apiRetry(2, 429, "rate_limit_error"));

  assert.equal(t.retries, 2);
  assert.equal(t.summary(), "2 SDK-retries, van max 10, laatste: HTTP 429 rate_limit_error");
  assert.equal(t.reason(), "api_error");
});

test("retries op 401/403 wijzen naar het token, niet naar de belasting", () => {
  for (const status of [401, 403]) {
    const t = new RetryTracker();
    t.observe(apiRetry(1, status, "authentication_failed"));
    assert.equal(t.reason(), "auth", `status ${status}`);
  }
});

test("KERN VAN #281: een timeout ná SDK-retries is een API-fout, geen trage LLM", () => {
  const t = new RetryTracker();
  for (let i = 1; i <= 7; i++) t.observe(apiRetry(i, 429, "rate_limit_error"));

  const enriched = withRetries(
    { reason: "timeout", detail: "extractie afgebroken na 90s (harde timeout)" },
    t,
  );

  // De reden wijst nu naar de échte knop — dit is precies het onderscheid dat
  // 22 mislukte kaarten onverklaarbaar maakte.
  assert.equal(enriched.reason, "api_error");
  // ... maar de timeout blijft leesbaar: de run is wél afgekapt.
  assert.match(enriched.detail, /harde timeout/);
  assert.match(enriched.detail, /7 SDK-retries/);
  assert.match(enriched.detail, /HTTP 429 rate_limit_error/);
});

test("zonder retries blijft een timeout gewoon een timeout", () => {
  const failure = { reason: "timeout" as const, detail: "afgebroken na 90s" };
  assert.deepEqual(withRetries(failure, new RetryTracker()), failure);
});

test("een NIET-timeout-oorzaak blijft leidend; retries gaan als context mee", () => {
  const t = new RetryTracker();
  t.observe(apiRetry(1, 500, "api_error"));

  // max_turns/spawn zijn eigen diagnoses — die mag een retry-telling niet
  // overschrijven, anders wijst de meting de verkeerde kant op.
  const enriched = withRetries({ reason: "max_turns", detail: "subtype=error_max_turns" }, t);
  assert.equal(enriched.reason, "max_turns");
  assert.match(enriched.detail, /1 SDK-retries/);
});

// ── Stderr-staart van het subprocess ──────────────────────────────────────

test("StderrTail houdt alleen de staart vast en redacteert bij het uitlezen", () => {
  withEnv({ CLAUDE_CODE_OAUTH_TOKEN: "token-uit-de-subprocess-stderr-777" }, () => {
    const tail = new StderrTail(64);
    tail.append("x".repeat(200));
    tail.append(" auth met token-uit-de-subprocess-stderr-777 mislukt");
    const out = tail.tail();
    assert.ok(out.length <= 64, `staart is ${out.length} tekens`);
    assert.doesNotMatch(out, /token-uit-de-subprocess/);
  });
});

test("een stille stderr voegt niets toe aan de melding", () => {
  const failure = { reason: "timeout" as const, detail: "afgebroken na 90s" };
  assert.deepEqual(withStderr(failure, new StderrTail()), failure);
});

test("withStderr plakt de staart achter het detail, met behoud van de reden", () => {
  const tail = new StderrTail();
  tail.append("Error: connect ECONNREFUSED api.anthropic.com:443");
  const enriched = withStderr({ reason: "spawn", detail: "subprocess om" }, tail);
  assert.equal(enriched.reason, "spawn");
  assert.match(enriched.detail, /subprocess om \| stderr: .*ECONNREFUSED/);
});

// ── describeThrown: fouten die WEL geworpen worden ────────────────────────

test("describeThrown deelt de gangbare faalvormen in", () => {
  const cases: Array<[unknown, string]> = [
    [Object.assign(new Error("aborted by user"), { name: "AbortError" }), "aborted"],
    [new Error("401 Unauthorized: invalid api key"), "auth"],
    [new Error("spawn /usr/bin/claude ENOMEM"), "spawn"],
    [new Error("request timed out after 90s"), "timeout"],
    [new Error("API error 529 Overloaded"), "api_error"],
    [new Error("iets volstrekt onbekends"), "unknown"],
  ];
  for (const [error, reason] of cases) {
    assert.equal(describeThrown(error).reason, reason, String(error));
  }
});

test("de LETTERLIJKE subprocess-meldingen van de Agent SDK worden herkend", () => {
  // Deze drie strings komen uit de SDK zelf (sdk.mjs, getProcessExitError en de
  // spawn-handler). Een omgevallen subprocess is de meest waarschijnlijke stille
  // 5xx op een krappe VM — juist die mag niet als "unknown" wegvallen, want dan
  // wijst de diagnose nergens heen.
  const sdkMessages = [
    "Failed to spawn Claude Code process: EACCES",
    "Claude Code process exited with code 137",
    "Claude Code process terminated by signal SIGKILL",
  ];
  for (const message of sdkMessages) {
    assert.equal(describeThrown(new Error(message)).reason, "spawn", message);
  }
});

test("describeThrown leest de cause mee — daar zit de echte systeemfout", () => {
  const wrapped = new Error("query failed", { cause: new Error("spawn ENOMEM") });
  const f = describeThrown(wrapped);
  assert.equal(f.reason, "spawn");
  assert.match(f.detail, /cause: Error: spawn ENOMEM/);
});

test("describeThrown overleeft een niet-Error worp", () => {
  const f = describeThrown("kapot");
  assert.equal(f.reason, "unknown");
  assert.match(f.detail, /kapot/);
});

test("failureOf behoudt een al vastgestelde reden en verzint er geen nieuwe", () => {
  const known = new AiRunError({ reason: "max_turns", detail: "subtype=error_max_turns" });
  assert.deepEqual(failureOf(known), {
    reason: "max_turns",
    detail: "subtype=error_max_turns",
  });
  // De melding blijft leesbaar voor wie alleen String(e) ziet (bv. de
  // bestaande fout-body van het agentic pad).
  assert.equal(String(known), "AiRunError: max_turns: subtype=error_max_turns");
  assert.equal(failureOf(new Error("spawn ENOMEM")).reason, "spawn");
});

// ── logEvent: de poort zelf (#292) ────────────────────────────────────────
// #281 zette de redactie-poort neer, maar twee oudere console.log's in ai.ts
// liepen er omheen. Deze tests toetsen de poort op GEDRAG: wat komt er echt uit
// stdout als je hem probeert te voeden met iets gevaarlijks?

test("logEvent redacteert ELK veld — ook eentje dat geen string is", () => {
  const token = "sk-ant-oat01-YYYYYYYY-geheim-token-uit-een-object-77";
  withEnv({ CLAUDE_CODE_OAUTH_TOKEN: token }, () => {
    const lines = captureLog(() => {
      logEvent("proef", {
        tekst: `auth faalde met ${token}`,
        brok: { header: `Bearer ${token}`, endpoint: "/extract/interactions", refs: 31 },
        fout: new Error(`401 unauthorized voor ${token}`),
      });
    });
    assert.equal(lines.length, 1);
    assert.equal(lines[0].includes(token), false, lines[0]);
    assert.doesNotMatch(lines[0], /geheim-token/, lines[0]);
    const parsed = JSON.parse(lines[0]) as Record<string, unknown>;
    assert.equal(parsed.evt, "proef");
    assert.match(String(parsed.tekst), /auth faalde met \[redacted\]/);
    assert.match(String(parsed.fout), /401 unauthorized/);

    // DEZE DRIE ASSERTS DRAGEN DE TEST (#295-review). Zonder hen kan hij niet
    // falen bij de bug die hij bewaakt: als het object stilletjes tot
    // "[object Object]" wordt platgeslagen is het token er óók niet, en dan is
    // "geen token in de regel" groen zonder dat er iets geredacteerd is.
    // Precies de klasse test die deze PR zelf bekritiseert. Door te eisen dat
    // de NIET-GEHEIME inhoud overleeft, bewijst hij dat er geserialiseerd én
    // geredacteerd wordt in plaats van vernietigd.
    assert.match(String(parsed.brok), /"endpoint":"\/extract\/interactions"/);
    assert.match(String(parsed.brok), /"refs":31/);
    assert.match(String(parsed.brok), /\[redacted\]/);
  });
});

test("logEvent behoudt de cause-keten van een Error, net als describeThrown", () => {
  // `describeThrown` leest `cause` bewust wél (de SDK stopt daar de echte
  // systeemfout in). Toen `logEvent` `String(e)` gebruikte, gooide het die
  // keten weg — twee helpers in één bestand met een tegengesteld oordeel
  // (#295-review).
  const lines = captureLog(() => {
    logEvent("proef", {
      fout: new Error("wrapper", { cause: new Error("ENOMEM spawn mislukt") }),
    });
  });
  const parsed = JSON.parse(lines[0]) as Record<string, unknown>;
  assert.match(String(parsed.fout), /wrapper/);
  assert.match(String(parsed.fout), /ENOMEM spawn mislukt/, "de cause is de échte oorzaak");
  assert.equal(describeThrown(new Error("w", { cause: new Error("ENOMEM") })).reason, "spawn");
});

test("logEvent: eventnaam is onoverschrijfbaar en NaN wordt geen null", () => {
  const lines = captureLog(() => {
    logEvent("ai_call", { evt: "iets_anders", ms: Number.NaN, refs: Number.POSITIVE_INFINITY });
  });
  const parsed = JSON.parse(lines[0]) as Record<string, unknown>;
  assert.equal(parsed.evt, "ai_call", "een veld 'evt' mag de eventnaam niet kapen");
  // Via de getallen-tak zou JSON.stringify hier `null` van maken, en dan is een
  // kapotte meting niet te onderscheiden van een ontbrekende (#282).
  assert.equal(parsed.ms, "NaN");
  assert.equal(parsed.refs, "Infinity");
});

test("logEvent overleeft een circulaire structuur zonder te gooien", () => {
  const kringetje: Record<string, unknown> = { naam: "slot" };
  kringetje.zelf = kringetje;
  const lines = captureLog(() => {
    logEvent("proef", { brok: kringetje });
  });
  assert.equal(lines.length, 1, "een logregel mag de aanroeper nooit omver trekken");
  assert.equal((JSON.parse(lines[0]) as Record<string, unknown>).evt, "proef");
});

test("logEvent laat getallen met rust en gooit lege velden weg — 0 blijft staan", () => {
  const lines = captureLog(() => {
    logEvent("proef", { bytes: 0, tools: 3, warm: false, weg: undefined, ook_weg: null });
  });
  const parsed = JSON.parse(lines[0]) as Record<string, unknown>;
  assert.equal(parsed.bytes, 0, "een maat van 0 is informatie, geen 'onbekend'");
  assert.equal(parsed.tools, 3);
  assert.equal(parsed.warm, false);
  assert.equal("weg" in parsed, false);
  assert.equal("ook_weg" in parsed, false);
});

test("logEvent schrijft precies één parseerbare JSON-regel", () => {
  const lines = captureLog(() => {
    logEvent("proef", { detail: "regel1\n   regel2" });
  });
  assert.equal(lines.length, 1);
  assert.equal((JSON.parse(lines[0]) as Record<string, unknown>).detail, "regel1 regel2");
});

// ── Eén poort, geen tweede pad (#292) ─────────────────────────────────────

/** Wie verwijst naar stdout/stderr? Op de IDENTIFIER, niet op de aanroepvorm
 * (#295-review): vier gemeten omzeilingen van een `console\.\w+\(`-patroon —
 * `globalThis.console.log(…)`, `console["log"](…)`, `const c = console; c.log(…)`
 * en `process.stdout.write.bind(…)` — moeten allemaal nog steeds de naam
 * noemen, dus daar zit de regel op.
 *
 * ÉÉN definitie, gedeeld door de scan en de test die de scan toetst. Met een
 * kopie in elk van beide kan de meta-test groen blijven terwijl de echte scan
 * iets anders doet — dan toetst hij zichzelf in plaats van de scan. */
const STDOUT_PATROON = String.raw`\bconsole\b|\bprocess\s*\.\s*std(?:out|err)\b`;

/** Vervang commentaar, string-/template-literalen en regex-literalen door
 * spaties, met behoud van nieuwe regels zodat regelnummers blijven kloppen.
 *
 * Nodig omdat de scan hieronder anders vals-positief gaat op de waarschuwing
 * die je erover schrijft (#295-review) — en omdat naïef commentaar strippen
 * juist een BLINDE VLEK maakt: `fetch("http://x"); console.log(y)` verliest bij
 * een kale `//`-strip zijn echte aanroep. Vandaar een kleine scanner in plaats
 * van een regex. Regex-literalen worden herkend met de gangbare heuristiek (een
 * `/` na een operator/opener start een regex, anders is het deling), zodat een
 * patroon met aanhalingstekens erin de string-state niet ontregelt.
 *
 * Bewuste grens: code binnen een `${…}`-interpolatie telt als string. Een
 * console-aanroep verstoppen in een template-interpolatie is geen realistische
 * omzeiling, en dit houdt de scanner klein. */
function stripNonCode(src: string): string {
  const uit: string[] = [];
  const leeg = (van: number, tot: number) => {
    for (let k = van; k < tot && k < src.length; k++) uit.push(src[k] === "\n" ? "\n" : " ");
  };
  // Na deze tekens is een `/` een regex-start; na een naam/getal/haakje-dicht
  // is het deling.
  const regexNa = /[=(,:[!&|?{};+\-*%~^<>]/;
  let vorigeCode = "";
  let i = 0;
  while (i < src.length) {
    const c = src[i];
    const d = src[i + 1];
    if (c === "/" && d === "/") {
      const eind = src.indexOf("\n", i);
      const tot = eind === -1 ? src.length : eind;
      leeg(i, tot);
      i = tot;
      continue;
    }
    if (c === "/" && d === "*") {
      const eind = src.indexOf("*/", i + 2);
      const tot = eind === -1 ? src.length : eind + 2;
      leeg(i, tot);
      i = tot;
      continue;
    }
    if (c === '"' || c === "'" || c === "`") {
      let j = i + 1;
      while (j < src.length && src[j] !== c) j += src[j] === "\\" ? 2 : 1;
      leeg(i, j + 1);
      i = Math.min(j + 1, src.length);
      vorigeCode = "x"; // een string gedraagt zich als waarde: `/` erna = deling
      continue;
    }
    if (c === "/" && regexNa.test(vorigeCode)) {
      let j = i + 1;
      let inKlasse = false;
      while (j < src.length && (inKlasse || src[j] !== "/")) {
        if (src[j] === "\\") j++;
        else if (src[j] === "[") inKlasse = true;
        else if (src[j] === "]") inKlasse = false;
        j++;
      }
      leeg(i, j + 1);
      i = Math.min(j + 1, src.length);
      vorigeCode = "x";
      continue;
    }
    uit.push(c);
    if (!/\s/.test(c)) vorigeCode = c;
    i++;
  }
  return uit.join("");
}

test("failure.ts is de ENIGE module in rb-ai die naar stdout schrijft", () => {
  // WAT DEZE TEST WEL EN NIET IS. Hij is structureel: hij leest broncode, geen
  // gedrag. Dat maakt hem principieel zwakker dan de tests hierboven — hij ziet
  // niet of een logregel geredacteerd is, alleen wie er schrijft, en een
  // hernoeming of verplaatsing kan hem laten struikelen zonder dat er iets mis
  // is. Hij staat hier voor precies één ding dat gedragstests niet kunnen
  // dekken: het BESTAAN van een tweede stdout-pad. #292 ontstond niet doordat de
  // poort verkeerd redacteerde, maar doordat twee regels er langs liepen — en
  // geen enkele test kon dat zien, want ze testten alleen de poort.
  //
  // De regel is daarom bewust NIET "geen template-interpolatie in console.log"
  // (die vorm-check faalt op een refactor en mist een concatenatie of een
  // String(e)), maar "alleen failure.ts schrijft". Alles wat de sidecar wil
  // melden gaat via logEvent/logCall; wie dat wil omzeilen moet deze test
  // aanpassen, en dat is precies het moment waarop iemand moet nadenken.
  // RECURSIEF (#295-review): een niet-recursieve scan mist `src/log/sneaky.ts`,
  // en dan handhaaft de test iets smallers dan de regel die CLAUDE.md vastlegt.
  const dir = fileURLToPath(new URL(".", import.meta.url));
  const modules = readdirSync(dir, { recursive: true })
    .map(String)
    .filter((f) => f.endsWith(".ts") && !f.endsWith(".test.ts") && f !== "failure.ts");
  assert.ok(modules.length >= 5, `verwacht meerdere modules, kreeg ${modules.length}`);

  const schrijvers = new RegExp(STDOUT_PATROON, "g");
  for (const naam of modules) {
    const src = readFileSync(`${dir}${naam}`, "utf8");
    const treffers = [...stripNonCode(src).matchAll(schrijvers)].map((m) => {
      const regel = src.slice(0, m.index ?? 0).split("\n").length;
      return `${naam}:${regel}`;
    });
    assert.deepEqual(
      treffers,
      [],
      `${treffers.join(", ")} verwijst rechtstreeks naar stdout/stderr. ` +
        "Gebruik logEvent/logCall uit failure.ts — die redacteert (werkafspraak 7).",
    );
  }
});

test("de stdout-scan kijkt door commentaar en strings heen, en ziet omzeilingen", () => {
  // De structurele test hierboven is een grep, en een grep is broos. Deze test
  // meet die broosheid in beide richtingen, zodat we niet hoeven te gokken.
  const heeftTreffer = (src: string) => new RegExp(STDOUT_PATROON).test(stripNonCode(src));

  // VALS-POSITIEF (#295-review): wie de waarschuwing opschrijft die de nieuwe
  // CLAUDE.md-valkuil uitnodigt, mag de build niet breken.
  assert.equal(heeftTreffer("// Nooit console.log(step) gebruiken hier — zie #292.\nconst a = 1;"), false);
  assert.equal(heeftTreffer("/* console.error is verboden */\nconst a = 1;"), false);
  assert.equal(heeftTreffer('const uitleg = "gebruik geen console.log";'), false);
  // Een `//` BINNEN een string mag de rest van de regel niet blind maken.
  assert.equal(heeftTreffer('fetch("http://rb-api:8080"); console.log(x);'), true);
  // Een regex-literal met aanhalingstekens mag de scanner niet ontsporen.
  assert.equal(heeftTreffer("const r = /[\"']/g;\nconst a = 1;"), false);
  assert.equal(heeftTreffer("const r = /[\"']/g;\nconsole.log(a);"), true);

  // VALS-NEGATIEF: de vier gemeten omzeilingen.
  for (const vorm of [
    "console.log(x);",
    'console["log"](x);',
    "globalThis.console.log(x);",
    "const c = console;\nc.log(x);",
    "const w = process.stdout.write.bind(process.stdout);\nw(x);",
    "process.stderr.write(x);",
  ]) {
    assert.equal(heeftTreffer(vorm), true, `omzeiling niet gezien: ${vorm}`);
  }
});

// ── De stderr-staart op een pad MÉT gebruikersinvoer (#300) ────────────────
//
// Dezelfde ringbuffer, twee leesvormen. `tail()` (extract) geeft alles;
// `digest()` (/ask) geeft alleen de regels uit een gesloten machine-vocabulaire
// plus maten. De motivering om het prompt-residu te aanvaarden was nooit
// "stderr is ongevaarlijk" maar "op dit endpoint is de invoer publieke
// Riot-tekst"; op /ask is de invoer de vraag van een bezoeker en valt dezelfde
// afweging andersom uit.

test("digest: machine-regels komen mee, de rest wordt geteld en niet geciteerd", () => {
  const stderr = new StderrTail();
  stderr.append("bezig met opstarten\n");
  stderr.append("Claude Code process exited with code 137\n");
  stderr.append("iets anders onbegrijpelijks\n");

  const d = stderr.digest();
  assert.deepEqual(d.machine, ["Claude Code process exited with code 137"]);
  assert.equal(d.withheld, 2, "de twee niet-herkende regels horen geteld te worden");
  assert.ok(d.bytes > 0);
});

test("digest: de bytes-telling overleeft de ringbuffer (maat ≠ inhoud)", () => {
  // Klein limiet zodat er gegarandeerd inhoud uit de buffer schuift. De MAAT
  // moet dan nog steeds het TOTAAL melden — anders lijkt een subprocess dat
  // 40 kB uitbraakte even spraakzaam als een dat één regel zei.
  const stderr = new StderrTail(20);
  stderr.append("a".repeat(100));
  stderr.append("b".repeat(100));
  assert.equal(stderr.digest().bytes, 200);
});

test("digest: geen uitvoer ⇒ geen regel, en de uitval blijft ongewijzigd", () => {
  const stderr = new StderrTail();
  const failure = { reason: "spawn" as const, detail: "oorspronkelijk" };
  assert.equal(stderrDigestLine(stderr), "");
  assert.deepEqual(withStderrDigest(failure, stderr), failure);
});

test("digest: hoogstens drie regels, en een lange regel wordt gekapt", () => {
  const stderr = new StderrTail(8000);
  for (let i = 0; i < 6; i++) stderr.append(`Claude Code process exited with code ${i}\n`);
  stderr.append(`FATAL ERROR ${"x".repeat(500)}\n`);
  const d = stderr.digest();
  assert.equal(d.machine.length, 3, "de cap op het aantal gemelde regels moet gelden");
  assert.equal(d.withheld, 4, "wat boven de cap valt telt als niet-gemeld");
  for (const line of d.machine) assert.ok(line.length <= 160, `regel te lang: ${line.length}`);
});

test("/ask-staart: de vraag van de bezoeker overleeft NIET, de machineregel WEL", () => {
  // De kern van #300 én van de #292-les in één assert-paar. Een test die
  // alleen "de vraag staat er niet in" toetst, zou ook slagen als de hele
  // staart weggegooid werd — dan bewijst hij niets over de diagnostiek. Dus
  // beide richtingen, altijd.
  const vraag = "mag Yasuo blokkeren als mijn buurman Sjoerd hem exhaust";
  const stderr = new StderrTail();
  stderr.append(`[debug] prompt: ${vraag}\n`);
  stderr.append("Claude Code process terminated by signal SIGKILL\n");

  const { detail } = withStderrDigest({ reason: "spawn", detail: "run omgevallen" }, stderr);

  assert.ok(detail.includes("SIGKILL"), `machine-diagnostiek weg: ${detail}`);
  assert.ok(detail.includes("run omgevallen"), `oorspronkelijke reden weg: ${detail}`);
  for (const woord of ["Yasuo", "Sjoerd", "blokkeren", "buurman", "exhaust"]) {
    assert.equal(
      detail.toLowerCase().includes(woord.toLowerCase()),
      false,
      `"${woord}" uit de vraag van de bezoeker staat in de toelichting: ${detail}`,
    );
  }
  // …maar het FEIT dat er meer was, blijft zichtbaar: stil weglaten zou de
  // beheerder laten denken dat het subprocess niets zei (#282-les).
  assert.match(detail, /niet gemeld/);
});

test("/ask-staart: een secret in een machineregel wordt geredacteerd, de rest overleeft", () => {
  // #295-review: een redactietest die alleen "het secret is weg" toetst is
  // vacuüm zodra de renderer de inhoud sowieso vernietigt. Dus ook hier de
  // tweede assert — de niet-geheime helft van dezelfde regel moet er staan.
  const token = "sk-ant-api03-DIT_IS_GEHEIM_1234567890";
  const stderr = new StderrTail();
  stderr.append(`Failed to spawn Claude Code process: auth=${token} ENOENT\n`);

  const { detail } = withStderrDigest({ reason: "spawn", detail: "start mislukt" }, stderr);

  assert.equal(detail.includes(token), false, `token gelekt: ${detail}`);
  assert.ok(detail.includes("Failed to spawn"), `diagnostiek vernietigd i.p.v. geredacteerd: ${detail}`);
  assert.ok(detail.includes("ENOENT"), detail);
});

test("het extract-pad houdt de VOLLEDIGE staart — de twee vormen zijn niet inwisselbaar", () => {
  // Regressiegrens: wie dit ooit "opruimt" tot één vorm moet expliciet kiezen
  // welke kant op. De volledige staart is op /extract bewust aanvaard
  // (publieke Riot-kaarttekst, ARCHITECTURE §6.6); hem daar vervangen door de
  // digest kost diagnostiek zonder iets te winnen.
  const stderr = new StderrTail();
  stderr.append("Aegis of the Legion — kaarttekst die niemand geheim hoeft te houden\n");

  assert.match(withStderr({ reason: "spawn", detail: "x" }, stderr).detail, /Aegis of the Legion/);
  assert.equal(
    withStderrDigest({ reason: "spawn", detail: "x" }, stderr).detail.includes("Aegis"),
    false,
    "de /ask-vorm hoort juist NIET te citeren",
  );
});

test("digest: natuurlijke vragen die een machine-woord bevatten LEKKEN niet (#300-review)", () => {
  // Finding 1: het passthrough-vocabulaire hergebruikte AUTH_PATTERNS +
  // `\bKilled\b` — gewone woorden die een speler tikt. Gemeten lekten 6 van 8
  // natuurlijke vragen hun hele regel. De passthrough matcht nu op tokens die
  // geen natuurlijke taal zijn (errno/signalen) of op aan `^` verankerde
  // machine-prefixen, dus zo'n vraag-echo hoort geteld te worden, niet geciteerd.
  const vragen = [
    "If my unit is Killed during combat, does its ability still trigger?",
    "Is the Forbidden Idol banned in the current format?",
    "Why is my token invalid when I attach it to a champion?",
    "Does a 401 error mean my deck is unauthorized in the event?",
    "Is attacking forbidden while my unit is exhausted?",
    "What happens when a process exits — does the chain resolve?",
  ];
  for (const vraag of vragen) {
    const stderr = new StderrTail();
    stderr.append(`${vraag}\n`);
    const d = stderr.digest();
    assert.deepEqual(
      d.machine,
      [],
      `natuurlijke vraag doorgelaten als machine-regel: ${JSON.stringify(d.machine)}`,
    );
    assert.equal(d.withheld, 1, `vraag niet geteld: ${vraag}`);
  }
});

test("digest: echte machine-regels gaan WÉL door (tegenproef bij #300-review)", () => {
  // De keerzijde van de test hierboven: het verankeren mag de echte
  // diagnostiek niet wegfilteren. Anders "beschermt" de digest door niets meer
  // te melden — dan verklaart hij geen enkele crash.
  const regels = [
    "Failed to spawn Claude Code process: ENOENT",
    "Claude Code process exited with code 137",
    "Claude Code process terminated by signal SIGKILL",
    "FATAL ERROR: Reached heap limit Allocation failed - JavaScript heap out of memory",
    "TypeError: Cannot read properties of undefined",
    "Killed",
    "read ECONNRESET",
  ];
  for (const regel of regels) {
    const stderr = new StderrTail();
    stderr.append(`${regel}\n`);
    assert.equal(stderr.digest().machine.length, 1, `machine-regel weggefilterd: ${regel}`);
  }
});

test("digest: machine[] is ZELF geredacteerd, vóór het afkappen (#281-regel)", () => {
  // `digest()` is publiek en `machine[]` kan rechtstreeks gelezen worden (niet
  // alleen via stderrDigestLine's slot-redactie). Een secret dat in een
  // doorgelaten machine-regel meelift mag daar dus al uit zijn — en de redactie
  // hoort VÓÓR de 160-teken-kap te gebeuren, anders snijdt de kap een token
  // doormidden en glipt het restant langs de patronen. De machine-prefix blijft.
  const token = "sk-ant-api03-DIGEST_MACHINE_GEHEIM_0123456789";
  const stderr = new StderrTail(4000);
  stderr.append(`Failed to spawn Claude Code process: token=${token}\n`);
  const [line] = stderr.digest().machine;
  assert.ok(line, "de machine-regel is er niet");
  assert.equal(line.includes(token), false, `token onveilig in machine[]: ${line}`);
  assert.ok(line.includes("Failed to spawn"), `diagnostiek vernietigd: ${line}`);
});

test("stderrDigestLine redacteert zelf — een derde afnemer erft de poort", () => {
  // Beide huidige afnemers redacteren al (withStderrDigest via safeDetail,
  // logEvent via de poort). Deze test bewaakt de functie zélf, want ze geeft
  // per definitie ongecontroleerde subprocess-uitvoer terug en "de aanroeper
  // doet het wel" is precies de aanname die #292 duur maakte.
  const token = "sk-ant-api03-DERDE_AFNEMER_GEHEIM_0123456789";
  const stderr = new StderrTail();
  stderr.append(`Failed to spawn Claude Code process: token=${token} ENOMEM\n`);

  const line = stderrDigestLine(stderr);
  assert.equal(line.includes(token), false, `token gelekt: ${line}`);
  // En weer: de niet-geheime helft moet overleven, anders bewijst de assert
  // hierboven alleen dat er iets vernietigd is (#295-review).
  assert.ok(line.includes("Failed to spawn"), line);
  assert.ok(line.includes("ENOMEM"), line);
});
