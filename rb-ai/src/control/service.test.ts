import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { access, mkdir, mkdtemp, readFile, readdir, stat, writeFile } from "node:fs/promises";
import type { IncomingMessage, ServerResponse } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { Readable } from "node:stream";
import { setImmediate as waitImmediate, setTimeout as wait } from "node:timers/promises";
import { describe, it } from "node:test";
import { safeDetail, registerRuntimeSecrets } from "../failure.js";
import type { ClaudeAccount } from "../providers/claude-accounts.js";
import {
  CodexDeviceLoginManager,
  type DeviceLoginTransport,
  type DeviceLoginTransportFactory,
} from "./codex-login.js";
import { createControlHttpHandler } from "./http.js";
import { RuntimeGenerationManager } from "./runtime.js";
import {
  AiControlService,
  AnthropicApiKeyProbe,
  ControlError,
  createControlPlane,
  type ManagedAccountTester,
} from "./service.js";
import { ManagedVault } from "./vault.js";

const MANAGED_SECRET = "managed-control-secret-325-do-not-leak";
const CONTROL_KEY = "control-key-325-at-least-32-characters-long";

function environmentAccount(secret = "environment-bootstrap-secret-325"): ClaudeAccount {
  return {
    environment: Object.freeze({
      CLAUDE_CODE_OAUTH_TOKEN: secret,
      HOME: "/tmp/rb-ai-control-test-home",
      CLAUDE_CONFIG_DIR: "/tmp/rb-ai-control-test-home",
    }),
    quotaReader: { read: async () => null },
  };
}

async function fixture(options: {
  environment?: boolean;
  tester?: ManagedAccountTester;
  deviceLogins?: CodexDeviceLoginManager;
} = {}) {
  const directory = await mkdtemp(join(tmpdir(), "rb-ai-control-test-"));
  const vault = new ManagedVault({ directory, key: randomBytes(32) });
  const runtime = new RuntimeGenerationManager({
    source: {},
    configDirectory: directory,
    claudeAccountsFromEnvironment: () => options.environment ? [environmentAccount()] : [],
    codexAccountsFromEnvironment: () => [],
  });
  const service = new AiControlService(vault, {
    runtime,
    source: {},
    configDirectory: directory,
    ...(options.tester ? { accountTester: options.tester } : {}),
    ...(options.deviceLogins ? { deviceLogins: options.deviceLogins } : {}),
  });
  await service.ready();
  // Initialization replaces the constructor's bootstrap generation. Give its
  // zero-lease retirement callback a chance to finish before assertions.
  await waitImmediate();
  return { directory, vault, runtime, service };
}

interface HttpResult {
  status: number;
  headers: Record<string, string | number | readonly string[]>;
  text: string;
  json: unknown;
  handled: boolean;
}

async function httpRequest(
  handler: (req: IncomingMessage, res: ServerResponse) => Promise<boolean>,
  input: {
    method?: string;
    url?: string;
    key?: string;
    body?: string;
  } = {},
): Promise<HttpResult> {
  const req = Readable.from(
    input.body === undefined ? [] : [Buffer.from(input.body)],
  ) as unknown as IncomingMessage;
  req.method = input.method ?? "GET";
  req.url = input.url ?? "/control";
  req.headers = input.key === undefined ? {} : { "x-rb-ai-control-key": input.key };
  const headers: HttpResult["headers"] = {};
  let text = "";
  const response = {
    statusCode: 200,
    setHeader(name: string, value: string | number | readonly string[]) {
      headers[name.toLowerCase()] = value;
      return this;
    },
    end(value?: string | Buffer) {
      text = value === undefined ? "" : Buffer.isBuffer(value) ? value.toString("utf8") : value;
      return this;
    },
  } as unknown as ServerResponse;
  const handled = await handler(req, response);
  let json: unknown = undefined;
  if (text) json = JSON.parse(text) as unknown;
  return { status: response.statusCode, headers, text, json, handled };
}

function isControlError(code: string) {
  return (error: unknown): boolean => {
    assert.ok(error instanceof ControlError);
    assert.equal(error.code, code);
    return true;
  };
}

