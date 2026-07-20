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
//
// PRIORITEIT (#279). Sinds de brein-mining parallel draait is deze semaphore
// gedeeld tussen twee soorten verkeer met totaal verschillende urgentie: een
// bezoeker op /ask die NU een antwoord wil, en een batch-mining-run die uren
// mag duren. Zonder onderscheid vult een nachtrun alle slots, belandt elke
// vraag in de rij en komt hij na de wachttijd-cap als 429 terug — de bezoeker
// ziet "AI weg" terwijl de machine juist keihard werkt. Twee regels houden dat
// tegen, samen:
//
//  1. ACHTERGROND-DEELCAP: background-aanvragen mogen samen hoogstens
//     `backgroundMax` permits bezetten (< max). Het verschil is de RESERVE die
//     per constructie vrij blijft voor interactief verkeer — ook wanneer de
//     mining op volle sterkte draait. De reserve is bewust ≥ 2 in productie
//     zodat óók een agentic /ask (2 permits) meteen door kan.
//  2. STRIKTE VOORRANG IN DE RIJ: zolang er een interactieve wachter staat
//     wordt er géén background-wachter toegelaten, en een interactieve
//     aanvraag mag wachtende background-aanvragen inhalen. Binnen één
//     prioriteit blijft het FIFO met kop-blokkade (een zware wachter vooraan
//     wordt niet ingehaald door lichte nieuwkomers).
//
// De keerzijde is bewust: onder aanhoudend interactief verkeer kan een
// background-wachter lang wachten en uiteindelijk een 429 krijgen. Dat is de
// goede kant om te falen — mining komt de volgende run gewoon terug (het
// per-kaart-watermark bewaart de voortgang), een weggestuurde bezoeker niet.

export class ConcurrencyLimitError extends Error {
  readonly code = "concurrency_limit";
  constructor(max: number, maxWaitMs: number) {
    super(
      `alle AI-slots bezet (cap ${max}); na ${Math.round(maxWaitMs / 1000)}s wachten afgewezen`,
    );
    this.name = "ConcurrencyLimitError";
  }
}

/** Soort verkeer (#279). `interactive` is alles waar een mens op wacht (/ask,
 * streaming, agentic); `background` is batch-werk zonder wachtende bezoeker
 * (de brein-mining via /extract/*). Default `interactive`: een aanroeper die
 * niets zegt krijgt de veilige, niet-gedegradeerde behandeling. */
export type AiPriority = "interactive" | "background";

export interface AcquireOptions {
  /** Client-abort tijdens het wachten: meteen de rij uit (geen 429 maar het
   * bestaande abort-pad — de vertaling doet ai.ts). */
  signal?: AbortSignal;
  maxWaitMs: number;
  /** Zie {@link AiPriority}; default `interactive`. */
  priority?: AiPriority;
}

export interface CapacitySnapshot {
  maxConcurrency: number;
  /** Deelcap voor background-verkeer; `maxConcurrency - backgroundMax` is de
   * reserve die altijd vrij blijft voor interactief verkeer (#279). */
  backgroundMax: number;
  active: number;
  waiting: number;
  /** Deel van `waiting` dat background is — zo laat /health zien of de mining
   * wacht (goed) of dat vragen wachten (fout). */
  waitingBackground: number;
  waitedTotal: number;
  rejectedTotal: number;
}

interface Waiter {
  weight: number;
  priority: AiPriority;
  grant: () => void;
}

export class AiSemaphore {
  private active = 0;
  private readonly queue: Waiter[] = [];
  private waitedTotal = 0;
  private rejectedTotal = 0;

  /** Deelcap voor background-verkeer (#279); altijd 1..max. Gelijk aan `max`
   * betekent: geen reserve, gedrag als vóór #279. */
  readonly backgroundMax: number;

  constructor(
    readonly max: number,
    backgroundMax: number = max,
  ) {
    this.backgroundMax = Math.min(Math.max(1, Math.floor(backgroundMax)), max);
  }

  /** De cap die voor déze prioriteit geldt: interactief mag tot aan `max`,
   * background stopt bij `backgroundMax` — het verschil is de reserve. */
  private limitFor(priority: AiPriority): number {
    return priority === "background" ? this.backgroundMax : this.max;
  }

