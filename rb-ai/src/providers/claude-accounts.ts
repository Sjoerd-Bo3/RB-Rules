import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { isAbsolute, join } from "node:path";
import {
  query,
  type SDKControlGetUsageResponse,
} from "@anthropic-ai/claude-agent-sdk";
import type { AiFailure } from "../failure.js";
import {
  ClaudeAgentToolProvider,
  type ClaudeRateLimitSignal,
  type QueryRunner,
} from "./claude-agent.js";
import type {
  AccountRouteMetadata,
  ProviderAccountHealth,
  ToolProvider,
  ToolProviderRequest,
  ToolProviderResult,
} from "./types.js";

const QUOTA_TTL_MS = 300_000;
const QUOTA_PROBE_TIMEOUT_MS = 3_000;
const RATE_LIMIT_COOLDOWN_MS = 60_000;
const AUTH_COOLDOWN_MS = 300_000;
const IN_FLIGHT_PENALTY_PERCENT = 50;
const MAX_IN_FLIGHT_PENALTY_PERCENT = 100;

export type ClaudeAccountEnvironment = Readonly<Record<string, string>>;

interface ClaudeQuotaWindow {
  utilization: number;
  resetsAt: number | null;
}

export interface ClaudeQuotaSnapshot {
  planLimitsAvailable: boolean;
  windows: Readonly<Record<string, ClaudeQuotaWindow>>;
}

export interface ClaudeQuotaReader {
  read(): Promise<ClaudeQuotaSnapshot | null>;
}

/** API-key accounts have no Claude-plan windows in the official usage API. */
export class NoopClaudeQuotaReader implements ClaudeQuotaReader {
  async read(): Promise<null> {
    return null;
  }
}

export interface ClaudeAccount {
  environment: ClaudeAccountEnvironment;
  quotaReader: ClaudeQuotaReader;
  route?: AccountRouteMetadata;
  initialStatus?: "auth_invalid";
}

interface ClaudeAccountState extends ClaudeAccount {
  index: number;
  inFlight: number;
  quota: ClaudeQuotaSnapshot | null;
  quotaReadAt: number;
  cooldownUntil: number;
  observedUtilization: number | null;
  credentialBlocked: boolean;
  safeStatus: "unknown" | "ready" | "cooldown" | "quota_exhausted" | "auth_invalid";
  refreshing?: Promise<void>;
}

export interface ClaudeAccountLease {
  readonly environment: ClaudeAccountEnvironment;
  readonly ordinal: number;
  readonly accountId?: string;
  readonly poolId?: string;
  release(): void;
}

export interface RoutedAccountStatus {
  accountId: string;
  poolId: string;
  status: "unknown" | "ready" | "cooldown" | "quota_exhausted" | "auth_invalid";
  available: boolean;
  inFlight: number;
}

export interface ClaudeAccountFactories {
  isolatedHome?: () => string;
  isolatedWorkingDirectory?: () => string;
  quotaReader?: (
    environment: ClaudeAccountEnvironment,
    workingDirectory: string,
  ) => ClaudeQuotaReader;
}

function isolatedDirectory(prefix: string): string {
  return mkdtempSync(join(tmpdir(), prefix));
}

function nonEmpty(value: string | undefined): string | undefined {
  return value && value.trim() ? value : undefined;
}