function deferredDeviceTransport(code: string) {
  let resolve!: (success: boolean) => void;
  const completion = new Promise<boolean>((done) => { resolve = done; });
  const transport: DeviceLoginTransport & { cancelCalls: number; closeCalls: number } = {
    loginId: `private-upstream-${code}`,
    verificationUrl: "https://auth.openai.com/device",
    userCode: code,
    completion,
    cancelCalls: 0,
    closeCalls: 0,
    cancel: async () => {
      transport.cancelCalls += 1;
      resolve(false);
    },
    close: () => { transport.closeCalls += 1; },
  };
  return { transport, succeed: () => resolve(true) };
}

async function pathExists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

describe("AI control authentication", () => {
  it("fails closed with 503 when control or vault configuration is absent", async () => {
    const handler = createControlHttpHandler(createControlPlane({}));
    const response = await httpRequest(handler, { key: "anything" });
    assert.equal(response.handled, true);
    assert.equal(response.status, 503);
    assert.deepEqual(response.json, { error: "control_unavailable" });
    assert.equal(response.headers["cache-control"], "no-store");
  });

  it("protects every control route and only accepts the configured header", async () => {
    const { directory, vault, runtime } = await fixture();
    const shortKey = createControlHttpHandler(createControlPlane({
      RB_AI_CONTROL_KEY: "too-short",
      RB_AI_CONFIG_DIR: directory,
    }, { vault, runtime }));
    assert.equal((await httpRequest(shortKey, { key: "too-short" })).status, 503);
    const plane = createControlPlane({
      RB_AI_CONTROL_KEY: CONTROL_KEY,
      RB_AI_CONFIG_DIR: directory,
    }, { vault, runtime });
    const handler = createControlHttpHandler(plane);

    for (const key of [undefined, "", "wrong-control-key"]) {
      const response = await httpRequest(handler, {
        method: "POST",
        url: "/control/pools",
        ...(key === undefined ? {} : { key }),
        body: JSON.stringify({ provider: "claude-agent-sdk", label: "Blocked" }),
      });
      assert.equal(response.status, 401);
      assert.deepEqual(response.json, { error: "unauthorized" });
    }

    assert.equal(plane.authorize([CONTROL_KEY, "ignored-duplicate"]), true);
    const allowed = await httpRequest(handler, { key: CONTROL_KEY });
    assert.equal(allowed.status, 200);
    assert.match(allowed.headers["content-type"] as string, /^application\/json/);
    assert.equal(allowed.headers["x-content-type-options"], "nosniff");

    const unrelated = await httpRequest(handler, { url: "/health" });
    assert.equal(unrelated.handled, false);
  });

  it("returns a generic 503 when an encrypted vault cannot be opened", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-control-broken-vault-"));
    const vault = new ManagedVault({ directory, key: randomBytes(32) });
    await vault.save({ version: 1, revision: 0, pools: [] });
    await writeFile(vault.path, "not-an-envelope", { mode: 0o600 });
    const runtime = new RuntimeGenerationManager({
      source: {},
      configDirectory: directory,
      claudeAccountsFromEnvironment: () => [],
      codexAccountsFromEnvironment: () => [],
    });
    const handler = createControlHttpHandler(createControlPlane({
      RB_AI_CONTROL_KEY: CONTROL_KEY,
      RB_AI_CONFIG_DIR: directory,
    }, { vault, runtime }));
    const response = await httpRequest(handler, { key: CONTROL_KEY });
    assert.equal(response.status, 503);
    assert.deepEqual(response.json, { error: "control_unavailable" });
    assert.doesNotMatch(response.text, /envelope|cipher|vault|key/i);
  });
});

