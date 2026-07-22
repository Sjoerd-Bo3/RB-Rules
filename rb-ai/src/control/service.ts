import { createHash, randomUUID, timingSafeEqual } from "node:crypto";
import { chmod, mkdir, readdir, rm } from "node:fs/promises";
import { join, resolve } from "node:path";
import { z } from "zod";
import {
  SdkClaudeQuotaReader,
  discoverClaudeAccountEnvironments,
} from "../providers/claude-accounts.js";
import type { MinimalEnvironment } from "../providers/codex.js";
import {
  CodexDeviceLoginManager,
  type DeviceLoginTransportFactory,
} from "./codex-login.js";
import {
  RuntimeGenerationManager,
  RuntimeTopologyConflictError,
  runtimeManager,
} from "./runtime.js";
import {
  authTypeAllowed,
  managedPoolSchema,
  providerIdSchema,
  type ManagedAccountRecord,
  type ManagedPoolRecord,
  type PublicAccount,
  type PublicPool,
  type SafeAccountStatus,
  type VaultDocument,
} from "./types.js";
import { ManagedVault, vaultFromEnvironment } from "./vault.js";

const labelSchema = z.string().trim().min(1).max(80)
  .refine((value) => !/[\u0000-\u001f\u007f]/.test(value));
const prioritySchema = z.number().int().min(-100).max(100);
const weightSchema = z.number().int().min(1).max(100);
const credentialSchema = z.string().trim().min(8).max(32_768);
const serverHomeNameSchema = z.string().uuid();

interface ManagedHomeRef {
  provider: "claude-agent-sdk" | "codex-sdk";
  name: string;
}

const createPoolSchema = z.object({
  provider: providerIdSchema,
  label: labelSchema,
  enabled: z.boolean().optional(),
  priority: prioritySchema.optional(),
  weight: weightSchema.optional(),
}).strict();

const patchPoolSchema = z.object({
  label: labelSchema.optional(),
  enabled: z.boolean().optional(),
  priority: prioritySchema.optional(),
  weight: weightSchema.optional(),
}).strict().refine((value) => Object.keys(value).length > 0);

const createAccountSchema = z.object({
  poolId: z.string().uuid(),
  label: labelSchema,
  authType: z.enum(["oauth-token", "api-key", "access-token", "chatgpt-device"]),
  enabled: z.boolean().optional(),
  credential: credentialSchema.optional(),
}).strict();

const patchAccountSchema = z.object({
  label: labelSchema.optional(),
  enabled: z.boolean().optional(),
}).strict().refine((value) => Object.keys(value).length > 0);

export class ControlError extends Error {
  constructor(
    readonly status: number,
    readonly code:
      | "control_unavailable"
      | "unauthorized"
      | "invalid_request"
      | "not_found"
      | "conflict"
      | "device_login_unavailable",
  ) {
    super(code);
    this.name = "ControlError";
  }
}

interface AccountTestContext {
  provider: "claude-agent-sdk" | "codex-sdk";
  account: ManagedAccountRecord;
  configDirectory: string;
  source: NodeJS.ProcessEnv;
}

export interface ManagedAccountTester {
  test(context: AccountTestContext): Promise<SafeAccountStatus>;
}

export interface ClaudeApiKeyProbe {
  test(apiKey: string): Promise<SafeAccountStatus>;
}

export class AnthropicApiKeyProbe implements ClaudeApiKeyProbe {
  constructor(
    private readonly fetcher: typeof fetch = fetch,
    private readonly timeoutMs = 5_000,
  ) {}

  async test(apiKey: string): Promise<SafeAccountStatus> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.timeoutMs);
    timer.unref?.();
    try {
      const response = await this.fetcher("https://api.anthropic.com/v1/models?limit=1", {
        method: "GET",
        headers: {
          "anthropic-version": "2023-06-01",
          "x-api-key": apiKey,
        },
        signal: controller.signal,
      });
      await response.body?.cancel().catch(() => {});
      if (response.status >= 200 && response.status < 300) return "ready";
      return response.status === 401 || response.status === 403 ? "auth_invalid" : "unknown";
    } catch (error) {
      return error instanceof Error && /authentication[_\s-]?failed/i.test(error.message)
        ? "auth_invalid"
        : "unknown";
    } finally {
      clearTimeout(timer);
    }
  }
}