/** Numbered mode is authoritative; unnumbered credentials are single-slot fallback only. */
export function discoverClaudeAccountEnvironments(
  source: NodeJS.ProcessEnv,
  makeIsolatedHome: () => string = () => isolatedDirectory("rb-ai-claude-home-"),
): readonly ClaudeAccountEnvironment[] {
  const numbered = new Set<number>();
  for (const key of Object.keys(source)) {
    const match = /^(?:CLAUDE_CODE_OAUTH_TOKEN|ANTHROPIC_API_KEY)_(\d+)$/.exec(key);
    if (match) numbered.add(Number(match[1]));
  }
  const numberedSlots = [...numbered].sort((a, b) => a - b).map((number) => ({
    oauth: nonEmpty(source[`CLAUDE_CODE_OAUTH_TOKEN_${number}`]),
    apiKey: nonEmpty(source[`ANTHROPIC_API_KEY_${number}`]),
  }));
  // Compose commonly materializes every optional variable as an empty string.
  // Empty numbered placeholders must not suppress a real unnumbered fallback.
  const slots = numberedSlots.some((slot) => slot.oauth || slot.apiKey)
    ? numberedSlots
    : [{
        oauth: nonEmpty(source.CLAUDE_CODE_OAUTH_TOKEN),
        apiKey: nonEmpty(source.ANTHROPIC_API_KEY),
      }];

  const credentials = new Set<string>();
  const environments: ClaudeAccountEnvironment[] = [];
  for (const slot of slots) {
    if (slot.oauth && slot.apiKey)
      throw new Error("één Claude-accountslot mag niet tegelijk OAuth en API-key bevatten");
    const credential = slot.oauth ?? slot.apiKey;
    if (!credential) continue;
    if (credentials.has(credential))
      throw new Error("Claude-accountcredentials moeten onderling uniek zijn");
    credentials.add(credential);
    const home = makeIsolatedHome();
    if (!isAbsolute(home)) throw new Error("Claude-accountmap moet een absoluut pad zijn");
    const environment: Record<string, string> = {
      PATH: source.PATH ?? "/usr/local/bin:/usr/bin:/bin",
      HOME: home,
      CLAUDE_CONFIG_DIR: home,
      TMPDIR: source.TMPDIR ?? tmpdir(),
      CLAUDE_AGENT_SDK_CLIENT_APP: "rb-ai/1.0.0",
      CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: "1",
      DISABLE_TELEMETRY: "1",
      DISABLE_ERROR_REPORTING: "1",
      ...(slot.oauth
        ? { CLAUDE_CODE_OAUTH_TOKEN: slot.oauth }
        : { ANTHROPIC_API_KEY: slot.apiKey as string }),
    };
    for (const key of ["LANG", "LC_ALL", "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS"])
      if (source[key]) environment[key] = source[key] as string;
    environments.push(Object.freeze(environment));
  }
  return Object.freeze(environments);
}

function percent(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value)
    ? Math.max(0, Math.min(100, value))
    : null;
}

