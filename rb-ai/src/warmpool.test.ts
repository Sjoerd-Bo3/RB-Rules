import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { fileURLToPath } from "node:url";
import { StderrTail } from "./failure.js";
import {
  pushableInput,
  signatureKey,
  WarmPool,
  type WarmBootHandle,
  type WarmSignature,
} from "./warmpool.js";

const SIG: WarmSignature = { systemPrompt: "rewrite-prompt", includePartialMessages: false };
const OTHER: WarmSignature = { systemPrompt: "antwoord-prompt", includePartialMessages: true };

const tick = () => new Promise<void>((r) => setTimeout(r, 0));
const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Nep-boot: gedraagt zich als een SDK-sessie met bestuurbare berichtenstroom. */
function fakeBoot() {
  const boots: Array<{
    sig: WarmSignature;
    pushed: unknown[];
    inputEnded: boolean;
    killed: boolean;
    emit: (m: unknown) => void;
    end: () => void;
    fail: (e: unknown) => void;
    stderr: StderrTail;
  }> = [];
  const boot = (sig: WarmSignature): WarmBootHandle => {
    const out = pushableInput<unknown>();
    let failure: unknown;
    const record = {
      sig,
      pushed: [] as unknown[],
      inputEnded: false,
      killed: false,
      emit: out.push,
      end: out.end,
      fail: (e: unknown) => {
        failure = e;
        out.end();
      },
      // Elke geboote sessie krijgt een EIGEN staart (#300), net als in
      // bootWarmCheapSession — zo kan een test toetsen dat de pool ze niet
      // door elkaar haalt.
      stderr: new StderrTail(),
    };
    boots.push(record);
    async function* messages() {
      for await (const m of out.iterable) yield m;
      if (failure !== undefined) throw failure;
    }
    return {
      messages: messages(),
      stderr: record.stderr,
      push: (m) => record.pushed.push(m),
      endInput: () => {
        record.inputEnded = true;
      },
      kill: () => {
        record.killed = true;
        out.end();
      },
    };
  };
  return { boot, boots };
}

function pool(overrides: { enabled?: boolean; ttlMs?: number } = {}) {
  const { boot, boots } = fakeBoot();
  const p = new WarmPool({
    boot,
    enabled: overrides.enabled ?? true,
    ttlMs: overrides.ttlMs ?? 60_000,
    log: () => {},
  });
  return { p, boots };
}

test("signatureKey: undefined en lege string zijn verschillende signaturen", () => {
  assert.notEqual(
    signatureKey({ includePartialMessages: false }),
    signatureKey({ systemPrompt: "", includePartialMessages: false }),
  );
  assert.notEqual(
    signatureKey({ systemPrompt: "x", includePartialMessages: false }),
    signatureKey({ systemPrompt: "x", includePartialMessages: true }),
  );
});

test("pushableInput: push vóór en ná het lezen, end sluit af", async () => {
  const q = pushableInput<number>();
  q.push(1);
  const it = q.iterable[Symbol.asyncIterator]();
  assert.deepEqual(await it.next(), { value: 1, done: false });
  const pending = it.next();
  q.push(2);
  assert.deepEqual(await pending, { value: 2, done: false });
  q.end();
  assert.equal((await it.next()).done, true);
});

test("kill-switch: uitgeschakelde pool doet helemaal niets", async () => {
  const { p, boots } = pool({ enabled: false });
  assert.deepEqual(p.prewarm(), { enabled: false, booted: false, reason: "kill-switch" });
  p.observe(SIG);
  assert.equal(p.claim(SIG), null);
  assert.equal(boots.length, 0);
  assert.equal(p.stats().signatures, 0);
});

test("prewarm zonder geleerde signatuur boot niets (maar opent het venster)", () => {
  const { p, boots } = pool();
  const r = p.prewarm();
  assert.deepEqual(r, { enabled: true, booted: false, reason: "nog geen signatuur geleerd" });
  assert.equal(boots.length, 0);
  assert.equal(p.stats().windowActive, true);
});

test("observe buiten het activiteitsvenster leert niets (mining-bulk telt niet mee)", () => {
  const { p } = pool();
  p.observe(SIG); // geen prewarm geweest → venster dicht
  assert.equal(p.stats().signatures, 0);
  p.prewarm();
  p.observe(SIG); // venster open → leert wél
  assert.equal(p.stats().signatures, 1);
});

