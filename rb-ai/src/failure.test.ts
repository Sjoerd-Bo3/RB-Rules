import assert from "node:assert/strict";
import { test } from "node:test";
import {
  AiRunError,
  describeThrown,
  failureOf,
  logCall,
  redactSecrets,
  resultFailure,
  RetryTracker,
  retryNote,
  safeDetail,
  StderrTail,
  withRetries,
  withStderr,
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