function resetTime(value: unknown): number | null {
  if (typeof value === "string") {
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  if (typeof value !== "number" || !Number.isFinite(value)) return null;
  return value < 10_000_000_000 ? value * 1000 : value;
}

/** Tolerant parser because the SDK explicitly marks this control response experimental. */
export function parseClaudeQuota(value: unknown): ClaudeQuotaSnapshot | null {
  if (typeof value !== "object" || value === null) return null;
  const root = value as Record<string, unknown>;
  const limits = typeof root.rate_limits === "object" && root.rate_limits !== null
    ? root.rate_limits as Record<string, unknown>
    : null;
  const available = root.rate_limits_available === true;
  if (!available || !limits) return { planLimitsAvailable: false, windows: {} };
  const windows: Record<string, ClaudeQuotaWindow> = {};
  for (const key of [
    "five_hour",
    "seven_day",
    "seven_day_oauth_apps",
    "seven_day_opus",
    "seven_day_sonnet",
  ]) {
    const raw = typeof limits[key] === "object" && limits[key] !== null
      ? limits[key] as Record<string, unknown>
      : null;
    const utilization = percent(raw?.utilization);
    if (utilization !== null)
      windows[key] = { utilization, resetsAt: resetTime(raw?.resets_at) };
  }
  if (Array.isArray(limits.model_scoped)) {
    for (const entry of limits.model_scoped) {
      if (typeof entry !== "object" || entry === null) continue;
      const item = entry as Record<string, unknown>;
      const name = typeof item.display_name === "string" ? item.display_name.trim().toLowerCase() : "";
      const utilization = percent(item.utilization);
      if (name && utilization !== null)
        windows[`model:${name}`] = { utilization, resetsAt: resetTime(item.resets_at) };
    }
  }
  return { planLimitsAvailable: true, windows: Object.freeze(windows) };
}

function relevantWindows(quota: ClaudeQuotaSnapshot, modelId: string): ClaudeQuotaWindow[] {
  if (!quota.planLimitsAvailable) return [];
  const keys = ["five_hour", "seven_day", "seven_day_oauth_apps"];
  const lower = modelId.toLowerCase();
  if (lower.includes("opus")) keys.push("seven_day_opus");
  if (lower.includes("sonnet")) keys.push("seven_day_sonnet");
  const selected = keys
    .map((key) => quota.windows[key])
    .filter((window): window is ClaudeQuotaWindow => Boolean(window));
  for (const [key, window] of Object.entries(quota.windows)) {
    if (!key.startsWith("model:")) continue;
    const label = key.slice("model:".length);
    if (lower.includes(label)) selected.push(window);
  }
  return selected;
}

export function claudeQuotaScore(quota: ClaudeQuotaSnapshot | null, modelId: string): number | null {
  if (!quota) return null;
  const relevant = relevantWindows(quota, modelId);
  return relevant.length > 0 ? Math.max(...relevant.map((window) => window.utilization)) : null;
}

function openInput() {
  let finish: ((value: IteratorResult<never>) => void) | undefined;
  let ended = false;
  return {
    iterable: {
      [Symbol.asyncIterator]() {
        return {
          next: () => ended
            ? Promise.resolve({ value: undefined as never, done: true })
            : new Promise<IteratorResult<never>>((resolve) => {
                finish = resolve;
              }),
        };
      },
    } as AsyncIterable<never>,
    end() {
      ended = true;
      finish?.({ value: undefined as never, done: true });
      finish = undefined;
    },
  };
}

export interface ClaudeQuotaProbeResult {
  status: "ready" | "auth_invalid" | "unknown";
  quota: ClaudeQuotaSnapshot | null;
}

export function claudeProbeFailureStatus(error: unknown): "auth_invalid" | "unknown" {
  const text = error instanceof Error ? `${error.name} ${error.message}` : String(error);
  return /\b401\b|\b403\b|authentication[_\s-]?failed|unauthori[sz]ed|invalid[_\s-]?(?:oauth|token)/i
    .test(text)
    ? "auth_invalid"
    : "unknown";
}

/** No-turn usage probe: initialize streaming SDK transport, request usage, close. */
export class SdkClaudeQuotaReader implements ClaudeQuotaReader {
  constructor(
    private readonly environment: ClaudeAccountEnvironment,
    private readonly workingDirectory: string,
    private readonly timeoutMs = QUOTA_PROBE_TIMEOUT_MS,
  ) {}

  async probe(): Promise<ClaudeQuotaProbeResult> {
    const controller = new AbortController();
    const input = openInput();
    let session: ReturnType<typeof query> | undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      session = query({
        prompt: input.iterable as Parameters<typeof query>[0]["prompt"],
        options: {
          abortController: controller,
          cwd: this.workingDirectory,
          env: { ...this.environment },
          maxTurns: 1,
          persistSession: false,
          settingSources: [],
          tools: [],
          stderr: () => {},
        },
      });
      const timeout = new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error("Claude usage probe timeout")), this.timeoutMs);
        timer.unref?.();
      });
      const response = await Promise.race([
        (async () => {
          await session.initializationResult();
          return await session.usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET();
        })(),
        timeout,
      ]);
      const quota = parseClaudeQuota(response as SDKControlGetUsageResponse);
      return { status: quota ? "ready" : "unknown", quota };
    } catch (error) {
      return { status: claudeProbeFailureStatus(error), quota: null };
    } finally {
      if (timer) clearTimeout(timer);
      input.end();
      controller.abort();
      session?.close();
    }
  }

  async read(): Promise<ClaudeQuotaSnapshot | null> {
    return (await this.probe()).quota;
  }
}

export function isClaudeAccountLocalFailure(
  failure: AiFailure | undefined,
  signal?: ClaudeRateLimitSignal,
): boolean {
  if (failure?.reason === "auth") return true;
  if (signal?.status === "rejected") return true;
  return failure?.reason === "api_error"
    && /\b429\b|rate.?limit|usage.?limit|quota|credit.*(?:exhausted|depleted)/i.test(failure.detail);
}

export class ClaudeAccountRouter {
  private readonly states: ClaudeAccountState[];
  private readonly poolCredits = new Map<string, number>();
  private cursor = 0;
  private refreshingAll?: Promise<void>;

