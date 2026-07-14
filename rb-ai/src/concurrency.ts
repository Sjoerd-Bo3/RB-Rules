// Globale gelijktijdigheids-cap op SDK-sessies (#155).
//
// N gelijktijdige vragen = N Claude-subprocessen (elk orde-grootte honderden
// MB's RSS; exacte cijfers volgen uit productiemetingen, niet uit deze PR)
// naast Ollama/Neo4j/Postgres op de 8GB-VM. Deze semaphore is de éne bron
// van waarheid rond elke sessie die een API-call gaat doen (koud én warm-
// geclaimd; ai.ts verwerft vóór de start). De voorverwarm-boot van de pool
// telt bewust NIET mee: dat is een idle proces zonder API-call, en de
// pool-cap (1) begrenst die al.
//
// Boven de cap wacht een aanvraag kort in de rij (FIFO, met wachttijd-cap);
// daarna volgt een ConcurrencyLimitError die server.ts als nette 429 met
// machine-leesbare reden teruggeeft — rb-api's RbAiClient behandelt elke
// non-success al als "AI weg" en degradeert naar de bestaande vriendelijke
// melding. Agentic-runs zijn groter en langer (multi-turn, tools) en kosten
// daarom 2 permits in dezelfde semaphore: één knop om bij te stellen in
// plaats van een tweede sub-cap met eigen wachtrij.

export class ConcurrencyLimitError extends Error {
  readonly code = "concurrency_limit";
  constructor(max: number, maxWaitMs: number) {
    super(
      `alle AI-slots bezet (cap ${max}); na ${Math.round(maxWaitMs / 1000)}s wachten afgewezen`,
    );
    this.name = "ConcurrencyLimitError";
  }
}

export interface AcquireOptions {
  /** Client-abort tijdens het wachten: meteen de rij uit (geen 429 maar het
   * bestaande abort-pad — de vertaling doet ai.ts). */
  signal?: AbortSignal;
  maxWaitMs: number;
}

export interface CapacitySnapshot {
  maxConcurrency: number;
  active: number;
  waiting: number;
  waitedTotal: number;
  rejectedTotal: number;
}

interface Waiter {
  weight: number;
  grant: () => void;
}

export class AiSemaphore {
  private active = 0;
  private readonly queue: Waiter[] = [];
  private waitedTotal = 0;
  private rejectedTotal = 0;

  constructor(readonly max: number) {}

  /** Verwerf `weight` permits; resolves met de release-functie. Gewichten
   * worden op de cap afgeknepen zodat een agentic-run (2) ook bij cap 1 ooit
   * aan de beurt komt. FIFO met kop-blokkade: een zware wachter vooraan wordt
   * niet eindeloos ingehaald door lichte nieuwkomers. */
  async acquire(weight: number, opts: AcquireOptions): Promise<() => void> {
    const w = Math.min(Math.max(1, Math.floor(weight)), this.max);
    if (opts.signal?.aborted)
      throw new Error("aanroep afgebroken vóór toewijzing van een AI-slot");
    if (this.queue.length === 0 && this.active + w <= this.max) {
      this.active += w;
      return this.releaser(w);
    }
    this.waitedTotal += 1;
    return await new Promise<() => void>((resolve, reject) => {
      const waiter: Waiter = { weight: w, grant: () => {} };
      const remove = () => {
        const i = this.queue.indexOf(waiter);
        if (i >= 0) this.queue.splice(i, 1);
      };
      const timer = setTimeout(() => {
        remove();
        opts.signal?.removeEventListener("abort", onAbort);
        this.rejectedTotal += 1;
        // De rij achter deze wachter kan nu mogelijk wél (kop-blokkade weg).
        this.drain();
        reject(new ConcurrencyLimitError(this.max, opts.maxWaitMs));
      }, opts.maxWaitMs);
      timer.unref?.();
      const onAbort = () => {
        remove();
        clearTimeout(timer);
        this.drain();
        reject(new Error("aanroep afgebroken tijdens het wachten op een AI-slot"));
      };
      opts.signal?.addEventListener("abort", onAbort, { once: true });
      waiter.grant = () => {
        clearTimeout(timer);
        opts.signal?.removeEventListener("abort", onAbort);
        this.active += w;
        resolve(this.releaser(w));
      };
      this.queue.push(waiter);
    });
  }

  private releaser(weight: number): () => void {
    let released = false;
    return () => {
      if (released) return;
      released = true;
      this.active -= weight;
      this.drain();
    };
  }

  private drain(): void {
    while (this.queue.length > 0 && this.active + this.queue[0].weight <= this.max) {
      const next = this.queue.shift();
      next?.grant();
    }
  }

  snapshot(): CapacitySnapshot {
    return {
      maxConcurrency: this.max,
      active: this.active,
      waiting: this.queue.length,
      waitedTotal: this.waitedTotal,
      rejectedTotal: this.rejectedTotal,
    };
  }
}

function envInt(name: string, fallback: number, min = 1): number {
  const parsed = Number.parseInt(process.env[name] ?? "", 10);
  return Number.isFinite(parsed) && parsed >= min ? parsed : fallback;
}

/** Cap op gelijktijdige SDK-sessies; default 3 — bij te stellen op de
 * tellers in /health (`capacity`). */
export const aiSemaphore = new AiSemaphore(envInt("AI_MAX_CONCURRENCY", 3));

/** Hoe lang een vraag boven de cap maximaal in de rij wacht vóór de 429. */
export const AI_QUEUE_WAIT_MS = envInt("AI_QUEUE_WAIT_MS", 30_000);
