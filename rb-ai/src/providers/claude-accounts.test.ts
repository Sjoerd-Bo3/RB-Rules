import assert from "node:assert/strict";
import { setImmediate as waitImmediate } from "node:timers/promises";
import { describe, it } from "node:test";
import { z } from "zod";
import {
  ClaudeAccountPoolProvider,
  ClaudeAccountRouter,
  claudeQuotaScore,
  createClaudeAccountsFromEnvironment,
  discoverClaudeAccountEnvironments,
  NoopClaudeQuotaReader,
  claudeProbeFailureStatus,
  parseClaudeQuota,
  SdkClaudeQuotaReader,
  type ClaudeAccount,
  type ClaudeAccountEnvironment,
  type ClaudeQuotaSnapshot,
} from "./claude-accounts.js";
import {
  ClaudeAgentToolProvider,
  mergeClaudeMessageFailure,
  type ClaudeToolProviderResult,
} from "./claude-agent.js";
import type { ToolProviderRequest } from "./types.js";

function quota(utilization: number, key = "five_hour"): ClaudeQuotaSnapshot {
  return {
    planLimitsAvailable: true,
    windows: { [key]: { utilization, resetsAt: null } },
  };
}

function environment(name: string): ClaudeAccountEnvironment {
  return Object.freeze({
    CLAUDE_CODE_OAUTH_TOKEN: name,
    HOME: `/tmp/${name}`,
    CLAUDE_CONFIG_DIR: `/tmp/${name}`,
  });
}

function account(input: {
  name: string;
  read?: () => Promise<ClaudeQuotaSnapshot | null>;
}): ClaudeAccount {
  return {
    environment: environment(input.name),
    quotaReader: { read: input.read ?? (async () => null) },
  };
}

function toolRequest(signal: AbortSignal = new AbortController().signal): ToolProviderRequest {
  return {
    modelId: "claude-sonnet-4-6",
    systemPrompt: "system",
    prompt: "public card text",
    tool: {
      name: "emit_items",
      description: "Emit items",
      schema: { items: z.array(z.string()) },
    },
    signal,
    onToolCall: (name, input) =>
      name === "emit_items" && z.object({ items: z.array(z.string()) }).safeParse(input).success,
  };
}