test("claim-en-verversing: miss leert en herverwarmt, daarna hit met bericht-doorvoer", async () => {
  const { p, boots } = pool();
  p.prewarm(); // venster open, nog niets geleerd
  p.observe(SIG);
  assert.equal(p.claim(SIG), null); // eerste vraag: koud
  assert.equal(boots.length, 1); // …maar het venster is actief → herverwarmd
  assert.equal(signatureKey(boots[0].sig), signatureKey(SIG));

  const claimed = p.claim(SIG);
  assert.ok(claimed, "tweede claim hoort de warme sessie te krijgen");
  assert.equal(p.stats().hits, 1);
  claimed.send({ vraag: "hoi" });
  assert.deepEqual(boots[0].pushed, [{ vraag: "hoi" }]);
  assert.equal(boots[0].inputEnded, true, "één sessie = één call: input dicht na push");

  boots[0].emit({ type: "system" });
  boots[0].emit({ type: "result", result: "antwoord" });
  boots[0].end();
  const got: unknown[] = [];
  for await (const m of claimed.messages()) got.push(m);
  assert.deepEqual(got, [{ type: "system" }, { type: "result", result: "antwoord" }]);
  p.destroy();
});

test("signatuur-mismatch laat de warme sessie staan voor de call die wél past", () => {
  const { p, boots } = pool();
  p.prewarm();
  p.observe(SIG);
  p.claim(SIG); // miss → herverwarmt SIG
  assert.equal(boots.length, 1);
  p.observe(OTHER);
  assert.equal(p.claim(OTHER), null, "andere signatuur mag de sessie niet claimen");
  assert.equal(boots[0].killed, false, "…en sloopt hem ook niet");
  assert.ok(p.claim(SIG), "de passende call krijgt hem daarna gewoon");
  p.destroy();
});

test("TTL: ongebruikte warme sessie gaat weg en wordt buiten het venster niet vervangen", async () => {
  const { p, boots } = pool({ ttlMs: 30 });
  p.prewarm();
  p.observe(SIG);
  p.claim(SIG); // miss → boot 1
  assert.equal(boots.length, 1);
  await sleep(70); // voorbij TTL én venster
  assert.equal(boots[0].killed, true, "verlopen warme sessie is gesloten");
  assert.equal(p.stats().expired, 1);
  assert.equal(p.claim(SIG), null);
  assert.equal(boots.length, 1, "geen herverwarming buiten het activiteitsvenster");
});

test("hit binnen het venster herverwarmt direct asynchroon", () => {
  const { p, boots } = pool();
  p.prewarm();
  p.observe(SIG);
  p.claim(SIG); // miss → boot 1
  assert.ok(p.claim(SIG)); // hit → boot 2 (vervanger)
  assert.equal(boots.length, 2);
  assert.equal(p.stats().hits, 1);
  p.destroy();
});

test("idle gestorven sessie: slot leeg, claim degradeert naar koud", async () => {
  const { p, boots } = pool();
  p.prewarm();
  p.observe(SIG);
  p.claim(SIG); // miss → boot 1
  boots[0].end(); // subprocess sterft terwijl de sessie idle is
  await tick();
  assert.equal(p.stats().deadIdle, 1);
  assert.equal(p.stats().slot, "leeg");
  assert.equal(p.claim(SIG), null, "claim na de dood gaat transparant koud");
  assert.equal(boots.length, 2, "…en binnen het venster komt er een verse");
  p.destroy();
});

test("fout in de warme stroom bereikt de claimende leeslus", async () => {
  const { p, boots } = pool();
  p.prewarm();
  p.observe(SIG);
  p.claim(SIG);
  const claimed = p.claim(SIG);
  assert.ok(claimed);
  claimed.send({ v: 1 });
  boots[0].emit({ type: "assistant" });
  boots[0].fail(new Error("subprocess kapot"));
  const got: unknown[] = [];
  await assert.rejects(async () => {
    for await (const m of claimed.messages()) got.push(m);
  }, /subprocess kapot/);
  assert.deepEqual(got, [{ type: "assistant" }]);
  p.destroy();
});