class DefaultManagedAccountTester implements ManagedAccountTester {
  constructor(
    private readonly codex: DeviceLoginTransportFactory,
    private readonly claudeApiKey: ClaudeApiKeyProbe,
  ) {}

  async test(context: AccountTestContext): Promise<SafeAccountStatus> {
    const { provider, account, configDirectory, source } = context;
    if (!account.enabled) return "disabled";
    if (provider === "claude-agent-sdk") {
      if (!account.secret) return "unknown";
      if (account.authType === "api-key")
        return await this.claudeApiKey.test(account.secret);
      const home = join(configDirectory, "accounts", "claude", account.id);
      await mkdir(home, { recursive: true, mode: 0o700 });
      await chmod(home, 0o700);
      const selected: NodeJS.ProcessEnv = {
        PATH: source.PATH,
        TMPDIR: source.TMPDIR,
        ...(account.authType === "oauth-token"
          ? { CLAUDE_CODE_OAUTH_TOKEN: account.secret }
          : { ANTHROPIC_API_KEY: account.secret }),
      };
      const [environment] = discoverClaudeAccountEnvironments(selected, () => home);
      if (!environment) return "unknown";
      const result = await new SdkClaudeQuotaReader(
        environment,
        join(configDirectory, "work"),
      ).probe();
      return result.status;
    }
    const home = join(configDirectory, "accounts", "codex", account.homeName ?? account.id);
    const environment: MinimalEnvironment = {
      CODEX_HOME: home,
      ...(account.authType === "access-token" && account.secret
        ? { CODEX_ACCESS_TOKEN: account.secret }
        : {}),
    };
    const result = await this.codex.readAccount?.(environment);
    return result === true ? "ready" : result === false ? "auth_invalid" : "unknown";
  }
}

export interface ControlServiceOptions {
  vault: ManagedVault;
  runtime?: RuntimeGenerationManager;
  source?: NodeJS.ProcessEnv;
  configDirectory: string;
  deviceLogins?: CodexDeviceLoginManager;
  accountTester?: ManagedAccountTester;
  claudeApiKeyProbe?: ClaudeApiKeyProbe;
}

export class AiControlService {
  private document!: VaultDocument;
  private readonly runtime: RuntimeGenerationManager;
  private readonly source: NodeJS.ProcessEnv;
  private readonly configDirectory: string;
  private readonly deviceLogins: CodexDeviceLoginManager;
  private readonly accountTester: ManagedAccountTester;
  private mutationTail: Promise<void> = Promise.resolve();
  private readonly initialized: Promise<void>;

  constructor(private readonly vault: ManagedVault, options: Omit<ControlServiceOptions, "vault">) {
    this.runtime = options.runtime ?? runtimeManager;
    this.source = options.source ?? process.env;
    this.configDirectory = options.configDirectory;
    this.deviceLogins = options.deviceLogins ?? new CodexDeviceLoginManager();
    this.accountTester = options.accountTester
      ?? new DefaultManagedAccountTester(
        this.deviceLogins.transportFactory(),
        options.claudeApiKeyProbe ?? new AnthropicApiKeyProbe(),
      );
    this.initialized = this.initialize();
    // Control configuration is optional. Keep a failed vault initialization
    // available to `ready()` as a safe 503 without creating an unhandled
    // rejection when no administrator opens the control endpoint at startup.
    void this.initialized.catch(() => {});
  }

  private async initialize(): Promise<void> {
    try {
      const document = await this.vault.load();
      await this.runtime.replace(document);
      this.document = document;
      await this.reconcileManagedHomes(document).catch(() => {});
    } catch {
      throw new ControlError(503, "control_unavailable");
    }
  }

  async ready(): Promise<void> {
    return await this.initialized;
  }

  private async serial<T>(work: () => Promise<T>): Promise<T> {
    const run = this.mutationTail.then(work, work);
    this.mutationTail = run.then(() => {}, () => {});
    return await run;
  }

  private homeRoot(provider: ManagedHomeRef["provider"]): string {
    return join(
      this.configDirectory,
      "accounts",
      provider === "claude-agent-sdk" ? "claude" : "codex",
    );
  }

  private async removeManagedHomes(refs: readonly ManagedHomeRef[]): Promise<void> {
    const unique = new Map(refs.map((ref) => [`${ref.provider}:${ref.name}`, ref]));
    for (const { provider, name } of unique.values()) {
      if (!serverHomeNameSchema.safeParse(name).success) continue;
      await rm(join(this.homeRoot(provider), name), { recursive: true, force: true });
    }
  }