describe("Claude account discovery", () => {
  it("distinguishes OAuth auth rejection from an unknown transport failure", () => {
    for (const error of [
      new Error("401 unauthorized"),
      new Error("403 forbidden"),
      new Error("authentication_failed"),
    ]) assert.equal(claudeProbeFailureStatus(error), "auth_invalid");
    assert.equal(claudeProbeFailureStatus(new Error("ECONNRESET")), "unknown");
    assert.equal(claudeProbeFailureStatus(new Error("probe timeout")), "unknown");
  });

  it("uses arbitrary numbered slots and isolates every selected credential", () => {
    const homes = ["/tmp/claude-home-2", "/tmp/claude-home-9"];
    const environments = discoverClaudeAccountEnvironments({
      CLAUDE_CODE_OAUTH_TOKEN: "ignored-fallback",
      ANTHROPIC_API_KEY_2: "api-two",
      CLAUDE_CODE_OAUTH_TOKEN_9: "oauth-nine",
      PATH: "/safe/bin",
      LANG: "nl_NL.UTF-8",
      DATABASE_URL: "must-not-reach-child",
      RB_API_URL: "also-not-a-child-secret",
    }, () => homes.shift() as string);

    assert.equal(environments.length, 2);
    assert.equal(environments[0]?.ANTHROPIC_API_KEY, "api-two");
    assert.equal("CLAUDE_CODE_OAUTH_TOKEN" in (environments[0] ?? {}), false);
    assert.equal(environments[1]?.CLAUDE_CODE_OAUTH_TOKEN, "oauth-nine");
    assert.equal("ANTHROPIC_API_KEY" in (environments[1] ?? {}), false);
    assert.deepEqual(environments.map((env) => env.HOME), [
      "/tmp/claude-home-2",
      "/tmp/claude-home-9",
    ]);
    for (const env of environments) {
      assert.equal(env.PATH, "/safe/bin");
      assert.equal(env.LANG, "nl_NL.UTF-8");
      assert.equal("DATABASE_URL" in env, false);
      assert.equal("RB_API_URL" in env, false);
      assert.equal("ANTHROPIC_API_KEY_2" in env, false);
      assert.equal("CLAUDE_CODE_OAUTH_TOKEN_9" in env, false);
    }
  });

  it("does not let empty compose placeholders suppress the unnumbered fallback", () => {
    const environments = discoverClaudeAccountEnvironments({
      CLAUDE_CODE_OAUTH_TOKEN: "fallback-oauth",
      CLAUDE_CODE_OAUTH_TOKEN_1: "",
      ANTHROPIC_API_KEY_1: "   ",
      CLAUDE_CODE_OAUTH_TOKEN_2: "",
    }, () => "/tmp/claude-fallback");
    assert.equal(environments.length, 1);
    assert.equal(environments[0]?.CLAUDE_CODE_OAUTH_TOKEN, "fallback-oauth");
  });

  it("rejects dual auth in one slot and duplicate credentials", () => {
    assert.throws(() => discoverClaudeAccountEnvironments({
      CLAUDE_CODE_OAUTH_TOKEN_4: "oauth",
      ANTHROPIC_API_KEY_4: "api",
    }), /niet tegelijk OAuth en API-key/i);
    assert.throws(() => discoverClaudeAccountEnvironments({
      CLAUDE_CODE_OAUTH_TOKEN_1: "same",
      CLAUDE_CODE_OAUTH_TOKEN_2: "same",
    }, (() => {
      let index = 0;
      return () => `/tmp/claude-unique-${index++}`;
    })()), /credentials.*uniek/i);
  });

  it("never boots plan-usage probes for API-key slots by default", () => {
    const apiAccounts = createClaudeAccountsFromEnvironment(
      { ANTHROPIC_API_KEY: "api-key" },
      {
        isolatedHome: () => "/tmp/claude-api-home",
        isolatedWorkingDirectory: () => "/tmp/claude-api-probe",
      },
    );
    const oauthAccounts = createClaudeAccountsFromEnvironment(
      { CLAUDE_CODE_OAUTH_TOKEN: "oauth-token" },
      {
        isolatedHome: () => "/tmp/claude-oauth-home",
        isolatedWorkingDirectory: () => "/tmp/claude-oauth-probe",
      },
    );
    assert.ok(apiAccounts[0]?.quotaReader instanceof NoopClaudeQuotaReader);
    assert.ok(oauthAccounts[0]?.quotaReader instanceof SdkClaudeQuotaReader);
  });
});