  constructor(
    accounts: readonly ClaudeAccount[],
    private readonly now: () => number = Date.now,
    private readonly quotaTtlMs = QUOTA_TTL_MS,
  ) {
    this.states = accounts.map((account, index) => ({
      ...account,
      index,
      inFlight: 0,
      quota: null,
      quotaReadAt: Number.NEGATIVE_INFINITY,
      cooldownUntil: account.initialStatus === "auth_invalid" ? Number.POSITIVE_INFINITY : 0,
      observedUtilization: null,
      credentialBlocked: account.initialStatus === "auth_invalid",
      safeStatus: account.initialStatus ?? "unknown",
    }));
  }

  accountCount(): number {
    return this.states.length;
  }

  singleEnvironment(): ClaudeAccountEnvironment | undefined {
    return this.states.length === 1 ? this.states[0]?.environment : undefined;
  }

  configured(): boolean {
    return this.states.length > 0;
  }

  private async refresh(state: ClaudeAccountState): Promise<void> {
    if (state.credentialBlocked) return;
    if (this.now() - state.quotaReadAt < this.quotaTtlMs) return;
    if (state.refreshing) return await state.refreshing;
    state.refreshing = (async () => {
      try {
        state.quota = await state.quotaReader.read();
      } catch {
        state.quota = null;
      } finally {
        state.quotaReadAt = this.now();
        state.refreshing = undefined;
      }
    })();
    return await state.refreshing;
  }

  async refreshQuotas(): Promise<void> {
    if (this.states.length < 2) return;
    // A probe boots a full Claude CLI. Never stack probes beside real runs and
    // never boot multiple probes in parallel on the memory-constrained VM.
    if (this.states.some((state) => state.inFlight > 0)) return;
    if (this.refreshingAll) return await this.refreshingAll;
    this.refreshingAll = (async () => {
      try {
        for (const state of this.states) await this.refresh(state);
      } finally {
        this.refreshingAll = undefined;
      }
    })();
    return await this.refreshingAll;
  }

  private score(state: ClaudeAccountState, modelId: string): number | null {
    const proactive = claudeQuotaScore(state.quota, modelId);
    if (proactive === null) return state.observedUtilization;
    return Math.max(proactive, state.observedUtilization ?? 0);
  }

  private distance(state: ClaudeAccountState): number {
    return this.states.length === 0
      ? 0
      : (state.index - this.cursor + this.states.length) % this.states.length;
  }

  private poolKey(state: ClaudeAccountState): string {
    return state.route?.poolId ?? `unmanaged-account-${state.index}`;
  }

  private recoverExpiredState(state: ClaudeAccountState, now: number): void {
    if (state.credentialBlocked || state.cooldownUntil <= 0 || state.cooldownUntil > now) return;
    state.cooldownUntil = 0;
    if (state.observedUtilization === 100) state.observedUtilization = null;
    if (state.safeStatus === "auth_invalid" || state.safeStatus === "quota_exhausted"
      || state.safeStatus === "cooldown")
      state.safeStatus = "unknown";
  }

