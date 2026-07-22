import { mkdir, chmod, rm } from "node:fs/promises";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  AppServerQuotaReader,
  CodexAccountPoolProvider,
  SdkCodexRunner,
  createCodexAccountsFromEnvironment,
  type CodexAccount,
  type MinimalEnvironment,
} from "../providers/codex.js";
import {
  ClaudeAccountPoolProvider,
  ClaudeAccountRouter,
  NoopClaudeQuotaReader,
  SdkClaudeQuotaReader,
  createClaudeAccountsFromEnvironment,
  discoverClaudeAccountEnvironments,
  type ClaudeAccount,
} from "../providers/claude-accounts.js";
import { ProviderRegistry, MODEL_TARGETS } from "../providers/registry.js";
import type {
  ModelAlias,
  ProviderAccountHealth,
  ToolProvider,
  ToolProviderRequest,
  ToolProviderResult,
} from "../providers/types.js";
import {
  emptyVaultDocument,
  type ManagedAccountRecord,
  type ManagedProviderId,
  type PublicAccount,
  type PublicPool,
  type SafeAccountStatus,
  type VaultDocument,
} from "./types.js";
import { registerRuntimeSecrets } from "../failure.js";

const ENV_PRIORITY = -100;
const ENV_WEIGHT = 1;

interface SafeAccountDescriptor {
  id: string;
  poolId: string;
  label: string;
  enabled: boolean;
  poolEnabled: boolean;
  authType: PublicAccount["authType"];
  credentialConfigured: boolean;
  editable: boolean;
  lastTestedAt?: string;
  persistedStatus?: SafeAccountStatus;
}

interface SafePoolDescriptor {
  id: string;
  provider: ManagedProviderId;
  label: string;
  enabled: boolean;
  priority: number;
  weight: number;
  source: "managed" | "environment";
  editable: boolean;
}

export interface RuntimeBuildOptions {
  source?: NodeJS.ProcessEnv;
  configDirectory?: string;
  claudeAccountsFromEnvironment?: typeof createClaudeAccountsFromEnvironment;
  codexAccountsFromEnvironment?: typeof createCodexAccountsFromEnvironment;
  codexAccountFactory?: (
    environment: MinimalEnvironment,
    workingDirectory: string,
  ) => CodexAccount;
  temporaryDirectory?: (prefix: string) => string;
}

export class RuntimeTopologyConflictError extends Error {
  constructor() {
    super("runtime_topology_conflict");
    this.name = "RuntimeTopologyConflictError";
  }
}

export class RuntimeGeneration {
  private active = 0;
  private retired = false;
  private drained?: Promise<void>;
  private resolveDrained?: () => void;
  private disposePromise?: Promise<void>;

  constructor(
    readonly id: number,
    readonly document: VaultDocument,
    readonly claudeRouter: ClaudeAccountRouter,
    readonly claudeProvider: ClaudeAccountPoolProvider,
    readonly codexProvider: CodexAccountPoolProvider,
    readonly pools: readonly SafePoolDescriptor[],
    readonly accounts: readonly SafeAccountDescriptor[],
    private readonly cleanup: () => void | Promise<void> = () => {},
  ) {}

  acquire(): () => void {
    this.active += 1;
    let released = false;
    return () => {
      if (released) return;
      released = true;
      this.active -= 1;
      if (this.retired && this.active === 0) this.resolveDrained?.();
    };
  }

  retire(): Promise<void> {
    this.retired = true;
    if (this.active === 0) return Promise.resolve();
    if (!this.drained)
      this.drained = new Promise<void>((resolve) => {
        this.resolveDrained = resolve;
      });
    return this.drained;
  }

  activeLeases(): number {
    return this.active;
  }

  dispose(): Promise<void> {
    return this.disposePromise ??= Promise.resolve().then(async () => await this.cleanup());
  }
}

async function removeOwnedTemporaryPaths(paths: readonly string[]): Promise<void> {
  await Promise.all(paths.map(async (path) => {
    await rm(path, { recursive: true, force: true });
  }));
}

function safeClaudeSource(source: NodeJS.ProcessEnv, authType: ManagedAccountRecord["authType"], secret: string) {
  const selected: NodeJS.ProcessEnv = {
    PATH: source.PATH,
    TMPDIR: source.TMPDIR,
    LANG: source.LANG,
    LC_ALL: source.LC_ALL,
    SSL_CERT_FILE: source.SSL_CERT_FILE,
    SSL_CERT_DIR: source.SSL_CERT_DIR,
    NODE_EXTRA_CA_CERTS: source.NODE_EXTRA_CA_CERTS,
  };
  if (authType === "oauth-token") selected.CLAUDE_CODE_OAUTH_TOKEN = secret;
  else selected.ANTHROPIC_API_KEY = secret;
  return selected;
}