describe("managed AI control CRUD", () => {
  it("classifies the costless Anthropic API-key auth probe without leaking transport detail", async () => {
    const response = (status: number) => (async () => ({ status }) as Response) as typeof fetch;
    assert.equal(await new AnthropicApiKeyProbe(response(200)).test(MANAGED_SECRET), "ready");
    assert.equal(await new AnthropicApiKeyProbe(response(401)).test(MANAGED_SECRET), "auth_invalid");
    const authenticationFailure = (async () => {
      throw new Error("authentication_failed private upstream detail");
    }) as typeof fetch;
    assert.equal(
      await new AnthropicApiKeyProbe(authenticationFailure).test(MANAGED_SECRET),
      "auth_invalid",
    );
    const transportFailure = (async () => {
      throw new Error("ECONNRESET private upstream detail");
    }) as typeof fetch;
    assert.equal(await new AnthropicApiKeyProbe(transportFailure).test(MANAGED_SECRET), "unknown");
  });

  it("validates provider/auth combinations and never returns write-only credentials", async () => {
    const tester: ManagedAccountTester = { test: async () => "auth_invalid" };
    const { directory, vault, runtime, service } = await fixture({ tester });
    const pool = await service.createPool({
      provider: "claude-agent-sdk",
      label: "Primary Claude",
      priority: 12,
      weight: 3,
    });
    assert.equal(pool.source, "managed");
    assert.equal(pool.editable, true);
    assert.equal(pool.priority, 12);
    assert.equal(pool.weight, 3);

    await assert.rejects(
      service.createAccount({
        poolId: pool.id,
        label: "Wrong provider auth",
        authType: "access-token",
        credential: MANAGED_SECRET,
      }),
      isControlError("invalid_request"),
    );
    await assert.rejects(
      service.createAccount({
        poolId: pool.id,
        label: "Too short to redact safely",
        authType: "oauth-token",
        credential: "short",
      }),
      isControlError("invalid_request"),
    );
    await assert.rejects(
      service.createAccount({
        poolId: pool.id,
        label: "Unexpected field",
        authType: "oauth-token",
        credential: MANAGED_SECRET,
        externalAccountEmail: "never-accept@example.test",
      }),
      isControlError("invalid_request"),
    );

    const account = await service.createAccount({
      poolId: pool.id,
      label: "Operator-owned label",
      authType: "oauth-token",
      credential: MANAGED_SECRET,
    });
    assert.equal(account.credentialConfigured, true);
    assert.equal(account.status, "unknown");
    assert.equal("secret" in account, false);
    assert.equal("homeName" in account, false);

    const rotated = await service.putCredential(account.id, {
      credential: `${MANAGED_SECRET}-rotated`,
    });
    assert.equal(rotated.credentialConfigured, true);
    assert.equal("credential" in rotated, false);
    assert.equal("secret" in rotated, false);

    const result = await service.testAccount(account.id);
    assert.equal(result.accountId, account.id);
    assert.equal(result.status, "auth_invalid");
    assert.ok(Date.parse(result.lastTestedAt) > 0);

    const snapshot = await service.get();
    const publicAccount = snapshot.accounts.find((item) => item.id === account.id);
    assert.equal(publicAccount?.status, "auth_invalid");
    const provider = snapshot.providers.find((item) => item.id === "claude-agent-sdk");
    assert.equal(provider?.configuredAccounts, 1);
    assert.equal(provider?.availableAccounts, 0);
    assert.equal(provider?.status, "unavailable");
    assert.equal(snapshot.pools.find((item) => item.id === pool.id)?.availableAccounts, 0);
    assert.equal(snapshot.pools.find((item) => item.id === pool.id)?.status, "auth_invalid");
    assert.equal(runtime.currentGeneration().claudeRouter.acquire("claude-sonnet-4-6"), null);
    const serialized = JSON.stringify(snapshot);
    assert.doesNotMatch(serialized, /managed-control-secret|externalAccountEmail|example\.test/);
    const rawVault = await readFile(vault.path, "utf8");
    assert.doesNotMatch(rawVault, /managed-control-secret|Operator-owned label|Primary Claude/);
    const stored = await vault.load();
    assert.equal(stored.pools[0]?.accounts[0]?.secret, `${MANAGED_SECRET}-rotated`);

    const reset = await service.putCredential(account.id, {
      credential: `${MANAGED_SECRET}-recovered`,
    });
    assert.equal(reset.status, "unknown");
    const recoveredLease = runtime.currentGeneration().claudeRouter.acquire("claude-sonnet-4-6");
    assert.ok(recoveredLease);
    recoveredLease.release();

    await service.patchPool(pool.id, { enabled: false });
    assert.equal((await service.get()).accounts.find((item) =>
      item.id === account.id)?.status, "disabled");
    await service.patchPool(pool.id, { enabled: true });
    const disabled = await service.patchAccount(account.id, { enabled: false });
    assert.equal(disabled.status, "disabled");
    assert.equal((await stat(directory)).mode & 0o777, 0o700);
    await service.deleteAccount(account.id);
    await service.deletePool(pool.id);
    assert.equal((await service.get()).pools.length, 0);
  });

  it("creates opaque 0700 Codex homes owned by the server", async () => {
    const { directory, vault, service } = await fixture();
    const pool = await service.createPool({ provider: "codex-sdk", label: "Codex operators" });
    const account = await service.createAccount({
      poolId: pool.id,
      label: "Access token slot",
      authType: "access-token",
      credential: MANAGED_SECRET,
    });
    const stored = await vault.load();
    const internal = stored.pools[0]?.accounts[0];
    assert.match(internal?.homeName ?? "", /^[0-9a-f-]{36}$/i);
    assert.notEqual(internal?.homeName, account.id);
    const home = join(directory, "accounts", "codex", internal?.homeName ?? "missing");
    assert.equal((await stat(home)).mode & 0o777, 0o700);
    assert.equal("homeName" in account, false);
    assert.equal(JSON.stringify(account).includes(MANAGED_SECRET), false);
  });

  it("does not count enabled accounts without credentials as available", async () => {
    const { service } = await fixture();
    const pool = await service.createPool({ provider: "claude-agent-sdk", label: "Empty slot" });
    const account = await service.createAccount({
      poolId: pool.id,
      label: "Credential pending",
      authType: "oauth-token",
    });
    const snapshot = await service.get();
    assert.equal(account.credentialConfigured, false);
    assert.equal(snapshot.pools.find((item) => item.id === pool.id)?.availableAccounts, 0);
    const provider = snapshot.providers.find((item) => item.id === "claude-agent-sdk");
    assert.equal(provider?.configuredAccounts, 0);
    assert.equal(provider?.availableAccounts, 0);
    assert.equal(provider?.status, "unconfigured");
  });

  it("clears a persisted disabled test status when an account is re-enabled", async () => {
    const tester: ManagedAccountTester = {
      test: async ({ account }) => account.enabled ? "ready" : "disabled",
    };
    const { runtime, service } = await fixture({ tester });
    const pool = await service.createPool({ provider: "claude-agent-sdk", label: "Toggle" });
    const account = await service.createAccount({
      poolId: pool.id,
      label: "Toggle account",
      authType: "oauth-token",
      credential: MANAGED_SECRET,
      enabled: false,
    });
    assert.equal((await service.testAccount(account.id)).status, "disabled");
    assert.equal((await service.patchAccount(account.id, { enabled: true })).status, "unknown");
    const lease = runtime.currentGeneration().claudeRouter.acquire("claude-sonnet-4-6");
    assert.ok(lease);
    lease.release();
  });

  it("rejects oversized bodies and malformed JSON without echoing input", async () => {
    const { directory, vault, runtime } = await fixture();
    const handler = createControlHttpHandler(createControlPlane({
      RB_AI_CONTROL_KEY: CONTROL_KEY,
      RB_AI_CONFIG_DIR: directory,
    }, { vault, runtime }));
    const malformed = await httpRequest(handler, {
      method: "POST",
      url: "/control/pools",
      key: CONTROL_KEY,
      body: `{\"secret\":\"${MANAGED_SECRET}\"`,
    });
    assert.equal(malformed.status, 400);
    assert.deepEqual(malformed.json, { error: "invalid_request" });
    assert.doesNotMatch(malformed.text, /managed-control-secret/);

    const oversized = await httpRequest(handler, {
      method: "POST",
      url: "/control/pools",
      key: CONTROL_KEY,
      body: JSON.stringify({ label: "x".repeat(70_000), credential: MANAGED_SECRET }),
    });
    assert.equal(oversized.status, 400);
    assert.deepEqual(oversized.json, { error: "invalid_request" });
    assert.doesNotMatch(oversized.text, /managed-control-secret/);
  });
});