  private async reconcileManagedHomes(document: VaultDocument): Promise<void> {
    const live = new Map<ManagedHomeRef["provider"], Set<string>>([
      ["claude-agent-sdk", new Set()],
      ["codex-sdk", new Set()],
    ]);
    for (const pool of document.pools)
      for (const account of pool.accounts)
        live.get(pool.provider)?.add(
          pool.provider === "claude-agent-sdk" ? account.id : account.homeName ?? account.id,
        );
    const configuredCodexHomes = new Set(Object.entries(this.source)
      .filter(([name, value]) => /^CODEX_HOME(?:_\d+)?$/.test(name) && value?.trim())
      .map(([, value]) => resolve(value as string)));
    for (const provider of ["claude-agent-sdk", "codex-sdk"] as const) {
      const root = this.homeRoot(provider);
      let entries;
      try {
        entries = await readdir(root, { withFileTypes: true });
      } catch {
        continue;
      }
      for (const entry of entries) {
        if (!entry.isDirectory() || !serverHomeNameSchema.safeParse(entry.name).success) continue;
        if (live.get(provider)?.has(entry.name)) continue;
        const path = join(root, entry.name);
        if (provider === "codex-sdk" && configuredCodexHomes.has(resolve(path))) continue;
        await rm(path, { recursive: true, force: true });
      }
    }
  }

  private async commit(
    next: VaultDocument,
    removeHomes: readonly ManagedHomeRef[] = [],
  ): Promise<void> {
    const candidate: VaultDocument = {
      ...next,
      version: 1,
      revision: this.document.revision + 1,
    };
    let prepared;
    try {
      prepared = await this.runtime.prepare(candidate);
    } catch (error) {
      if (error instanceof RuntimeTopologyConflictError)
        throw new ControlError(409, "conflict");
      throw new ControlError(503, "control_unavailable");
    }
    try {
      await this.vault.save(candidate);
    } catch {
      await prepared.dispose().catch(() => {});
      throw new ControlError(503, "control_unavailable");
    }
    this.runtime.swap(
      prepared,
      removeHomes.length > 0
        ? async () => await this.removeManagedHomes(removeHomes)
        : undefined,
    );
    this.document = candidate;
  }

  private safeSnapshot() {
    const snapshot = this.runtime.snapshot();
    return { ...snapshot, models: this.runtime.models() };
  }

  async get() {
    await this.ready();
    return this.safeSnapshot();
  }

  private publicPool(id: string): PublicPool {
    const pool = this.runtime.snapshot().pools.find((item) => item.id === id);
    if (!pool) throw new ControlError(404, "not_found");
    return pool;
  }

  private publicAccount(id: string): PublicAccount {
    const account = this.runtime.snapshot().accounts.find((item) => item.id === id);
    if (!account) throw new ControlError(404, "not_found");
    return account;
  }

  async createPool(body: unknown): Promise<PublicPool> {
    await this.ready();
    const parsed = createPoolSchema.safeParse(body);
    if (!parsed.success) throw new ControlError(400, "invalid_request");
    return await this.serial(async () => {
      const pool: ManagedPoolRecord = {
        id: randomUUID(),
        provider: parsed.data.provider,
        label: parsed.data.label,
        enabled: parsed.data.enabled ?? true,
        priority: parsed.data.priority ?? 0,
        weight: parsed.data.weight ?? 1,
        accounts: [],
      };
      const next = structuredClone(this.document);
      next.pools.push(managedPoolSchema.parse(pool));
      await this.commit(next);
      return this.publicPool(pool.id);
    });
  }

  async patchPool(id: string, body: unknown): Promise<PublicPool> {
    await this.ready();
    const parsed = patchPoolSchema.safeParse(body);
    if (!parsed.success) throw new ControlError(400, "invalid_request");
    return await this.serial(async () => {
      const next = structuredClone(this.document);
      const pool = next.pools.find((item) => item.id === id);
      if (!pool) throw new ControlError(404, "not_found");
      Object.assign(pool, parsed.data);
      await this.commit(next);
      return this.publicPool(id);
    });
  }