function publicAuthTypeForClaude(environment: Readonly<Record<string, string>>) {
  return environment.CLAUDE_CODE_OAUTH_TOKEN ? "oauth-token" as const : "api-key" as const;
}

function publicAuthTypeForCodex(environment: MinimalEnvironment | undefined) {
  return environment?.CODEX_ACCESS_TOKEN ? "access-token" as const : "chatgpt-device" as const;
}

async function secureDirectory(path: string): Promise<void> {
  await mkdir(path, { recursive: true, mode: 0o700 });
  await chmod(path, 0o700);
}

function poolCredentialConfigured(account: ManagedAccountRecord): boolean {
  return account.authType === "chatgpt-device"
    ? account.deviceAuthorized === true
    : Boolean(account.secret);
}

export class RuntimeGenerationManager {
  private current: RuntimeGeneration;
  private nextId = 1;
  private readonly retired = new Set<RuntimeGeneration>();
  private readonly source: NodeJS.ProcessEnv;
  private readonly configDirectory: string;
  private readonly options: RuntimeBuildOptions;
  private readonly topologyListeners = new Set<() => void>();
  readonly registry: ProviderRegistry;

  constructor(options: RuntimeBuildOptions = {}) {
    this.options = options;
    this.source = options.source ?? process.env;
    this.configDirectory = options.configDirectory
      ?? (this.source.RB_AI_CONFIG_DIR?.trim() || "/var/lib/rb-ai/config");
    this.current = this.buildEnvironmentGeneration();
    this.registry = new ProviderRegistry([
      new GenerationToolProvider("claude-agent-sdk", this),
      new GenerationToolProvider("codex-sdk", this),
    ]);
  }

  private buildEnvironmentParts(suppressed: {
    claude: ReadonlySet<string>;
    codex: ReadonlySet<string>;
  } = { claude: new Set(), codex: new Set() }): {
    claude: ClaudeAccount[];
    codex: CodexAccount[];
    pools: SafePoolDescriptor[];
    accounts: SafeAccountDescriptor[];
    ownedTemporaryPaths: string[];
  } {
    const selectedSource: NodeJS.ProcessEnv = { ...this.source };
    for (const [name, value] of Object.entries(selectedSource)) {
      if (/^(?:CLAUDE_CODE_OAUTH_TOKEN|ANTHROPIC_API_KEY)(?:_\d+)?$/.test(name)
        && value && suppressed.claude.has(value))
        selectedSource[name] = "";
      const codex = /^CODEX_ACCESS_TOKEN(_\d+)?$/.exec(name);
      if (codex && value && suppressed.codex.has(value)) {
        selectedSource[name] = "";
        selectedSource[`CODEX_HOME${codex[1] ?? ""}`] = "";
      }
    }
    const ownedTemporaryPaths: string[] = [];
    const ownedDirectory = (prefix: string) => {
      const path = this.options.temporaryDirectory?.(prefix)
        ?? mkdtempSync(join(tmpdir(), prefix));
      ownedTemporaryPaths.push(path);
      return path;
    };
    const claudeRaw = this.options.claudeAccountsFromEnvironment
      ? this.options.claudeAccountsFromEnvironment(selectedSource)
      : createClaudeAccountsFromEnvironment(selectedSource, {
          isolatedHome: () => ownedDirectory("rb-ai-runtime-claude-home-"),
          isolatedWorkingDirectory: () => ownedDirectory("rb-ai-runtime-claude-work-"),
        });
    const codexRaw = this.options.codexAccountsFromEnvironment
      ? this.options.codexAccountsFromEnvironment(selectedSource)
      : createCodexAccountsFromEnvironment(selectedSource, {
          isolatedHome: () => ownedDirectory("rb-ai-runtime-codex-home-"),
          isolatedWorkingDirectory: () => ownedDirectory("rb-ai-runtime-codex-work-"),
        });
    const claudePoolId = "environment-claude";
    const codexPoolId = "environment-codex";
    const claude = claudeRaw.flatMap((account, index) => {
      const credential = account.environment.CLAUDE_CODE_OAUTH_TOKEN
        ?? account.environment.ANTHROPIC_API_KEY;
      if (credential && suppressed.claude.has(credential)) return [];
      return [{
        ...account,
        route: {
          accountId: `environment-claude-${index + 1}`,
          poolId: claudePoolId,
          priority: ENV_PRIORITY,
          weight: ENV_WEIGHT,
        },
      }];
    });
    const codex = codexRaw.flatMap((account, index) => {
      const credential = account.environment?.CODEX_ACCESS_TOKEN;
      if (credential && suppressed.codex.has(credential)) return [];
      return [{
        ...account,
        route: {
          accountId: `environment-codex-${index + 1}`,
          poolId: codexPoolId,
          priority: ENV_PRIORITY,
          weight: ENV_WEIGHT,
        },
      }];
    });
    const pools: SafePoolDescriptor[] = [];
    const accounts: SafeAccountDescriptor[] = [];
    if (claude.length > 0) {
      pools.push({
        id: claudePoolId,
        provider: "claude-agent-sdk",
        label: "Environment bootstrap",
        enabled: true,
        priority: ENV_PRIORITY,
        weight: ENV_WEIGHT,
        source: "environment",
        editable: false,
      });
      accounts.push(...claude.map((account) => ({
        id: account.route.accountId,
        poolId: claudePoolId,
        label: `Environment account ${account.route.accountId.split("-").at(-1)}`,
        enabled: true,
        poolEnabled: true,
        authType: publicAuthTypeForClaude(account.environment),
        credentialConfigured: true,
        editable: false,
      })));
    }
    if (codex.length > 0) {
      pools.push({
        id: codexPoolId,
        provider: "codex-sdk",
        label: "Environment bootstrap",
        enabled: true,
        priority: ENV_PRIORITY,
        weight: ENV_WEIGHT,
        source: "environment",
        editable: false,
      });
      accounts.push(...codex.map((account) => ({
        id: account.route.accountId,
        poolId: codexPoolId,
        label: `Environment account ${account.route.accountId.split("-").at(-1)}`,
        enabled: true,
        poolEnabled: true,
        authType: publicAuthTypeForCodex(account.environment),
        credentialConfigured: true,
        editable: false,
      })));
    }
    return { claude, codex, pools, accounts, ownedTemporaryPaths };
  }