test("abort-doorwerking: kill op de claim breekt de onderliggende sessie af", () => {
  const { p, boots } = pool();
  p.prewarm();
  p.observe(SIG);
  p.claim(SIG);
  const claimed = p.claim(SIG);
  assert.ok(claimed);
  claimed.kill(); // ai.ts koppelt dit aan de AbortController van de call
  assert.equal(boots[0].killed, true);
  p.destroy();
});

test("boot-fout is gedegradeerd gedrag: teller omhoog, pool blijft bruikbaar", () => {
  let calls = 0;
  const p = new WarmPool({
    boot: () => {
      calls += 1;
      throw new Error("spawn faalt");
    },
    enabled: true,
    ttlMs: 60_000,
    log: () => {},
  });
  p.prewarm();
  p.observe(SIG);
  assert.equal(p.claim(SIG), null);
  assert.equal(calls, 1);
  assert.equal(p.stats().bootFailures, 1);
  assert.equal(p.claim(SIG), null, "volgende claim probeert het gewoon opnieuw");
  assert.equal(calls, 2);
});

test("boot-fout lekt geen token, ook niet via een geïnjecteerde log-sink (#292)", () => {
  // De default-sink loopt door `logEvent` en is dus per constructie geredacteerd
  // — maar een MEEGEGEVEN sink (zoals hier, en zoals elke test) omzeilt die
  // poort. Daarom wordt de tekst óók bij de bron al geclassificeerd en
  // geredacteerd: een boot-fout is een rauwe SDK-melding, en die kan een
  // auth-header dragen (werkafspraak 7).
  const token = "sk-ant-oat01-XXXXXXXX-warmpool-boot-token-42";
  const regels: string[] = [];
  const p = new WarmPool({
    boot: () => {
      throw new Error(`spawn faalde — Authorization: Bearer ${token}`);
    },
    enabled: true,
    ttlMs: 60_000,
    log: (line) => regels.push(line),
  });
  p.prewarm();
  p.observe(SIG);
  p.claim(SIG);

  assert.equal(regels.length, 1);
  assert.equal(regels[0].includes(token), false, regels[0]);
  assert.doesNotMatch(regels[0], /oat01/, regels[0]);
  // Diagnostisch bruikbaar blijven: de melding zegt nog steeds wát er misging.
  assert.match(regels[0], /voorverwarmen mislukt/);
  assert.match(regels[0], /spawn/);
});

test("prewarm is idempotent zolang er al een warme sessie staat", () => {
  const { p, boots } = pool();
  p.prewarm();
  p.observe(SIG);
  p.claim(SIG); // boot 1
  assert.equal(p.prewarm().booted, false);
  assert.equal(p.prewarm().reason, "al warm");
  assert.equal(boots.length, 1);
  p.destroy();
});

test("registry kiest de vaakst geziene signatuur voor het voorverwarmen", () => {
  const { p, boots } = pool();
  p.prewarm();
  p.observe(OTHER);
  p.observe(SIG);
  p.observe(SIG); // SIG wint op aantal
  p.claim(SIG); // miss → herverwarmt de beste signatuur
  assert.equal(boots.length, 1);
  assert.equal(signatureKey(boots[0].sig), signatureKey(SIG));
  p.destroy();
});

// ── Module-grens: WarmPool kent geen concurrency-cap (#154/#155) ──────────
// prewarm()/claim() mogen nooit van een externe permit afhangen — anders zou
// een volle `aiSemaphore` (#155) het voorverwarmen kunnen blokkeren, terwijl
// juist de bedoeling is dat de boot ERBUITEN valt (concurrency.ts-comment:
// "de pool-cap (1) begrenst die al"). Geen enkele runtime-test kan dit beter
// bewijzen dan de module zelf: WarmPool importeert concurrency.ts nergens.
test("WarmPool is module-los van de concurrency-cap: geen import/verwijzing naar de semaphore", () => {
  const src = readFileSync(fileURLToPath(new URL("./warmpool.ts", import.meta.url)), "utf8");
  assert.doesNotMatch(src, /concurrency(\.js)?['"]/, "warmpool.ts mag concurrency.ts niet importeren");
  assert.doesNotMatch(src, /[Ss]emaphore/, "warmpool.ts mag geen semaphore-concept kennen");
});