  acquire(modelId: string, excluded: ReadonlySet<number> = new Set()): ClaudeAccountLease | null {
    const now = this.now();
    const candidates = this.states.filter((state) => {
      this.recoverExpiredState(state, now);
      if (excluded.has(state.index) || state.cooldownUntil > now) return false;
      const score = this.score(state, modelId);
      return score === null || score < 100;
    });
    const effective = (state: ClaudeAccountState) =>
      (this.score(state, modelId) ?? 101)
      + Math.min(MAX_IN_FLIGHT_PENALTY_PERCENT, state.inFlight * IN_FLIGHT_PENALTY_PERCENT);
    const compareLoad = (left: ClaudeAccountState, right: ClaudeAccountState) =>
      left.inFlight - right.inFlight || this.distance(left) - this.distance(right);
    const grouped = new Map<string, ClaudeAccountState[]>();
    for (const state of candidates) {
      const key = this.poolKey(state);
      const members = grouped.get(key) ?? [];
      members.push(state);
      grouped.set(key, members);
    }
    const pools = [...grouped.entries()].map(([key, states]) => {
      const priority = states[0]?.route?.priority ?? 0;
      const weight = states[0]?.route?.weight ?? 1;
      const known = states
        .filter((state) => this.score(state, modelId) !== null)
        .sort((left, right) =>
          effective(left) - effective(right)
          || compareLoad(left, right));
      const unknown = states
        .filter((state) => this.score(state, modelId) === null)
        .sort(compareLoad);
      return {
        key,
        priority,
        weight,
        known: known.length > 0,
        best: (known.length > 0 ? known : unknown)[0],
      };
    });
    const highestPriority = pools.length > 0
      ? Math.max(...pools.map((pool) => pool.priority))
      : Number.NEGATIVE_INFINITY;
    const prioritized = pools.filter((pool) => pool.priority === highestPriority && pool.best);
    const comparable = prioritized.some((pool) => pool.known)
      ? prioritized.filter((pool) => pool.known)
      : prioritized;
    comparable.sort((left, right) =>
      effective(left.best) - effective(right.best)
      || compareLoad(left.best, right.best));
    const bestPool = comparable[0];
    const equivalent = bestPool
      ? comparable.filter((pool) =>
          effective(pool.best) === effective(bestPool.best)
          && pool.best.inFlight === bestPool.best.inFlight)
      : [];
    // A load/quota/cooldown-forced choice is not a weighted-routing turn;
    // carrying credit through it would bias the next genuinely equal turn.
    if (equivalent.length === 1) this.poolCredits.clear();
    for (const pool of equivalent)
      this.poolCredits.set(pool.key, (this.poolCredits.get(pool.key) ?? 0) + pool.weight);
    const selectedPool = equivalent.sort((left, right) =>
      (this.poolCredits.get(right.key) ?? 0) - (this.poolCredits.get(left.key) ?? 0)
      || this.distance(left.best) - this.distance(right.best))[0];
    if (!selectedPool) return null;
    this.poolCredits.set(
      selectedPool.key,
      (this.poolCredits.get(selectedPool.key) ?? 0)
        - equivalent.reduce((total, pool) => total + pool.weight, 0),
    );
    const selected = selectedPool.best;
    selected.inFlight += 1;
    this.cursor = (selected.index + 1) % this.states.length;
    let released = false;
    return {
      environment: selected.environment,
      ordinal: selected.index,
      ...(selected.route
        ? { accountId: selected.route.accountId, poolId: selected.route.poolId }
        : {}),
      release: () => {
        if (released) return;
        released = true;
        selected.inFlight -= 1;
      },
    };
  }

  observeSignal(lease: ClaudeAccountLease, signal: ClaudeRateLimitSignal | undefined): void {
    if (!signal) return;
    const state = this.states[lease.ordinal];
    if (!state) return;
    if (typeof signal.utilization === "number") state.observedUtilization = signal.utilization;
    if (signal.status === "allowed" || signal.status === "allowed_warning")
      state.safeStatus = "ready";
    if (signal.status === "rejected") {
      state.observedUtilization = 100;
      state.safeStatus = "quota_exhausted";
      state.cooldownUntil = Math.max(
        state.cooldownUntil,
        resetTime(signal.resetsAt) ?? this.now() + RATE_LIMIT_COOLDOWN_MS,
      );
    }
  }

  markSuccess(lease: ClaudeAccountLease): void {
    const state = this.states[lease.ordinal];
    if (state) state.safeStatus = "ready";
  }

  markAccountFailure(
    lease: ClaudeAccountLease,
    failure: AiFailure,
    signal?: ClaudeRateLimitSignal,
  ): void {
    const state = this.states[lease.ordinal];
    if (!state) return;
    this.observeSignal(lease, signal);
    if (failure.reason === "auth")
      {
        state.safeStatus = "auth_invalid";
        state.cooldownUntil = Math.max(state.cooldownUntil, this.now() + AUTH_COOLDOWN_MS);
      }
    else if (isClaudeAccountLocalFailure(failure, signal)) {
      state.safeStatus = "quota_exhausted";
      state.observedUtilization = 100;
      state.cooldownUntil = Math.max(state.cooldownUntil, this.now() + RATE_LIMIT_COOLDOWN_MS);
    }
  }

  health(): ProviderAccountHealth {
    const now = this.now();
    for (const state of this.states) this.recoverExpiredState(state, now);
    return {
      configuredAccounts: this.states.length,
      availableAccounts: this.states.filter((state) =>
        state.cooldownUntil <= now
        && (this.score(state, "claude-sonnet-4-6") ?? 0) < 100).length,
      inFlight: this.states.reduce((total, state) => total + state.inFlight, 0),
    };
  }

