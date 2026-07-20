// Warme-sessie-pool (#154).
//
// ONTWERPGRENS (bewust, uit het issue): één sessie = één call, nooit
// hergebruik over vragen heen. Context van eerdere vragen zou anders in
// latere antwoorden lekken (ook tussen gebruikers) en de meegroeiende
// geschiedenis maakt elke beurt juist duurder. Een geclaimde sessie wordt
// na afloop (of bij fout) altijd beëindigd; voorverwarmen gebeurt met een
// verse sessie.
//
// Waarom signatuur-gebonden: de Agent SDK legt de sessie-opties vast op het
// moment van `query()` — systemPrompt gaat in het initialize-controlbericht
// en `--include-partial-messages` is een spawn-flag van het CLI-subprocess.
// Een voorverwarmde sessie kan die dus NIET later nog krijgen. Een warme
// sessie is daarom alleen bruikbaar voor een call waarvan (systemPrompt,
// includePartialMessages) byte-gelijk zijn aan wat bij de boot is
// meegegeven. In de praktijk is dat de query-rewrite-call van /ask (statisch
// systeem-prompt, niet-streamend): precies de eerste SDK-boot op het
// kritieke pad. De pool leert signaturen uit het echte verkeer en verwarmt
// de vaakst geziene voor.
//
// Signaal-gedreven (scope-aanvulling op #154): een `POST /prewarm` (vanaf de
// /ask-paginalaad) opent een activiteitsvenster van `ttlMs`. Alleen binnen
// dat venster wordt er geboot en na een claim herverwarmd; een ongebruikte
// warme sessie wordt na `ttlMs` gesloten en NIET vervangen. Zo bestaat het
// extra idle CLI-proces (orde-grootte honderden MB's RSS; exacte cijfers
// volgen uit productiemetingen) alleen rond echte activiteit — relevant op
// de 8GB-VM. Kill-switch: `AI_WARM_POOL=0`.

import { describeThrown, logEvent, type StderrTail } from "./failure.js";

/** Sessie-opties die bij de SDK-boot vastliggen en dus de claim-sleutel zijn. */
export interface WarmSignature {
  systemPrompt?: string;
  includePartialMessages: boolean;
}

/** Byte-gelijke opties ⇒ zelfde sleutel; `undefined` en `""` zijn bewust
 * verschillend (het koude pad zet systemPrompt alleen als die truthy is). */
export function signatureKey(sig: WarmSignature): string {
  return JSON.stringify([sig.systemPrompt ?? null, sig.includePartialMessages]);
}

/** Wat de boot-factory (ai.ts) aan de pool teruggeeft: de SDK-berichtenstroom
 * plus de vastgehouden input-iterator waarin de user-message later gepusht
 * wordt. `kill` breekt de sessie hard af (abort + subprocess weg). */
export interface WarmBootHandle {
  messages: AsyncIterable<unknown>;
  push(message: unknown): void;
  endInput(): void;
  kill(): void;
  /** De stderr-ringbuffer van DIT subprocess (#300). Hij wordt bij de boot
   * aangelegd en reist met de sessie mee, want de pool draagt sessies over
   * tijd: een globale buffer zou de uitvoer van een sessie die nu boot mengen
   * met die van een gelijktijdige koude call. Omdat één sessie precies één
   * call bedient (de ontwerpgrens bovenaan dit bestand), is deze staart
   * eenduidig van de claimende aanroep — inclusief wat er tijdens de boot in
   * kwam, en dát is juist de uitvoer die verklaart waaróm een claim dood
   * bleek. Wordt de sessie nooit geclaimd, dan sterft de staart met haar. */
  stderr: StderrTail;
}

/** Een geclaimde warme sessie: push het bericht en consumeer de berichten
 * met dezelfde leeslus als het koude pad. Na afloop is de sessie op. */
export interface ClaimedWarmSession {
  send(message: unknown): void;
  messages(): AsyncGenerator<unknown, void>;
  /** Voor de abort-koppeling (#103-les): client weg ⇒ sessie hard afbreken. */
  kill(): void;
  /** De staart van déze sessie (#300) — zie {@link WarmBootHandle.stderr}. */
  stderr: StderrTail;
}

export interface WarmPoolConfig {
  boot(sig: WarmSignature): WarmBootHandle;
  enabled: boolean;
  /** TTL van een ongebruikte warme sessie én duur van het activiteitsvenster. */
  ttlMs: number;
  /** Registrygrootte voor geleerde signaturen (LRU); default 8. */
  maxSignatures?: number;
  log?: (line: string) => void;
}

export interface WarmPoolStats {
  enabled: boolean;
  slot: "leeg" | "aan het opwarmen of warm";
  windowActive: boolean;
  boots: number;
  hits: number;
  misses: number;
  expired: number;
  deadIdle: number;
  deadOnClaim: number;
  bootFailures: number;
  signatures: number;
}