  private buildEnvironmentGeneration(): RuntimeGeneration {
    const { claude, codex, pools, accounts, ownedTemporaryPaths } = this.buildEnvironmentParts();
    const claudeRouter = new ClaudeAccountRouter(claude);
    const codexProvider = new CodexAccountPoolProvider({ accounts: codex });
    return new RuntimeGeneration(
      this.nextId++,
      emptyVaultDocument(),
      claudeRouter,
      new ClaudeAccountPoolProvider(claudeRouter),
      codexProvider,
      pools,
      accounts,
      async () => await removeOwnedTemporaryPaths(ownedTemporaryPaths),
    );
  }

  private async build(document: VaultDocument): Promise<RuntimeGeneration> {
    const enabledManagedCredentials = new Set<string>();
    const managedClaudeCredentials = new Set<string>();
    const managedCodexCredentials = new Set<string>();
    for (const pool of document.pools) {
      if (!pool.enabled) continue;
      for (const account of pool.accounts) {
        if (!account.enabled || !account.secret) continue;
        if (enabledManagedCredentials.has(account.secret))
          throw new RuntimeTopologyConflictError();
        enabledManagedCredentials.add(account.secret);
        (pool.provider === "claude-agent-sdk"
          ? managedClaudeCredentials
          : managedCodexCredentials).add(account.secret);
      }
    }
    // During env→managed migration the managed account owns the credential
    // immediately. Suppress the duplicate bootstrap route so there is no
    // outage window and no chance of concurrent use through two homes.
    const parts = this.buildEnvironmentParts({
      claude: managedClaudeCredentials,
      codex: managedCodexCredentials,
    });
    try {
    const claude: ClaudeAccount[] = [...parts.claude];
    const codex: CodexAccount[] = [...parts.codex];
    const pools: SafePoolDescriptor[] = [...parts.pools];
    const accounts: SafeAccountDescriptor[] = [...parts.accounts];
    const credentials = new Set<string>();
    for (const item of claude) {
      const credential = item.environment.CLAUDE_CODE_OAUTH_TOKEN ?? item.environment.ANTHROPIC_API_KEY;
      if (credential) credentials.add(credential);
    }
    for (const item of codex) {
      if (item.environment?.CODEX_ACCESS_TOKEN) credentials.add(item.environment.CODEX_ACCESS_TOKEN);
    }

    const claudeRoot = join(this.configDirectory, "accounts", "claude");
    const codexRoot = join(this.configDirectory, "accounts", "codex");
    const codexWork = join(this.configDirectory, "work", "codex");
    await Promise.all([secureDirectory(claudeRoot), secureDirectory(codexRoot), secureDirectory(codexWork)]);

    for (const pool of document.pools) {
      pools.push({
        id: pool.id,
        provider: pool.provider,
        label: pool.label,
        enabled: pool.enabled,
        priority: pool.priority,
        weight: pool.weight,
        source: "managed",
        editable: true,
      });
      for (const item of pool.accounts) {
        const configured = poolCredentialConfigured(item);
        accounts.push({
          id: item.id,
          poolId: pool.id,
          label: item.label,
          enabled: item.enabled,
          poolEnabled: pool.enabled,
          authType: item.authType,
          credentialConfigured: configured,
          editable: true,
          ...(item.lastTestedAt ? { lastTestedAt: item.lastTestedAt } : {}),
          ...(item.lastStatus
            ? { persistedStatus: item.enabled && item.lastStatus === "disabled"
                ? "unknown" as const
                : item.lastStatus }
            : {}),
        });
        if (!pool.enabled || !item.enabled || !configured) continue;
        if (item.secret) {
          if (credentials.has(item.secret)) throw new RuntimeTopologyConflictError();
          credentials.add(item.secret);
        }
        const route = {
          accountId: item.id,
          poolId: pool.id,
          priority: pool.priority,
          weight: pool.weight,
        };
        if (pool.provider === "claude-agent-sdk") {
          if (item.authType !== "oauth-token" && item.authType !== "api-key") continue;
          const home = join(claudeRoot, item.id);
          await secureDirectory(home);
          const [environment] = discoverClaudeAccountEnvironments(
            safeClaudeSource(this.source, item.authType, item.secret as string),
            () => home,
          );
          if (!environment) continue;
          claude.push({
            environment,
            quotaReader: item.authType === "api-key"
              ? new NoopClaudeQuotaReader()
              : new SdkClaudeQuotaReader(environment, join(this.configDirectory, "work")),
            route,
            ...(item.lastStatus === "auth_invalid"
              ? { initialStatus: "auth_invalid" as const }
              : {}),
          });
        } else {
          const homeName = item.homeName ?? item.id;
          const home = join(codexRoot, homeName);
          await secureDirectory(home);
          const environment: MinimalEnvironment = Object.freeze({
            CODEX_HOME: home,
            ...(item.authType === "access-token" ? { CODEX_ACCESS_TOKEN: item.secret as string } : {}),
          });
          const makeAccount = this.options.codexAccountFactory
            ?? ((env: MinimalEnvironment, cwd: string): CodexAccount => ({
              environment: env,
              runner: new SdkCodexRunner(env, cwd),
              quotaReader: new AppServerQuotaReader(env),
            }));
          codex.push({
            ...makeAccount(environment, codexWork),
            environment,
            route,
            ...(item.lastStatus === "auth_invalid"
              ? { initialStatus: "auth_invalid" as const }
              : {}),
          });
        }
      }
    }

    const claudeRouter = new ClaudeAccountRouter(claude);
    const codexProvider = new CodexAccountPoolProvider({ accounts: codex });
    const managedSecrets = document.pools.flatMap((pool) =>
      pool.accounts.flatMap((account) => account.secret ? [account.secret] : []));
    const releaseSecrets = registerRuntimeSecrets(managedSecrets);
    return new RuntimeGeneration(
      this.nextId++,
      structuredClone(document),
      claudeRouter,
      new ClaudeAccountPoolProvider(claudeRouter),
      codexProvider,
      pools,
      accounts,
      async () => {
        releaseSecrets();
        await removeOwnedTemporaryPaths(parts.ownedTemporaryPaths);
      },
    );
    } catch (error) {
      await removeOwnedTemporaryPaths(parts.ownedTemporaryPaths);
      throw error;
    }
  }