  accountStatuses(): readonly RoutedAccountStatus[] {
    const now = this.now();
    return this.states.flatMap((state) => {
      this.recoverExpiredState(state, now);
      if (!state.route) return [];
      const quotaExhausted = (this.score(state, "claude-sonnet-4-6") ?? 0) >= 100;
      const status = state.cooldownUntil > now
        ? state.safeStatus === "auth_invalid" ? "auth_invalid" : "cooldown"
        : quotaExhausted ? "quota_exhausted" : state.safeStatus;
      return [{
        accountId: state.route.accountId,
        poolId: state.route.poolId,
        status,
        available: state.cooldownUntil <= now && !quotaExhausted,
        inFlight: state.inFlight,
      }];
    });
  }
}

export function createClaudeAccountsFromEnvironment(
  source: NodeJS.ProcessEnv,
  factories: ClaudeAccountFactories = {},
): readonly ClaudeAccount[] {
  const environments = discoverClaudeAccountEnvironments(source, factories.isolatedHome);
  if (environments.length === 0) return [];
  const workingDirectory = (factories.isolatedWorkingDirectory
    ?? (() => isolatedDirectory("rb-ai-claude-quota-")))();
  if (!isAbsolute(workingDirectory)) throw new Error("Claude-probemap moet een absoluut pad zijn");
  const makeReader = factories.quotaReader
    ?? ((environment: ClaudeAccountEnvironment, directory: string) =>
      environment.ANTHROPIC_API_KEY
        ? new NoopClaudeQuotaReader()
        : new SdkClaudeQuotaReader(environment, directory));
  return environments.map((environment) => ({
    environment,
    quotaReader: makeReader(environment, workingDirectory),
  }));
}

export function createClaudeAccountRouter(
  source: NodeJS.ProcessEnv,
  factories: ClaudeAccountFactories = {},
): ClaudeAccountRouter {
  return new ClaudeAccountRouter(createClaudeAccountsFromEnvironment(source, factories));
}

let fallbackClaudeAccountRouter: ClaudeAccountRouter | undefined;

export function getFallbackClaudeAccountRouter(): ClaudeAccountRouter {
  return fallbackClaudeAccountRouter
    ??= createClaudeAccountRouter(process.env);
}

export class ClaudeAccountPoolProvider implements ToolProvider {
  readonly id = "claude-agent-sdk";

  constructor(
    private readonly router: ClaudeAccountRouter = getFallbackClaudeAccountRouter(),
    private readonly providerFactory: (environment: ClaudeAccountEnvironment) => ClaudeAgentToolProvider =
      (environment) => new ClaudeAgentToolProvider(query as unknown as QueryRunner, environment),
  ) {}

  configured(): boolean {
    return this.router.configured();
  }

  health(): ProviderAccountHealth {
    return this.router.health();
  }

  async invokeTool(request: ToolProviderRequest): Promise<ToolProviderResult> {
    if (!this.router.configured())
      return {
        failure: { reason: "auth", detail: "geen Claude-accounts geconfigureerd" },
        usage: null,
      };
    await this.router.refreshQuotas();
    const excluded = new Set<number>();
    let lastFailure: AiFailure | undefined;
    while (true) {
      if (request.signal.aborted)
        return {
          failure: { reason: "aborted", detail: "Claude-aanroep afgebroken" },
          usage: null,
        };
      const lease = this.router.acquire(request.modelId, excluded);
      if (!lease)
        return {
          failure: lastFailure
            ?? { reason: "api_error", detail: "geen Claude-account met gebruiksruimte beschikbaar" },
          usage: null,
        };
      excluded.add(lease.ordinal);
      let toolCalled = false;
      try {
        const result = await this.providerFactory(lease.environment).invokeTool({
          ...request,
          onToolCall: (name, input) => {
            toolCalled = true;
            return request.onToolCall(name, input);
          },
        });
        if (!result.failure) this.router.markSuccess(lease);
        this.router.observeSignal(lease, result.accountSignal);
        if (
          result.failure
          && !toolCalled
          && isClaudeAccountLocalFailure(result.failure, result.accountSignal)
        ) {
          lastFailure = result.failure;
          this.router.markAccountFailure(lease, result.failure, result.accountSignal);
          continue;
        }
        const { accountSignal: _accountSignal, ...providerResult } = result;
        return providerResult;
      } finally {
        lease.release();
      }
    }
  }
}
