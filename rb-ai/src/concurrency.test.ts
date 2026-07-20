import assert from "node:assert/strict";
import { test } from "node:test";
import { AiSemaphore, ConcurrencyLimitError } from "./concurrency.js";

const tick = () => new Promise<void>((r) => setTimeout(r, 0));

/** Wacht op een productie-timeout die met `unref()` is afgesteld.
 *
 * De semaphore-timeout roept bewust `timer.unref()` aan (correct: een
 * wachttijd-timeout mag het proces nooit levend houden). In `node:test`
 * betekent dat: als de rejectie-timer het énige openstaande handle is, sluit
 * de event-loop af vóór hij vuurt en blijft de acquire-promise "pending" bij
 * teardown → flaky. Deze helper houdt tijdens het wachten één ref'd timer
 * open (die de unref'd timeout gewoon laat vuren) en ruimt hem daarna op, dus
 * de test wacht deterministisch op de rejectie zónder de productiecode te
 * raken. */
async function withKeepAlive<T>(fn: () => Promise<T>): Promise<T> {
  const handle = setTimeout(() => {}, 5_000);
  try {
    return await fn();
  } finally {
    clearTimeout(handle);
  }
}

/** Volgt of een promise al gesettled is zonder erop te wachten. `promise`
 * blijft beschikbaar zodat een test de wachter aan het eind kan opruimen. */
function track<T>(p: Promise<T>) {
  const state = { settled: false, rejected: undefined as unknown, promise: p };
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
  await withKeepAlive(() =>
    assert.rejects(
      sem.acquire(1, { maxWaitMs: 15 }),
      (e: unknown) => e instanceof ConcurrencyLimitError && e.code === "concurrency_limit",
    ),
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
  const heavyP = sem.acquire(2, { maxWaitMs: 1_000 });
  const heavy = track(heavyP);
  const lightP = sem.acquire(1, { maxWaitMs: 1_000 });
  const light = track(lightP);
  r1();
  await tick();
  assert.equal(heavy.settled, false, "zwaar past nog niet (1 van 2 vrij)");
  assert.equal(light.settled, false, "licht mag zwaar niet inhalen");
  r2();
  await tick();
  assert.equal(heavy.settled, true, "zwaar gaat als eerste");
  assert.equal(light.settled, false, "licht wacht tot zwaar klaar is");
  // Opruimen: laat zwaar los zodat licht alsnog aan de beurt komt en beide
  // wachters afgehandeld zijn — geen dangling (unref'd) timer of pending
  // promise die node:test-teardown kan verstoren.
  (await heavyP)();
  (await lightP)();
});

// ── /ask-vrijwaring tijdens een mining-run (#279) ─────────────────────────
//
// Dit is de acceptatie-eis van #279 die niet aangenomen mag worden: draait de
// brein-mining op volle sterkte, dan moet een bezoeker nog steeds meteen een
// slot krijgen. Zonder deze garantie belandt elke vraag tijdens de nachtrun in
// de rij en komt hij als 429 terug — "AI weg" terwijl de machine werkt.

test("mining op volle sterkte: een interactieve aanvraag krijgt nog meteen een slot", async () => {
  // Productie-instelling: cap 5, reserve 2 → mining mag er hoogstens 3.
  const sem = new AiSemaphore(5, 3);
  const mining = [];
  for (let i = 0; i < 3; i++)
    mining.push(await sem.acquire(1, { maxWaitMs: 1_000, priority: "background" }));
  assert.equal(sem.snapshot().active, 3);

  // Een vierde mining-aanvraag past NIET meer: de deelcap houdt de reserve vrij.
  const fourth = track(sem.acquire(1, { maxWaitMs: 1_000, priority: "background" }));
  await tick();
  assert.equal(fourth.settled, false, "background stopt bij de deelcap, niet bij max");

  // …maar de bezoeker gaat er dwars doorheen, zónder te wachten.
  const ask = track(sem.acquire(1, { maxWaitMs: 1_000, priority: "interactive" }));
  await tick();
  assert.equal(ask.settled, true, "/ask mag de reserve gebruiken en het wachtende mining-werk inhalen");

  mining.forEach((r) => r());
  (await fourth.promise)();
});