describe("Claude quota parsing and router", () => {
  it("scores the worst relevant global and model-specific window", () => {
    const parsed = parseClaudeQuota({
      rate_limits_available: true,
      rate_limits: {
        five_hour: { utilization: 21, resets_at: "2030-01-01T00:00:00Z" },
        seven_day: { utilization: 40, resets_at: null },
        seven_day_opus: { utilization: 83, resets_at: null },
        seven_day_sonnet: { utilization: 57, resets_at: null },
        model_scoped: [{ display_name: "Fable", utilization: 68, resets_at: null }],
      },
    });
    assert.ok(parsed);
    assert.equal(claudeQuotaScore(parsed, "claude-opus-4-8"), 83);
    assert.equal(claudeQuotaScore(parsed, "claude-sonnet-4-6"), 57);
    assert.equal(claudeQuotaScore(parsed, "claude-fable-5"), 68);
  });

  it("coalesces refreshes and probes arbitrary accounts sequentially", async () => {
    const reads = [0, 0, 0, 0];
    let activeReaders = 0;
    let maxActiveReaders = 0;
    const router = new ClaudeAccountRouter(reads.map((_, index) => account({
      name: `claude-${index}`,
      read: async () => {
        reads[index] += 1;
        activeReaders += 1;
        maxActiveReaders = Math.max(maxActiveReaders, activeReaders);
        await waitImmediate();
        activeReaders -= 1;
        return quota(10 + index);
      },
    })), () => 1_000);

    await Promise.all([
      router.refreshQuotas(),
      router.refreshQuotas(),
      router.refreshQuotas(),
    ]);
    await router.refreshQuotas();

    assert.deepEqual(reads, [1, 1, 1, 1]);
    assert.equal(maxActiveReaders, 1);
  });

  it("skips probes while a real call is in flight", async () => {
    const reads = [0, 0];
    const router = new ClaudeAccountRouter(reads.map((_, index) => account({
      name: `busy-${index}`,
      read: async () => {
        reads[index] += 1;
        return quota(10);
      },
    })));
    const lease = router.acquire("claude-sonnet-4-6");
    assert.ok(lease);

    await router.refreshQuotas();
    assert.deepEqual(reads, [0, 0]);
    lease.release();
    await router.refreshQuotas();
    assert.deepEqual(reads, [1, 1]);
  });

  it("routes by quota, then bounded in-flight load and round-robin", async () => {
    const quotaRouter = new ClaudeAccountRouter([
      account({ name: "used", read: async () => quota(88) }),
      account({ name: "room", read: async () => quota(12) }),
    ]);
    await quotaRouter.refreshQuotas();
    const roomy = quotaRouter.acquire("claude-sonnet-4-6");
    assert.equal(roomy?.ordinal, 1);
    roomy?.release();

    const balanced = new ClaudeAccountRouter([
      account({ name: "a" }),
      account({ name: "b" }),
    ]);
    const leases = [
      balanced.acquire("claude-sonnet-4-6"),
      balanced.acquire("claude-sonnet-4-6"),
      balanced.acquire("claude-sonnet-4-6"),
    ];
    assert.deepEqual(leases.map((lease) => lease?.ordinal), [0, 1, 0]);
    assert.equal(balanced.health().inFlight, 3);
    for (const lease of leases) lease?.release();
    assert.equal(balanced.health().inFlight, 0);
  });

  it("honors managed pool priority and smooth routing weight on equivalent accounts", () => {
    const routed = (
      name: string,
      priority: number,
      weight: number,
      poolId = `pool-${name}`,
    ): ClaudeAccount => ({
      ...account({ name }),
      route: { accountId: name, poolId, priority, weight },
    });
    const prioritized = new ClaudeAccountRouter([
      routed("low", 1, 100),
      routed("high", 10, 1),
    ]);
    const high = prioritized.acquire("claude-sonnet-4-6");
    assert.equal(high?.accountId, "high");
    high?.release();

    const weighted = new ClaudeAccountRouter([
      routed("weight-three", 5, 3),
      routed("weight-one", 5, 1),
    ]);
    const counts = new Map<string, number>();
    for (let index = 0; index < 8; index += 1) {
      const lease = weighted.acquire("claude-sonnet-4-6");
      assert.ok(lease?.accountId);
      counts.set(lease.accountId, (counts.get(lease.accountId) ?? 0) + 1);
      lease.release();
    }
    assert.deepEqual(Object.fromEntries(counts), { "weight-three": 6, "weight-one": 2 });

    const poolCounts = (weightA: number) => {
      const router = new ClaudeAccountRouter([
        routed("a-1", 5, weightA, "pool-a"),
        routed("a-2", 5, weightA, "pool-a"),
        routed("b-1", 5, 1, "pool-b"),
      ]);
      const selected: string[] = [];
      for (let index = 0; index < 8; index += 1) {
        const lease = router.acquire("claude-sonnet-4-6");
        assert.ok(lease?.poolId);
        selected.push(lease.poolId);
        lease.release();
      }
      return selected;
    };
    const equalPools = poolCounts(1);
    assert.equal(equalPools.filter((pool) => pool === "pool-a").length, 4);
    assert.equal(equalPools.filter((pool) => pool === "pool-b").length, 4);
    const weightedPools = poolCounts(3);
    assert.equal(weightedPools.filter((pool) => pool === "pool-a").length, 6);
    assert.equal(weightedPools.filter((pool) => pool === "pool-b").length, 2);
  });

  it("recovers auth and rate-limit cooldowns after their deadline", () => {
    let now = 1_800_000_000_000;
    const router = new ClaudeAccountRouter([
      { ...account({ name: "a" }), route: {
        accountId: "a", poolId: "pool-a", priority: 0, weight: 1,
      } },
      { ...account({ name: "b" }), route: {
        accountId: "b", poolId: "pool-b", priority: 0, weight: 1,
      } },
    ], () => now);
    const first = router.acquire("claude-sonnet-4-6");
    assert.equal(first?.ordinal, 0);
    if (!first) return;
    router.markAccountFailure(first, { reason: "auth", detail: "sanitized auth failure" });
    first.release();
    const second = router.acquire("claude-sonnet-4-6");
    assert.equal(second?.ordinal, 1);
    second?.release();

    now += 300_001;
    assert.equal(router.accountStatuses().find((item) => item.accountId === "a")?.status, "unknown");
    const recovered = router.acquire("claude-sonnet-4-6");
    assert.equal(recovered?.ordinal, 0);
    assert.equal(router.accountStatuses().find((item) => item.accountId === "a")?.status, "unknown");
    if (!recovered) return;
    router.markAccountFailure(
      recovered,
      { reason: "api_error", detail: "rate limit" },
      { status: "rejected", utilization: 100, resetsAt: now + 5_000 },
    );
    recovered.release();
    const alternate = router.acquire("claude-sonnet-4-6");
    assert.equal(alternate?.ordinal, 1);
    alternate?.release();
    // Account-local failures have a one-minute minimum cooldown even when an
    // event carries an earlier reset, avoiding an immediate retry storm.
    now += 60_001;
    assert.equal(router.accountStatuses().find((item) => item.accountId === "a")?.status, "unknown");
    const resetRecovered = router.acquire("claude-sonnet-4-6");
    assert.equal(resetRecovered?.ordinal, 0);
    resetRecovered?.release();
    assert.equal(router.accountStatuses().find((item) => item.accountId === "a")?.status, "unknown");
  });
});