  async prepare(document: VaultDocument): Promise<RuntimeGeneration> {
    return await this.build(document);
  }

  swap(
    next: RuntimeGeneration,
    afterPreviousDrained?: () => void | Promise<void>,
  ): void {
    const previous = this.current;
    this.current = next;
    for (const listener of this.topologyListeners) {
      try {
        listener();
      } catch {
        // Topology invalidation is best-effort; generation retirement is not.
      }
    }
    this.retired.add(previous);
    void previous.retire().then(async () => {
      await previous.dispose().catch(() => {});
      this.retired.delete(previous);
      try {
        await afterPreviousDrained?.();
      } catch {
        // Cleanup must never compromise the live runtime generation.
      }
    });
  }

  async replace(document: VaultDocument): Promise<void> {
    this.swap(await this.prepare(document));
  }

  onTopologyChange(listener: () => void): () => void {
    this.topologyListeners.add(listener);
    return () => this.topologyListeners.delete(listener);
  }

  lease(): { generation: RuntimeGeneration; release: () => void } {
    const generation = this.current;
    return { generation, release: generation.acquire() };
  }

  currentGeneration(): RuntimeGeneration {
    return this.current;
  }

  retiredGenerationCount(): number {
    return this.retired.size;
  }

  snapshot(): {
    generation: number;
    providers: Array<ProviderAccountHealth & {
      id: ManagedProviderId;
      configured: boolean;
      status: string;
    }>;
    pools: PublicPool[];
    accounts: PublicAccount[];
  } {
    const generation = this.current;
    const claudeStatuses = new Map(generation.claudeRouter.accountStatuses()
      .map((status) => [status.accountId, status]));
    const codexStatuses = new Map(generation.codexProvider.accountStatuses()
      .map((status) => [status.accountId, status]));
    const statuses = new Map([...claudeStatuses, ...codexStatuses]);
    const descriptors = new Map(generation.accounts.map((account) => [account.id, account]));
    const accounts = generation.accounts.map((account): PublicAccount => {
      const live = statuses.get(account.id);
      const status: SafeAccountStatus = !account.enabled || !account.poolEnabled
        ? "disabled"
        : live && live.status !== "unknown"
          ? live.status
          : account.persistedStatus ?? live?.status ?? "unknown";
      return {
        id: account.id,
        poolId: account.poolId,
        label: account.label,
        enabled: account.enabled,
        authType: account.authType,
        status,
        ...(account.lastTestedAt ? { lastTestedAt: account.lastTestedAt } : {}),
        credentialConfigured: account.credentialConfigured,
        editable: account.editable,
      };
    });
    const pools = generation.pools.map((pool): PublicPool => {
      const members = accounts.filter((account) => account.poolId === pool.id);
      const availableAccounts = members.filter((account) =>
        account.enabled
        && descriptors.get(account.id)?.credentialConfigured === true
        && statuses.get(account.id)?.available !== false
        && ["ready", "unknown"].includes(account.status)).length;
      const status: SafeAccountStatus = !pool.enabled
        ? "disabled"
        : availableAccounts > 0
          ? members.some((account) => account.status === "ready") ? "ready" : "unknown"
          : members.some((account) => account.status === "auth_invalid")
            ? "auth_invalid"
            : members.some((account) => account.status === "quota_exhausted")
              ? "quota_exhausted"
              : members.some((account) => account.status === "cooldown")
                ? "cooldown"
              : "unknown";
      return {
        ...pool,
        accountCount: members.length,
        availableAccounts,
        status,
      };
    });
    const providerStatus = (id: ManagedProviderId, health: ProviderAccountHealth) => ({
      id,
      ...health,
      configured: health.configuredAccounts > 0,
      status: health.configuredAccounts === 0
        ? "unconfigured"
        : health.availableAccounts === 0
          ? "unavailable"
          : health.availableAccounts < health.configuredAccounts ? "degraded" : "ready",
    });
    return {
      generation: generation.id,
      providers: [
        providerStatus("claude-agent-sdk", generation.claudeProvider.health()),
        providerStatus("codex-sdk", generation.codexProvider.health()),
      ],
      pools,
      accounts,
    };
  }

