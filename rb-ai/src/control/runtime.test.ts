import assert from "node:assert/strict";
import { randomBytes, randomUUID } from "node:crypto";
import { access, mkdir, mkdtemp, stat } from "node:fs/promises";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setTimeout as wait } from "node:timers/promises";
import { describe, it } from "node:test";
import { z } from "zod";
import type { ToolProviderRequest } from "../providers/types.js";
import { RuntimeGenerationManager } from "./runtime.js";
import { AiControlService, ControlError } from "./service.js";
import type { VaultDocument } from "./types.js";
import { ManagedVault } from "./vault.js";

const TOKEN = "runtime-owned-temporary-token-325";

async function exists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

async function eventuallyRemoved(paths: readonly string[]): Promise<void> {
  for (let attempt = 0; attempt < 60; attempt += 1) {
    if ((await Promise.all(paths.map(exists))).every((present) => !present)) return;
    await wait(5);
  }
  assert.deepEqual(await Promise.all(paths.map(exists)), paths.map(() => false));
}

function tracker() {
  const paths: string[] = [];
  return {
    paths,
    make: (prefix: string) => {
      const path = mkdtempSync(join(tmpdir(), prefix));
      paths.push(path);
      return path;
    },
  };
}

function codexDocument(): VaultDocument {
  return {
    version: 1,
    revision: 1,
    pools: [{
      id: randomUUID(),
      provider: "codex-sdk",
      label: "Codex",
      enabled: true,
      priority: 0,
      weight: 1,
      accounts: [{
        id: randomUUID(),
        label: "Codex account",
        authType: "access-token",
        enabled: true,
        secret: `${TOKEN}-managed`,
        homeName: randomUUID(),
      }],
    }],
  };
}

function request(): ToolProviderRequest {
  return {
    modelId: "gpt-5.6-sol",
    systemPrompt: "test",
    prompt: "public input",
    tool: {
      name: "emit_items",
      description: "Emit items",
      schema: { items: z.array(z.string()) },
    },
    signal: new AbortController().signal,
    onToolCall: () => true,
  };
}