describe("Claude provider failover", () => {
  it("preserves an auth signal over a later generic terminal result", () => {
    const auth = mergeClaudeMessageFailure(undefined, {
      type: "assistant",
      error: "authentication_failed",
    });
    const terminal = mergeClaudeMessageFailure(auth, {
      type: "result",
      subtype: "error_during_execution",
      is_error: true,
    });
    assert.equal(terminal?.reason, "auth");
  });

  it("checks request abort before trying the next account", async () => {
    const controller = new AbortController();
    let calls = 0;
    const router = new ClaudeAccountRouter([
      account({ name: "first" }),
      account({ name: "second" }),
    ]);
    const factory = (() => ({
      invokeTool: async (): Promise<ClaudeToolProviderResult> => {
        calls += 1;
        controller.abort();
        return {
          failure: { reason: "api_error", detail: "Claude-account heeft zijn rate limit bereikt" },
          usage: null,
          accountSignal: { status: "rejected", utilization: 100 },
        };
      },
    }) as unknown as ClaudeAgentToolProvider);
    const provider = new ClaudeAccountPoolProvider(router, factory);

    const outcome = await provider.invokeTool(toolRequest(controller.signal));
    assert.equal(outcome.failure?.reason, "aborted");
    assert.equal(calls, 1);
    assert.equal(router.health().inFlight, 0);
  });

  it("keeps a tool-call captured before failure instead of replaying it", async () => {
    let calls = 0;
    const router = new ClaudeAccountRouter([
      account({ name: "first" }),
      account({ name: "second" }),
    ]);
    const factory = (() => ({
      invokeTool: async (req: ToolProviderRequest): Promise<ClaudeToolProviderResult> => {
        calls += 1;
        req.onToolCall(req.tool.name, { items: ["partial"] });
        return {
          failure: { reason: "api_error", detail: "Claude-account heeft zijn rate limit bereikt" },
          usage: null,
          accountSignal: { status: "rejected", utilization: 100 },
        };
      },
    }) as unknown as ClaudeAgentToolProvider);
    const provider = new ClaudeAccountPoolProvider(router, factory);

    const outcome = await provider.invokeTool(toolRequest());
    assert.equal(outcome.failure?.reason, "api_error");
    assert.equal(calls, 1);
    assert.equal(router.health().inFlight, 0);
  });
});