  models(): Array<{ alias: ModelAlias; provider: string; model: string; capabilities: string[] }> {
    return (Object.entries(MODEL_TARGETS) as Array<[
      ModelAlias,
      { providerId: string; modelId: string },
    ]>).map(([alias, target]) => ({
      alias,
      provider: target.providerId,
      model: target.modelId,
      capabilities: ["structured-output"],
    }));
  }
}

class GenerationToolProvider implements ToolProvider {
  constructor(
    readonly id: ManagedProviderId,
    private readonly manager: RuntimeGenerationManager,
  ) {}

  private selected(generation: RuntimeGeneration): ToolProvider {
    return this.id === "claude-agent-sdk" ? generation.claudeProvider : generation.codexProvider;
  }

  configured(): boolean {
    return this.selected(this.manager.currentGeneration()).configured();
  }

  health(): ProviderAccountHealth {
    return this.selected(this.manager.currentGeneration()).health?.()
      ?? { configuredAccounts: 0, availableAccounts: 0, inFlight: 0 };
  }

  async invokeTool(request: ToolProviderRequest): Promise<ToolProviderResult> {
    const lease = this.manager.lease();
    try {
      return await this.selected(lease.generation).invokeTool(request);
    } finally {
      lease.release();
    }
  }
}

export const runtimeManager = new RuntimeGenerationManager();
export const runtimeProviderRegistry = runtimeManager.registry;