  /** Verwerf `weight` permits; resolves met de release-functie. Gewichten
   * worden op de cap afgeknepen zodat een agentic-run (2) ook bij cap 1 ooit
   * aan de beurt komt. FIFO met kop-blokkade binnen één prioriteit: een zware
   * wachter vooraan wordt niet eindeloos ingehaald door lichte nieuwkomers.
   * Tussen prioriteiten geldt strikte voorrang (#279): interactief haalt
   * wachtend background-werk in, nooit andersom. */
  async acquire(weight: number, opts: AcquireOptions): Promise<() => void> {
    const priority = opts.priority ?? "interactive";
    const w = Math.min(Math.max(1, Math.floor(weight)), this.limitFor(priority));
    if (opts.signal?.aborted)
      throw new Error("aanroep afgebroken vóór toewijzing van een AI-slot");
    // Direct toewijzen mag alleen als deze aanvraag niemand voordringt die er
    // eerder recht op had: interactief kijkt naar de interactieve wachters
    // (wachtend background-werk mág het inhalen), background naar de hele rij.
    const ahead =
      priority === "background"
        ? this.queue.length
        : this.queue.filter((q) => q.priority === "interactive").length;
    if (ahead === 0 && this.active + w <= this.limitFor(priority)) {
      this.active += w;
      return this.releaser(w);
    }
    this.waitedTotal += 1;
    return await new Promise<() => void>((resolve, reject) => {
      const waiter: Waiter = { weight: w, priority, grant: () => {} };
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

  /** De eerstvolgende wachter die NU een permit mag krijgen, of null.
   * Strikte voorrang (#279): staat er een interactieve wachter, dan is die de
   * enige kandidaat — past hij nog niet, dan wacht de hele rij (ook het
   * background-werk erachter). Pas als er geen interactieve wachter is komt de
   * kop van het background-werk in beeld, tegen de lagere deelcap. */
  private nextGrantable(): number | null {
    const i = this.queue.findIndex((q) => q.priority === "interactive");
    const head = i >= 0 ? i : this.queue.length > 0 ? 0 : -1;
    if (head < 0) return null;
    const waiter = this.queue[head];
    return this.active + waiter.weight <= this.limitFor(waiter.priority) ? head : null;
  }

  private drain(): void {
    for (;;) {
      const i = this.nextGrantable();
      if (i === null) return;
      const [next] = this.queue.splice(i, 1);
      next.grant();
    }
  }

  snapshot(): CapacitySnapshot {
    return {
      maxConcurrency: this.max,
      backgroundMax: this.backgroundMax,
      active: this.active,
      waiting: this.queue.length,
      waitingBackground: this.queue.filter((q) => q.priority === "background").length,
      waitedTotal: this.waitedTotal,
      rejectedTotal: this.rejectedTotal,
    };
  }
}

function envInt(name: string, fallback: number, min = 1): number {
  const parsed = Number.parseInt(process.env[name] ?? "", 10);
  return Number.isFinite(parsed) && parsed >= min ? parsed : fallback;
}

/** Cap op gelijktijdige SDK-sessies; default 5 sinds #279 — bij te stellen op
 * de tellers in /health (`capacity`).
 *
 * Van 3 naar 5 hoort ONLOSMAKELIJK bij het geheugenplafond van de container:
 * elke gelijktijdige sessie is een Claude-subprocess van orde-grootte 300-400
 * MiB RSS, dus 5 slots vragen ~2 GiB náást node zelf. `rb-v2-ai` ging in
 * dezelfde PR van 1g naar 2500m (deploy/server-setup-v2/docker-compose.yml) —
 * verhoog deze waarde nooit zonder die limiet mee te nemen, anders OOM-killt de
 * container zichzelf precies wanneer het druk is. */
export const aiSemaphore = new AiSemaphore(
  envInt("AI_MAX_CONCURRENCY", 5),
  // Achtergrond-deelcap (#279): standaard alles behalve de interactieve
  // reserve. Reserve 2 (niet 1) is bewust — een agentic /ask kost 2 permits,
  // dus met één vrij slot zou juist het zwaarste vraagpad alsnog in de rij
  // belanden terwijl de mining draait.
  envInt("AI_MAX_CONCURRENCY", 5) - envInt("AI_INTERACTIVE_RESERVE", 2, 0),
);

/** Hoe lang een vraag boven de cap maximaal in de rij wacht vóór de 429. */
export const AI_QUEUE_WAIT_MS = envInt("AI_QUEUE_WAIT_MS", 30_000);