  async deletePool(id: string): Promise<void> {
    await this.ready();
    await this.serial(async () => {
      const next = structuredClone(this.document);
      const index = next.pools.findIndex((item) => item.id === id);
      if (index < 0) throw new ControlError(404, "not_found");
      const removed = next.pools[index];
      await Promise.all(removed.accounts.map((account) =>
        this.deviceLogins.cancelAccount(account.id)));
      next.pools.splice(index, 1);
      const homes = removed.accounts.map((account): ManagedHomeRef => ({
        provider: removed.provider,
        name: removed.provider === "claude-agent-sdk"
          ? account.id
          : account.homeName ?? account.id,
      }));
      await this.commit(next, homes);
    });
  }

  async createAccount(body: unknown): Promise<PublicAccount> {
    await this.ready();
    const parsed = createAccountSchema.safeParse(body);
    if (!parsed.success) throw new ControlError(400, "invalid_request");
    return await this.serial(async () => {
      const next = structuredClone(this.document);
      const pool = next.pools.find((item) => item.id === parsed.data.poolId);
      if (!pool) throw new ControlError(404, "not_found");
      if (!authTypeAllowed(pool.provider, parsed.data.authType))
        throw new ControlError(400, "invalid_request");
      if (parsed.data.authType === "chatgpt-device" && parsed.data.credential)
        throw new ControlError(400, "invalid_request");
      const account: ManagedAccountRecord = {
        id: randomUUID(),
        label: parsed.data.label,
        authType: parsed.data.authType,
        enabled: parsed.data.enabled ?? true,
        ...(parsed.data.credential ? { secret: parsed.data.credential } : {}),
        ...(pool.provider === "codex-sdk" ? { homeName: randomUUID() } : {}),
        ...(parsed.data.authType === "chatgpt-device" ? { deviceAuthorized: false } : {}),
      };
      const createdHome: ManagedHomeRef = {
        provider: pool.provider,
        name: pool.provider === "claude-agent-sdk"
          ? account.id
          : account.homeName as string,
      };
      const home = join(this.homeRoot(createdHome.provider), createdHome.name);
      await mkdir(home, { recursive: true, mode: 0o700 });
      await chmod(home, 0o700);
      pool.accounts.push(account);
      try {
        await this.commit(next);
      } catch (error) {
        await this.removeManagedHomes([createdHome]).catch(() => {});
        throw error;
      }
      return this.publicAccount(account.id);
    });
  }

  private findAccount(document: VaultDocument, id: string): {
    pool: ManagedPoolRecord;
    account: ManagedAccountRecord;
  } {
    for (const pool of document.pools) {
      const account = pool.accounts.find((item) => item.id === id);
      if (account) return { pool, account };
    }
    throw new ControlError(404, "not_found");
  }

  async patchAccount(id: string, body: unknown): Promise<PublicAccount> {
    await this.ready();
    const parsed = patchAccountSchema.safeParse(body);
    if (!parsed.success) throw new ControlError(400, "invalid_request");
    return await this.serial(async () => {
      const next = structuredClone(this.document);
      const { account } = this.findAccount(next, id);
      if (parsed.data.enabled === true && !account.enabled) {
        account.lastStatus = "unknown";
        account.lastTestedAt = undefined;
      }
      Object.assign(account, parsed.data);
      await this.commit(next);
      return this.publicAccount(id);
    });
  }

  async deleteAccount(id: string): Promise<void> {
    await this.ready();
    await this.serial(async () => {
      const next = structuredClone(this.document);
      let removed: { provider: ManagedPoolRecord["provider"]; account: ManagedAccountRecord }
        | undefined;
      for (const pool of next.pools) {
        const index = pool.accounts.findIndex((item) => item.id === id);
        if (index >= 0) {
          const account = pool.accounts[index];
          pool.accounts.splice(index, 1);
          removed = { provider: pool.provider, account };
          break;
        }
      }
      if (!removed) throw new ControlError(404, "not_found");
      await this.deviceLogins.cancelAccount(id);
      const homes: ManagedHomeRef[] = [{
        provider: removed.provider,
        name: removed.provider === "claude-agent-sdk"
          ? removed.account.id
          : removed.account.homeName ?? removed.account.id,
      }];
      await this.commit(next, homes);
    });
  }