/** Push-gedreven AsyncIterable: de SDK-sessie start met deze iterator als
 * prompt (streaming input) en boot alvast; de eerste user-message komt pas
 * bij de claim. Na `end()` is de input compleet en rondt de sessie na het
 * result af (één sessie = één call). */
export function pushableInput<T>() {
  const items: T[] = [];
  const waiters: Array<(r: IteratorResult<T>) => void> = [];
  let ended = false;
  return {
    push(value: T) {
      const w = waiters.shift();
      if (w) w({ value, done: false });
      else items.push(value);
    },
    end() {
      ended = true;
      for (const w of waiters.splice(0)) w({ value: undefined as T, done: true });
    },
    iterable: {
      [Symbol.asyncIterator]() {
        return {
          next(): Promise<IteratorResult<T>> {
            if (items.length > 0)
              return Promise.resolve({ value: items.shift() as T, done: false });
            if (ended) return Promise.resolve({ value: undefined as T, done: true });
            return new Promise<IteratorResult<T>>((r) => waiters.push(r));
          },
        };
      },
    } as AsyncIterable<T>,
  };
}

interface Slot {
  key: string;
  handle: WarmBootHandle;
  buffered: unknown[];
  done: boolean;
  error: unknown;
  claimed: boolean;
  notify: (() => void) | null;
  timer: ReturnType<typeof setTimeout> | null;
}

interface SignatureStat {
  sig: WarmSignature;
  count: number;
  lastSeen: number;
}

export class WarmPool {
  private slot: Slot | null = null;
  private windowUntil = 0;
  private readonly registry = new Map<string, SignatureStat>();
  private readonly maxSignatures: number;
  private readonly log: (line: string) => void;
  private boots = 0;
  private hits = 0;
  private misses = 0;
  private expired = 0;
  private deadIdle = 0;
  private deadOnClaim = 0;
  private bootFailures = 0;

  constructor(private readonly config: WarmPoolConfig) {
    this.maxSignatures = config.maxSignatures ?? 8;
    // Default-sink door de redactie-poort (#292): deze klasse logt onder meer
    // een boot-fout, en dat is dezelfde soort rauwe SDK-tekst die #281 uit de
    // containerlog wilde houden. Een geïnjecteerde sink (tests) omzeilt de
    // poort per definitie — daarom wordt de tekst hieronder óók bij de bron al
    // geclassificeerd en geredacteerd.
    this.log = config.log ?? ((line) => logEvent("warmpool", { detail: line }));
  }

  isEnabled(): boolean {
    return this.config.enabled;
  }

  private windowActive(): boolean {
    return Date.now() < this.windowUntil;
  }

  /** Leer een signatuur uit echt verkeer — alleen binnen het activiteits-
   * venster, zodat losse achtergrondjobs (mining draait óók task=cheap in
   * bulk) de voorverwarm-keuze niet domineren buiten gebruikersactiviteit. */
  observe(sig: WarmSignature): void {
    if (!this.config.enabled || !this.windowActive()) return;
    const key = signatureKey(sig);
    const existing = this.registry.get(key);
    if (existing) {
      existing.count += 1;
      existing.lastSeen = Date.now();
      return;
    }
    if (this.registry.size >= this.maxSignatures) {
      let oldestKey: string | null = null;
      let oldestSeen = Infinity;
      for (const [k, v] of this.registry) {
        if (v.lastSeen < oldestSeen) {
          oldestSeen = v.lastSeen;
          oldestKey = k;
        }
      }
      if (oldestKey) this.registry.delete(oldestKey);
    }
    this.registry.set(key, { sig, count: 1, lastSeen: Date.now() });
  }

  private bestSignature(): WarmSignature | null {
    let best: SignatureStat | null = null;
    for (const stat of this.registry.values()) {
      if (
        !best ||
        stat.count > best.count ||
        (stat.count === best.count && stat.lastSeen > best.lastSeen)
      )
        best = stat;
    }
    return best?.sig ?? null;
  }

  /** Voorverwarmsignaal (paginalaad): opent/verlengt het activiteitsvenster
   * en boot — idempotent — één warme sessie voor de best geleerde signatuur.
   * Antwoordt altijd meteen; de boot zelf loopt op de achtergrond. */
  prewarm(): { enabled: boolean; booted: boolean; reason?: string } {
    if (!this.config.enabled) return { enabled: false, booted: false, reason: "kill-switch" };
    this.windowUntil = Date.now() + this.config.ttlMs;
    if (this.slot && !this.slot.done) return { enabled: true, booted: false, reason: "al warm" };
    const sig = this.bestSignature();
    if (!sig)
      return { enabled: true, booted: false, reason: "nog geen signatuur geleerd" };
    const booted = this.bootSlot(sig);
    return { enabled: true, booted, ...(booted ? {} : { reason: "boot mislukt" }) };
  }