describe("runtime generations and environment bootstrap", () => {
  it("keeps environment accounts read-only while managed topology is added", async () => {
    const { service } = await fixture({ environment: true });
    const initial = await service.get();
    assert.deepEqual(initial.pools.map((pool) => ({
      id: pool.id,
      source: pool.source,
      editable: pool.editable,
    })), [{ id: "environment-claude", source: "environment", editable: false }]);
    assert.equal(initial.accounts[0]?.id, "environment-claude-1");
    assert.equal(initial.accounts[0]?.label, "Environment account 1");
    assert.equal(initial.accounts[0]?.editable, false);
    assert.equal(initial.accounts[0]?.credentialConfigured, true);
    assert.equal(initial.providers.find((provider) => provider.id === "claude-agent-sdk")?.configured, true);
    assert.ok(initial.models.some((model) =>
      model.alias === "sonnet" && model.provider === "claude-agent-sdk"));
    assert.doesNotMatch(JSON.stringify(initial), /environment-bootstrap-secret/);

    const managed = await service.createPool({
      provider: "claude-agent-sdk",
      label: "Managed fallback",
      priority: 5,
    });
    const after = await service.get();
    assert.ok(after.pools.some((pool) => pool.id === "environment-claude"));
    assert.ok(after.pools.some((pool) => pool.id === managed.id && pool.source === "managed"));
    await assert.rejects(service.patchPool("environment-claude", { enabled: false }),
      isControlError("not_found"));
  });

  it("hot-swaps atomically and drains an old leased generation", async () => {
    const { runtime, service } = await fixture();
    const old = runtime.lease();
    const oldId = old.generation.id;
    runtime.onTopologyChange(() => {
      throw new Error("listener failure must not block retirement");
    });

    await service.createPool({ provider: "claude-agent-sdk", label: "New generation" });
    assert.notEqual(runtime.currentGeneration().id, oldId);
    assert.equal(runtime.retiredGenerationCount(), 1);
    assert.equal(old.generation.activeLeases(), 1);

    const current = runtime.lease();
    assert.equal(current.generation.id, runtime.currentGeneration().id);
    assert.notEqual(current.generation.id, oldId);
    current.release();
    old.release();
    await waitImmediate();
    assert.equal(runtime.retiredGenerationCount(), 0);
  });

  it("suppresses a duplicate env route during zero-downtime managed migration", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-control-migration-"));
    const shared = "shared-env-managed-migration-credential";
    const vault = new ManagedVault({ directory, key: randomBytes(32) });
    const runtime = new RuntimeGenerationManager({
      source: {},
      configDirectory: directory,
      claudeAccountsFromEnvironment: () => [environmentAccount(shared)],
      codexAccountsFromEnvironment: () => [],
    });
    const service = new AiControlService(vault, { runtime, source: {}, configDirectory: directory });
    await service.ready();
    const pool = await service.createPool({
      provider: "claude-agent-sdk",
      label: "Managed migration target",
    });
    const managed = await service.createAccount({
      poolId: pool.id,
      label: "Migrated operator",
      authType: "oauth-token",
      credential: shared,
    });
    const snapshot = await service.get();
    assert.equal(snapshot.accounts.some((account) => account.id === "environment-claude-1"), false);
    assert.equal(snapshot.pools.some((item) => item.id === "environment-claude"), false);
    assert.equal(snapshot.accounts.some((account) => account.id === managed.id), true);
    assert.equal(snapshot.providers.find((provider) =>
      provider.id === "claude-agent-sdk")?.configuredAccounts, 1);

    await assert.rejects(service.createAccount({
      poolId: pool.id,
      label: "Duplicate managed owner",
      authType: "oauth-token",
      credential: shared,
    }), isControlError("conflict"));
    assert.equal((await service.get()).accounts.length, 1);
  });

  it("redacts active managed credentials and unregisters them on generation disposal", () => {
    const secret = "vault-runtime-secret-325-exact-value";
    const release = registerRuntimeSecrets([secret]);
    assert.equal(safeDetail(`upstream included ${secret}`), "upstream included [redacted]");
    release();
    // The generic high-entropy guard may still redact some secret formats, so
    // use a prose-like credential here to prove runtime registration ended.
    const proseSecret = "runtime secret phrase with spaces";
    const releaseProse = registerRuntimeSecrets([proseSecret]);
    assert.equal(safeDetail(`detail: ${proseSecret}`), "detail: [redacted]");
    releaseProse();
    assert.equal(safeDetail(`detail: ${proseSecret}`), `detail: ${proseSecret}`);
  });
});