  async putCredential(id: string, body: unknown): Promise<PublicAccount> {
    await this.ready();
    const parsed = z.object({ credential: credentialSchema }).strict().safeParse(body);
    if (!parsed.success) throw new ControlError(400, "invalid_request");
    return await this.serial(async () => {
      const next = structuredClone(this.document);
      const { account } = this.findAccount(next, id);
      if (account.authType === "chatgpt-device")
        throw new ControlError(400, "invalid_request");
      account.secret = parsed.data.credential;
      account.lastStatus = "unknown";
      account.lastTestedAt = undefined;
      await this.commit(next);
      return this.publicAccount(id);
    });
  }

  async testAccount(id: string): Promise<{
    accountId: string;
    status: SafeAccountStatus;
    lastTestedAt: string;
  }> {
    await this.ready();
    // Serialize so a credential cannot be rotated between test and persistence.
    return await this.serial(async () => {
      const current = structuredClone(this.document);
      const { pool, account } = this.findAccount(current, id);
      let status: SafeAccountStatus = "unknown";
      try {
        status = await this.accountTester.test({
          provider: pool.provider,
          account,
          configDirectory: this.configDirectory,
          source: this.source,
        });
      } catch {
        status = "unknown";
      }
      const lastTestedAt = new Date().toISOString();
      account.lastStatus = status;
      account.lastTestedAt = lastTestedAt;
      await this.commit(current);
      return { accountId: id, status, lastTestedAt };
    });
  }

  private codexEnvironment(account: ManagedAccountRecord): MinimalEnvironment {
    return {
      CODEX_HOME: join(
        this.configDirectory,
        "accounts",
        "codex",
        account.homeName ?? account.id,
      ),
    };
  }

  async startDeviceLogin(id: string) {
    await this.ready();
    return await this.serial(async () => {
      const current = structuredClone(this.document);
      const { pool, account } = this.findAccount(current, id);
      if (pool.provider !== "codex-sdk" || account.authType !== "chatgpt-device")
        throw new ControlError(400, "invalid_request");
      const environment = this.codexEnvironment(account);
      await mkdir(environment.CODEX_HOME, { recursive: true, mode: 0o700 });
      await chmod(environment.CODEX_HOME, 0o700);
      try {
        return await this.deviceLogins.start(id, environment, async (isActive) => {
          await this.serial(async () => {
            if (!isActive()) return;
            const next = structuredClone(this.document);
            const found = this.findAccount(next, id).account;
            found.deviceAuthorized = true;
            found.lastStatus = "ready";
            found.lastTestedAt = new Date().toISOString();
            await this.commit(next);
          });
        });
      } catch {
        throw new ControlError(502, "device_login_unavailable");
      }
    });
  }

  async deviceLoginStatus(id: string) {
    await this.ready();
    const status = this.deviceLogins.status(id);
    if (!status) throw new ControlError(404, "not_found");
    return status;
  }

  async cancelDeviceLogin(id: string): Promise<void> {
    await this.ready();
    await this.serial(async () => {
      if (!await this.deviceLogins.cancel(id)) throw new ControlError(404, "not_found");
    });
  }
}

function hashKey(value: string): Buffer {
  return createHash("sha256").update(value, "utf8").digest();
}

export interface AiControlPlane {
  enabled: boolean;
  authorize(value: string | string[] | undefined): boolean;
  service?: AiControlService;
}

export function createControlPlane(
  source: NodeJS.ProcessEnv = process.env,
  options: {
    runtime?: RuntimeGenerationManager;
    vault?: ManagedVault | null;
    deviceLogins?: CodexDeviceLoginManager;
    accountTester?: ManagedAccountTester;
    claudeApiKeyProbe?: ClaudeApiKeyProbe;
  } = {},
): AiControlPlane {
  const controlKey = source.RB_AI_CONTROL_KEY?.trim();
  const vault = options.vault === undefined ? vaultFromEnvironment(source) : options.vault;
  if (!controlKey || controlKey.length < 32 || !vault)
    return { enabled: false, authorize: () => false };
  const expected = hashKey(controlKey);
  const configDirectory = source.RB_AI_CONFIG_DIR?.trim() || "/var/lib/rb-ai/config";
  const service = new AiControlService(vault, {
    runtime: options.runtime,
    source,
    configDirectory,
    deviceLogins: options.deviceLogins,
    accountTester: options.accountTester,
    claudeApiKeyProbe: options.claudeApiKeyProbe,
  });
  return {
    enabled: true,
    service,
    authorize: (value) => {
      const supplied = Array.isArray(value) ? value[0] ?? "" : value ?? "";
      return timingSafeEqual(expected, hashKey(supplied));
    },
  };
}