  private bootSlot(sig: WarmSignature): boolean {
    let handle: WarmBootHandle;
    try {
      handle = this.config.boot(sig);
    } catch (e) {
      this.bootFailures += 1;
      this.log(`[warmpool] voorverwarmen mislukt: ${describeThrown(e).detail}`);
      return false;
    }
    const slot: Slot = {
      key: signatureKey(sig),
      handle,
      buffered: [],
      done: false,
      error: undefined,
      claimed: false,
      notify: null,
      timer: null,
    };
    this.boots += 1;
    this.slot = slot;
    // TTL: een ongebruikte warme sessie gaat na ttlMs weg en wordt NIET
    // vervangen — herverwarmen gebeurt alleen op een nieuw signaal/claim.
    slot.timer = setTimeout(() => {
      if (slot.claimed || this.slot !== slot) return;
      this.expired += 1;
      this.slot = null;
      slot.handle.kill();
    }, this.config.ttlMs);
    slot.timer.unref?.();
    // Pomp: lees de SDK-berichten vanaf de boot (system-init e.d. komen al
    // vóór de claim binnen en worden gebufferd; de leeslus van ai.ts negeert
    // ze). Eindigt de stroom terwijl de sessie nog idle is, dan is het
    // subprocess dood — slot leegmaken zodat een claim transparant koud gaat.
    void (async () => {
      try {
        for await (const m of handle.messages) {
          slot.buffered.push(m);
          this.wake(slot);
        }
      } catch (e) {
        slot.error = e;
      }
      slot.done = true;
      this.wake(slot);
      if (!slot.claimed && this.slot === slot) {
        this.deadIdle += 1;
        this.slot = null;
        if (slot.timer) clearTimeout(slot.timer);
        slot.handle.kill();
        this.log("[warmpool] warme sessie idle gestorven — slot leeggemaakt");
      }
    })();
    return true;
  }

  private wake(slot: Slot): void {
    const n = slot.notify;
    slot.notify = null;
    n?.();
  }

  /** Claim de warme sessie voor exact deze signatuur. Mismatch of leeg slot
   * ⇒ null (aanroeper start koud); binnen het activiteitsvenster wordt dan —
   * of na een geslaagde claim — asynchroon opnieuw voorverwarmd. */
  claim(sig: WarmSignature): ClaimedWarmSession | null {
    if (!this.config.enabled) return null;
    const slot = this.slot;
    if (!slot) {
      this.misses += 1;
      this.rewarmIfActive();
      return null;
    }
    if (slot.done || slot.key !== signatureKey(sig)) {
      // done ⇒ dood (pomp ruimt op); mismatch ⇒ warme sessie laten staan
      // voor de call waarvoor hij wél past (bv. de volgende rewrite).
      this.misses += 1;
      if (slot.done) {
        this.slot = null;
        if (slot.timer) clearTimeout(slot.timer);
        slot.handle.kill();
        this.rewarmIfActive();
      }
      return null;
    }
    slot.claimed = true;
    if (slot.timer) clearTimeout(slot.timer);
    this.slot = null;
    this.hits += 1;
    this.rewarmIfActive();
    return {
      send: (message: unknown) => {
        slot.handle.push(message);
        slot.handle.endInput();
      },
      kill: () => slot.handle.kill(),
      messages: () => this.drain(slot),
      // De staart van DEZE sessie gaat mee naar de claimende aanroep (#300);
      // een sessie die hier nooit uitkomt neemt haar uitvoer mee het graf in.
      stderr: slot.handle.stderr,
    };
  }

  private async *drain(slot: Slot): AsyncGenerator<unknown, void> {
    let i = 0;
    while (true) {
      if (i < slot.buffered.length) {
        yield slot.buffered[i++];
        continue;
      }
      if (slot.done) {
        if (slot.error !== undefined) throw slot.error;
        return;
      }
      await new Promise<void>((r) => {
        slot.notify = r;
      });
    }
  }

  /** Geclaimde sessie bleek dood vóór er output kwam (ai.ts start dan
   * transparant koud) — alleen een teller, de sessie zelf is al op. */
  noteDeadClaim(): void {
    this.deadOnClaim += 1;
  }

  private rewarmIfActive(): void {
    if (!this.windowActive() || this.slot) return;
    const sig = this.bestSignature();
    if (sig) this.bootSlot(sig);
  }

  stats(): WarmPoolStats {
    return {
      enabled: this.config.enabled,
      slot: this.slot ? "aan het opwarmen of warm" : "leeg",
      windowActive: this.windowActive(),
      boots: this.boots,
      hits: this.hits,
      misses: this.misses,
      expired: this.expired,
      deadIdle: this.deadIdle,
      deadOnClaim: this.deadOnClaim,
      bootFailures: this.bootFailures,
      signatures: this.registry.size,
    };
  }

  /** Voor tests en een nette shutdown: warme sessie en timer opruimen. */
  destroy(): void {
    const slot = this.slot;
    this.slot = null;
    if (slot) {
      if (slot.timer) clearTimeout(slot.timer);
      slot.handle.kill();
    }
    this.windowUntil = 0;
  }
}