describe("managed Codex device-login lifecycle", () => {
  it("persists successful authorization without exposing upstream identity", async () => {
    const login = deferredDeviceTransport("LOGIN-325");
    const factory: DeviceLoginTransportFactory = { start: async () => login.transport };
    const manager = new CodexDeviceLoginManager(factory);
    const { vault, service } = await fixture({ deviceLogins: manager });
    const pool = await service.createPool({ provider: "codex-sdk", label: "Codex device" });
    const account = await service.createAccount({
      poolId: pool.id,
      label: "Device slot",
      authType: "chatgpt-device",
    });
    assert.equal(account.credentialConfigured, false);

    const started = await service.startDeviceLogin(account.id);
    assert.equal(started.verificationUri, "https://auth.openai.com/device");
    assert.equal(started.userCode, "LOGIN-325");
    assert.doesNotMatch(JSON.stringify(started), /private-upstream|Device slot/);
    login.succeed();
    for (let index = 0; index < 50; index += 1) {
      if ((await service.deviceLoginStatus(started.sessionId)).status === "complete") break;
      await wait(5);
    }
    assert.deepEqual(await service.deviceLoginStatus(started.sessionId), { status: "complete" });
    const current = (await service.get()).accounts.find((item) => item.id === account.id);
    assert.equal(current?.credentialConfigured, true);
    assert.equal(current?.status, "ready");
    const stored = await vault.load();
    assert.equal(stored.pools[0]?.accounts[0]?.deviceAuthorized, true);
    assert.equal(stored.pools[0]?.accounts[0]?.secret, undefined);
  });

  it("cancels pending account/pool sessions and removes only UUID homes after drain", async () => {
    const first = deferredDeviceTransport("FIRST-325");
    const second = deferredDeviceTransport("SECOND-325");
    let started = 0;
    const factory: DeviceLoginTransportFactory = {
      start: async () => [first.transport, second.transport][started++] as DeviceLoginTransport,
    };
    const manager = new CodexDeviceLoginManager(factory);
    const { directory, vault, runtime, service } = await fixture({ deviceLogins: manager });
    const pool = await service.createPool({ provider: "codex-sdk", label: "Disposable pool" });
    const accountA = await service.createAccount({
      poolId: pool.id,
      label: "Device A",
      authType: "chatgpt-device",
    });
    const accountB = await service.createAccount({
      poolId: pool.id,
      label: "Device B",
      authType: "chatgpt-device",
    });
    const stored = await vault.load();
    const internalA = stored.pools[0]?.accounts.find((item) => item.id === accountA.id);
    const internalB = stored.pools[0]?.accounts.find((item) => item.id === accountB.id);
    const root = join(directory, "accounts", "codex");
    const homeA = join(root, internalA?.homeName ?? "missing-a");
    const homeB = join(root, internalB?.homeName ?? "missing-b");
    const unrelated = join(root, "keep-non-uuid-directory");
    await mkdir(unrelated, { recursive: true });
    await writeFile(join(homeA, "auth.json"), "plaintext account state", { mode: 0o600 });
    await writeFile(join(homeB, "auth.json"), "plaintext account state", { mode: 0o600 });
    const sessionA = await service.startDeviceLogin(accountA.id);
    const sessionB = await service.startDeviceLogin(accountB.id);

    const old = runtime.lease();
    await service.deleteAccount(accountA.id);
    assert.equal(first.transport.cancelCalls, 1);
    assert.equal(await pathExists(homeA), true, "old lease still owns account A home");
    await service.deletePool(pool.id);
    assert.equal(second.transport.cancelCalls, 1);
    assert.equal(manager.status(sessionA.sessionId), null);
    assert.equal(manager.status(sessionB.sessionId), null);
    assert.equal(await pathExists(unrelated), true);

    old.release();
    for (let index = 0; index < 50; index += 1) {
      if (!await pathExists(homeA) && !await pathExists(homeB)) break;
      await wait(5);
    }
    assert.equal(await pathExists(homeA), false);
    assert.equal(await pathExists(homeB), false);
    assert.equal(await pathExists(unrelated), true, "cleanup is restricted to server UUID homes");
  });
});

