import assert from "node:assert/strict";
import { test } from "node:test";
import { AiSemaphore, ConcurrencyLimitError } from "./concurrency.js";

const tick = () => new Promise<void>((r) => setTimeout(r, 0));

/** Volgt of een promise al gesettled is zonder erop te wachten. */
function track<T>(p: Promise<T>) {
  const state = { settled: false, rejected: undefined as unknown };
  p.then(
    () => {
      state.settled = true;
    },
    (e) => {
      state.settled = true;
      state.rejected = e;
    },
  );
  return state;
}

test("cap afgedwongen: de N+1e wacht tot er een permit vrijkomt", async () => {
  const sem = new AiSemaphore(2);
  const r1 = await sem.acquire(1, { maxWaitMs: 1_000 });
  await sem.acquire(1, { maxWaitMs: 1_000 });
  const third = sem.acquire(1, { maxWaitMs: 1_000 });
  const state = track(third);
  await tick();
  assert.equal(state.settled, false, "boven de cap hoort de aanvraag te wachten");
  assert.equal(sem.snapshot().waiting, 1);
  r1();
  const release = await third;
  assert.equal(sem.snapshot().active, 2);
  release();
});

test("wachttijd-overschrijding geeft ConcurrencyLimitError met machine-leesbare code", async () => {
  const sem = new AiSemaphore(1);
  const release = await sem.acquire(1, { maxWaitMs: 1_000 });
  await assert.rejects(
    sem.acquire(1, { maxWaitMs: 15 }),
    (e: unknown) => e instanceof ConcurrencyLimitError && e.code === "concurrency_limit",
  );
  assert.equal(sem.snapshot().rejectedTotal, 1);
  assert.equal(sem.snapshot().waiting, 0, "de afgewezen wachter is uit de rij");
  release();
});

test("agentic-gewicht: 2 permits per run", async () => {
  const sem = new AiSemaphore(3);
  const agentic = await sem.acquire(2, { maxWaitMs: 1_000 });
  await sem.acquire(1, { maxWaitMs: 1_000 });
  assert.equal(sem.snapshot().active, 3);
  const next = track(sem.acquire(1, { maxWaitMs: 1_000 }));
  await tick();
  assert.equal(next.settled, false, "cap vol: 2 (agentic) + 1");
  agentic();
  await tick();
  assert.equal(next.settled, true, "agentic-release maakt 2 permits vrij");
});

test("gewicht wordt op de cap afgeknepen (agentic kan ook bij cap 1 aan de beurt)", async () => {
  const sem = new AiSemaphore(1);
  const release = await sem.acquire(2, { maxWaitMs: 1_000 });
  assert.equal(sem.snapshot().active, 1);
  release();
  assert.equal(sem.snapshot().active, 0);
});

test("abort tijdens het wachten: uit de rij, géén 429-fout", async () => {
  const sem = new AiSemaphore(1);
  const release = await sem.acquire(1, { maxWaitMs: 1_000 });
  const abort = new AbortController();
  const waiting = sem.acquire(1, { signal: abort.signal, maxWaitMs: 1_000 });
  const state = track(waiting);
  await tick();
  abort.abort();
  await tick();
  assert.equal(state.settled, true);
  assert.ok(!(state.rejected instanceof ConcurrencyLimitError));
  assert.match(String(state.rejected), /afgebroken/);
  assert.equal(sem.snapshot().waiting, 0);
  release();
});

test("al afgebroken vóór de acquire: meteen weigeren zonder te tellen", async () => {
  const sem = new AiSemaphore(1);
  const abort = new AbortController();
  abort.abort();
  await assert.rejects(sem.acquire(1, { signal: abort.signal, maxWaitMs: 1_000 }));
  assert.equal(sem.snapshot().active, 0);
  assert.equal(sem.snapshot().waitedTotal, 0);
});

test("FIFO: een zware wachter vooraan wordt niet ingehaald door lichte nieuwkomers", async () => {
  const sem = new AiSemaphore(2);
  const r1 = await sem.acquire(1, { maxWaitMs: 1_000 });
  const r2 = await sem.acquire(1, { maxWaitMs: 1_000 });
  const heavy = track(sem.acquire(2, { maxWaitMs: 1_000 }));
  const light = track(sem.acquire(1, { maxWaitMs: 1_000 }));
  r1();
  await tick();
  assert.equal(heavy.settled, false, "zwaar past nog niet (1 van 2 vrij)");
  assert.equal(light.settled, false, "licht mag zwaar niet inhalen");
  r2();
  await tick();
  assert.equal(heavy.settled, true, "zwaar gaat als eerste");
  assert.equal(light.settled, false, "licht wacht tot zwaar klaar is");
});

test("tellers: waited en rejected lopen mee voor bijstelling op cijfers", async () => {
  const sem = new AiSemaphore(1);
  const release = await sem.acquire(1, { maxWaitMs: 1_000 });
  const waiting = sem.acquire(1, { maxWaitMs: 1_000 });
  await assert.rejects(sem.acquire(1, { maxWaitMs: 10 }));
  release();
  (await waiting)();
  const snap = sem.snapshot();
  assert.equal(snap.waitedTotal, 2);
  assert.equal(snap.rejectedTotal, 1);
  assert.equal(snap.maxConcurrency, 1);
});