describe("runtime generation resource ownership", () => {
  it("keeps current env tempdirs, then removes only the old generation after lease drain", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-runtime-owner-"));
    const tracked = tracker();
    const runtime = new RuntimeGenerationManager({
      source: { CLAUDE_CODE_OAUTH_TOKEN: TOKEN },
      configDirectory: directory,
      temporaryDirectory: tracked.make,
    });
    const oldHome = runtime.currentGeneration().claudeRouter.singleEnvironment()?.HOME;
    assert.ok(oldHome);
    const oldPaths = [...tracked.paths];
    const old = runtime.lease();
    await runtime.replace({ version: 1, revision: 0, pools: [] });
    const currentPaths = tracked.paths.filter((path) => !oldPaths.includes(path));
    assert.ok(currentPaths.length >= 2);
    assert.ok((await Promise.all([...oldPaths, ...currentPaths].map(exists))).every(Boolean));

    old.release();
    await eventuallyRemoved(oldPaths);
    assert.ok((await Promise.all(currentPaths.map(exists))).every(Boolean));
    await runtime.currentGeneration().dispose();
    await eventuallyRemoved(currentPaths);
  });

  it("never owns or removes a configured CODEX_HOME or unrelated directory", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-runtime-configured-"));
    const configuredHome = await mkdtemp(join(tmpdir(), "rb-ai-configured-codex-home-"));
    const unrelated = await mkdtemp(join(tmpdir(), "rb-ai-unrelated-home-"));
    const tracked = tracker();
    const runtime = new RuntimeGenerationManager({
      source: { CODEX_HOME: configuredHome },
      configDirectory: directory,
      temporaryDirectory: tracked.make,
    });
    const oldPaths = [...tracked.paths];
    await runtime.replace({ version: 1, revision: 0, pools: [] });
    await eventuallyRemoved(oldPaths);
    assert.equal(await exists(configuredHome), true);
    assert.equal(await exists(unrelated), true);
    await runtime.currentGeneration().dispose();
    assert.equal(await exists(configuredHome), true);
    assert.equal(await exists(unrelated), true);
  });

  it("awaits cleanup of candidate tempdirs after prepare and vault-save failures", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-runtime-failure-"));
    const tracked = tracker();
    const runtime = new RuntimeGenerationManager({
      source: { CLAUDE_CODE_OAUTH_TOKEN: TOKEN },
      configDirectory: directory,
      temporaryDirectory: tracked.make,
      codexAccountFactory: () => { throw new Error("candidate construction failed"); },
    });
    const currentPaths = [...tracked.paths];
    const beforePrepare = tracked.paths.length;
    await assert.rejects(runtime.prepare(codexDocument()), /candidate construction failed/);
    const failedPreparePaths = tracked.paths.slice(beforePrepare);
    assert.ok(failedPreparePaths.length >= 2);
    assert.ok((await Promise.all(failedPreparePaths.map(exists))).every((present) => !present));
    assert.ok((await Promise.all(currentPaths.map(exists))).every(Boolean));

    class SaveFailingVault extends ManagedVault {
      override async save(): Promise<void> {
        throw new Error("simulated persistence failure");
      }
    }
    const saveDirectory = await mkdtemp(join(tmpdir(), "rb-ai-runtime-save-failure-"));
    const saveTracked = tracker();
    const saveRuntime = new RuntimeGenerationManager({
      source: { CLAUDE_CODE_OAUTH_TOKEN: `${TOKEN}-save` },
      configDirectory: saveDirectory,
      temporaryDirectory: saveTracked.make,
    });
    const vault = new SaveFailingVault({ directory: saveDirectory, key: randomBytes(32) });
    const service = new AiControlService(vault, {
      runtime: saveRuntime,
      source: { CLAUDE_CODE_OAUTH_TOKEN: `${TOKEN}-save` },
      configDirectory: saveDirectory,
    });
    await service.ready();
    await wait(10);
    const liveBeforeCommit: string[] = [];
    for (const path of saveTracked.paths)
      if (await exists(path)) liveBeforeCommit.push(path);
    const beforeCommit = saveTracked.paths.length;
    await assert.rejects(
      service.createPool({ provider: "claude-agent-sdk", label: "Will fail" }),
      (error: unknown) => error instanceof ControlError && error.code === "control_unavailable",
    );
    const failedCommitPaths = saveTracked.paths.slice(beforeCommit);
    assert.ok(failedCommitPaths.length >= 2);
    assert.ok((await Promise.all(failedCommitPaths.map(exists))).every((present) => !present));
    assert.ok((await Promise.all(liveBeforeCommit.map(exists))).every(Boolean));
    await runtime.currentGeneration().dispose();
    await saveRuntime.currentGeneration().dispose();
  });
});

describe("runtime snapshot consistency", () => {
  it("propagates proactive Codex quota unavailability through account, pool, and provider", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-runtime-quota-status-"));
    const vault = new ManagedVault({ directory, key: randomBytes(32) });
    const runtime = new RuntimeGenerationManager({
      source: {},
      configDirectory: directory,
      claudeAccountsFromEnvironment: () => [],
      codexAccountsFromEnvironment: () => [],
      codexAccountFactory: (environment) => ({
        environment,
        quotaReader: { read: async () => ({ usedPercent: 100, available: false }) },
        runner: { run: async () => assert.fail("quota-exhausted account must not run") },
      }),
    });
    const service = new AiControlService(vault, { runtime, source: {}, configDirectory: directory });
    await service.ready();
    const pool = await service.createPool({ provider: "codex-sdk", label: "Limited Codex" });
    const account = await service.createAccount({
      poolId: pool.id,
      label: "Limited account",
      authType: "access-token",
      credential: TOKEN,
    });
    assert.equal((await runtime.currentGeneration().codexProvider.invokeTool(request())).failure?.reason,
      "api_error");
    const snapshot = await service.get();
    assert.equal(snapshot.accounts.find((item) => item.id === account.id)?.status, "quota_exhausted");
    assert.equal(snapshot.pools.find((item) => item.id === pool.id)?.availableAccounts, 0);
    assert.equal(snapshot.pools.find((item) => item.id === pool.id)?.status, "quota_exhausted");
    const provider = snapshot.providers.find((item) => item.id === "codex-sdk");
    assert.equal(provider?.availableAccounts, 0);
    assert.equal(provider?.status, "unavailable");
    assert.equal((await stat(join(directory, "accounts", "codex"))).mode & 0o777, 0o700);
  });
});