describe("managed account-home lifecycle", () => {
  it("removes a newly created Claude home when vault persistence fails", async () => {
    class ToggleVault extends ManagedVault {
      fail = false;
      override async save(document: Parameters<ManagedVault["save"]>[0]): Promise<void> {
        if (this.fail) throw new Error("simulated vault failure");
        await super.save(document);
      }
    }
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-home-failed-create-"));
    const vault = new ToggleVault({ directory, key: randomBytes(32) });
    const runtime = new RuntimeGenerationManager({
      source: {},
      configDirectory: directory,
      claudeAccountsFromEnvironment: () => [],
      codexAccountsFromEnvironment: () => [],
    });
    const service = new AiControlService(vault, { runtime, source: {}, configDirectory: directory });
    await service.ready();
    const pool = await service.createPool({ provider: "claude-agent-sdk", label: "Failure cleanup" });
    const root = join(directory, "accounts", "claude");
    const before = await readdir(root);
    vault.fail = true;
    await assert.rejects(service.createAccount({
      poolId: pool.id,
      label: "Must roll back",
      authType: "oauth-token",
      credential: MANAGED_SECRET,
    }), isControlError("control_unavailable"));
    assert.deepEqual(await readdir(root), before);
  });

  it("removes a Claude UUID home only after its old generation drains", async () => {
    const { directory, runtime, service } = await fixture();
    const pool = await service.createPool({ provider: "claude-agent-sdk", label: "Claude homes" });
    const account = await service.createAccount({
      poolId: pool.id,
      label: "Claude home",
      authType: "oauth-token",
      credential: MANAGED_SECRET,
    });
    const root = join(directory, "accounts", "claude");
    const home = join(root, account.id);
    const unrelated = join(root, "keep-non-uuid-directory");
    await mkdir(unrelated, { recursive: true });
    await writeFile(join(home, "marker"), "managed state", { mode: 0o600 });
    assert.equal((await stat(home)).mode & 0o777, 0o700);
    const old = runtime.lease();
    await service.deleteAccount(account.id);
    assert.equal(await pathExists(home), true);
    old.release();
    for (let index = 0; index < 50; index += 1) {
      if (!await pathExists(home)) break;
      await wait(5);
    }
    assert.equal(await pathExists(home), false);
    assert.equal(await pathExists(unrelated), true);
  });

  it("reconciles crash-orphan UUID homes but preserves configured and non-UUID paths", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-home-reconcile-"));
    const claudeRoot = join(directory, "accounts", "claude");
    const codexRoot = join(directory, "accounts", "codex");
    const orphanClaude = join(claudeRoot, "11111111-1111-4111-8111-111111111111");
    const orphanCodex = join(codexRoot, "22222222-2222-4222-8222-222222222222");
    const configuredCodex = join(codexRoot, "33333333-3333-4333-8333-333333333333");
    const unrelatedClaude = join(claudeRoot, "operator-notes");
    await Promise.all([
      mkdir(orphanClaude, { recursive: true }),
      mkdir(orphanCodex, { recursive: true }),
      mkdir(configuredCodex, { recursive: true }),
      mkdir(unrelatedClaude, { recursive: true }),
    ]);
    const source = { CODEX_HOME: configuredCodex };
    const vault = new ManagedVault({ directory, key: randomBytes(32) });
    const runtime = new RuntimeGenerationManager({ source, configDirectory: directory });
    const service = new AiControlService(vault, { runtime, source, configDirectory: directory });
    await service.ready();
    assert.equal(await pathExists(orphanClaude), false);
    assert.equal(await pathExists(orphanCodex), false);
    assert.equal(await pathExists(configuredCodex), true);
    assert.equal(await pathExists(unrelatedClaude), true);
    assert.equal((await service.get()).accounts.some((item) =>
      item.id.startsWith("environment-codex-")), true);
    await runtime.currentGeneration().dispose();
  });
});