test("ook een agentic vraag (2 permits) past nog naast een volle mining-run", async () => {
  const sem = new AiSemaphore(5, 3);
  for (let i = 0; i < 3; i++)
    await sem.acquire(1, { maxWaitMs: 1_000, priority: "background" });
  // Reserve 2 is precies op het agentic-gewicht gekozen: één vrij slot zou het
  // zwaarste vraagpad alsnog in de rij duwen.
  const agentic = track(sem.acquire(2, { maxWaitMs: 1_000, priority: "interactive" }));
  await tick();
  assert.equal(agentic.settled, true);
  assert.equal(sem.snapshot().active, 5);
});

test("strikte voorrang in de rij: background wordt niet toegelaten vóór een wachtende vraag", async () => {
  const sem = new AiSemaphore(2, 2);   // geen reserve: alleen de rij-voorrang telt hier
  const r1 = await sem.acquire(1, { maxWaitMs: 1_000, priority: "background" });
  const r2 = await sem.acquire(1, { maxWaitMs: 1_000, priority: "background" });

  // Eerst een background-wachter, dan pas de vraag — FIFO zou background winnen.
  const queuedMining = track(sem.acquire(1, { maxWaitMs: 1_000, priority: "background" }));
  const ask = track(sem.acquire(1, { maxWaitMs: 1_000, priority: "interactive" }));
  await tick();
  assert.equal(sem.snapshot().waiting, 2);
  assert.equal(sem.snapshot().waitingBackground, 1);

  r1();
  await tick();
  assert.equal(ask.settled, true, "het vrijgekomen slot gaat naar de vraag, niet naar de mining");
  assert.equal(queuedMining.settled, false, "mining wacht netjes achteraan");

  r2();
  await tick();
  assert.equal(queuedMining.settled, true, "daarna komt de mining alsnog aan de beurt");
  (await ask.promise)();
  (await queuedMining.promise)();
});

test("background dringt niet voor bij de directe toewijzing (wachtende vraag gaat voor)", async () => {
  const sem = new AiSemaphore(2, 2);
  const r1 = await sem.acquire(1, { maxWaitMs: 1_000, priority: "interactive" });
  const r2 = await sem.acquire(1, { maxWaitMs: 1_000, priority: "interactive" });
  const ask = track(sem.acquire(1, { maxWaitMs: 1_000, priority: "interactive" }));
  await tick();

  // Er is een wachtende vraag: nieuw mining-werk moet erachter aansluiten,
  // ook al zou het gewicht op dat moment passen zodra r1 loslaat.
  const mining = track(sem.acquire(1, { maxWaitMs: 1_000, priority: "background" }));
  r1();
  await tick();
  assert.equal(ask.settled, true);
  assert.equal(mining.settled, false);

  r2();
  await tick();
  (await ask.promise)();
  (await mining.promise)();
});

test("deelcap wordt op de cap begrensd en blijft minstens 1", async () => {
  assert.equal(new AiSemaphore(3, 99).backgroundMax, 3, "nooit meer dan de cap");
  assert.equal(new AiSemaphore(3, 0).backgroundMax, 1, "achtergrondwerk komt nooit volledig stil te liggen");
  assert.equal(new AiSemaphore(3).backgroundMax, 3, "zonder deelcap: gedrag als vóór #279");
});

test("tellers: waited en rejected lopen mee voor bijstelling op cijfers", async () => {
  const sem = new AiSemaphore(1);
  const release = await sem.acquire(1, { maxWaitMs: 1_000 });
  const waiting = sem.acquire(1, { maxWaitMs: 1_000 });
  await withKeepAlive(() => assert.rejects(sem.acquire(1, { maxWaitMs: 10 })));
  release();
  (await waiting)();
  const snap = sem.snapshot();
  assert.equal(snap.waitedTotal, 2);
  assert.equal(snap.rejectedTotal, 1);
  assert.equal(snap.maxConcurrency, 1);
});
