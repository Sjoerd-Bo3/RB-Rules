import assert from "node:assert/strict";
import { setImmediate as waitImmediate } from "node:timers/promises";
import { describe, it } from "node:test";
import { z } from "zod";
import {
  CodexAccountPoolProvider,
  discoverCodexAccountEnvironments,
  parseCodexQuota,
  type CodexAccount,
  type CodexQuota,
} from "./codex.js";
import type { ToolProviderRequest } from "./types.js";

const success = { finalResponse: JSON.stringify({ items: ["ok"] }), usage: null };

function request(signal: AbortSignal = new AbortController().signal): ToolProviderRequest {
  return {
    modelId: "gpt-5.6-sol",
    systemPrompt: "emit structured test data",
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

function account(input: {
  quota?: CodexQuota | null;
  read?: () => Promise<CodexQuota | null>;
  run?: CodexAccount["runner"]["run"];
} = {}): CodexAccount {
  return {
    quotaReader: { read: input.read ?? (async () => input.quota ?? null) },
    runner: { run: input.run ?? (async () => success) },
  };
}

describe("Codex account discovery", () => {
  it("uses arbitrary numbered slots and passes only the selected token/home", () => {
    const generated = ["/tmp/generated-codex-7"];
    const environments = discoverCodexAccountEnvironments(
      {
        CODEX_ACCESS_TOKEN: "ignored-fallback",
        CODEX_ACCESS_TOKEN_2: "slot-two",
        CODEX_HOME_2: "/tmp/codex-two",
        CODEX_ACCESS_TOKEN_7: "slot-seven",
        DATABASE_URL: "must-not-reach-child",
      },
      () => generated.shift() as string,
    );

    assert.deepEqual(environments, [
      { CODEX_HOME: "/tmp/codex-two", CODEX_ACCESS_TOKEN: "slot-two" },
      { CODEX_HOME: "/tmp/generated-codex-7", CODEX_ACCESS_TOKEN: "slot-seven" },
    ]);
    for (const environment of environments) {
      assert.deepEqual(Object.keys(environment).sort(), ["CODEX_ACCESS_TOKEN", "CODEX_HOME"]);
      assert.equal("DATABASE_URL" in environment, false);
      assert.equal("CODEX_ACCESS_TOKEN_2" in environment, false);
    }
  });

  it("does not let empty compose placeholders suppress the unnumbered fallback", () => {
    const environments = discoverCodexAccountEnvironments(
      {
        CODEX_ACCESS_TOKEN: "fallback-token",
        CODEX_ACCESS_TOKEN_1: "",
        CODEX_HOME_1: "   ",
        CODEX_ACCESS_TOKEN_2: "",
      },
      () => "/tmp/codex-fallback",
    );
    assert.deepEqual(environments, [{
      CODEX_HOME: "/tmp/codex-fallback",
      CODEX_ACCESS_TOKEN: "fallback-token",
    }]);
  });

  it("rejects shared credentials or state roots", () => {
    assert.throws(() => discoverCodexAccountEnvironments({
      CODEX_ACCESS_TOKEN_1: "same",
      CODEX_ACCESS_TOKEN_2: "same",
    }, (() => {
      let index = 0;
      return () => `/tmp/codex-unique-${index++}`;
    })()), /tokens.*uniek/i);
    assert.throws(() => discoverCodexAccountEnvironments({
      CODEX_HOME_1: "/tmp/codex-shared",
      CODEX_HOME_2: "/tmp/codex-shared",
    }), /accountmappen.*uniek/i);
  });
});

describe("Codex quota and routing", () => {
  it("parses the worst aggregate window without retaining account fields", () => {
    assert.deepEqual(parseCodexQuota({
      account: { email: "never-retain@example.test" },
      rateLimits: {
        primary: { usedPercent: 24 },
        secondary: { usedPercent: 71 },
        rateLimitReachedType: null,
      },
    }), { usedPercent: 71, available: true });
    assert.deepEqual(parseCodexQuota({
      rateLimits: { rateLimitReachedType: "primary" },
    }), { usedPercent: null, available: false });
  });

  it("routes to the account with the most quota room", async () => {
    const calls: string[] = [];
    const provider = new CodexAccountPoolProvider({
      accounts: [
        account({ quota: { usedPercent: 91, available: true }, run: async () => {
          calls.push("busy-quota");
          return success;
        } }),
        account({ quota: { usedPercent: 18, available: true }, run: async () => {
          calls.push("roomy");
          return success;
        } }),
      ],
    });

    assert.equal((await provider.invokeTool(request())).failure, undefined);
    assert.deepEqual(calls, ["roomy"]);
  });

  it("honors managed pool priority and smooth routing weight on equivalent accounts", async () => {
    const routed = (
      name: string,
      priority: number,
      weight: number,
      calls: string[],
      poolId = `pool-${name}`,
    ): CodexAccount => ({
      ...account({ run: async () => {
        calls.push(name);
        return success;
      } }),
      route: { accountId: name, poolId, priority, weight },
    });
    const priorityCalls: string[] = [];
    const prioritized = new CodexAccountPoolProvider({ accounts: [
      routed("low", 1, 100, priorityCalls),
      routed("high", 10, 1, priorityCalls),
    ] });
    assert.equal((await prioritized.invokeTool(request())).failure, undefined);
    assert.deepEqual(priorityCalls, ["high"]);

    const weightedCalls: string[] = [];
    const weighted = new CodexAccountPoolProvider({ accounts: [
      routed("weight-three", 5, 3, weightedCalls),
      routed("weight-one", 5, 1, weightedCalls),
    ] });
    for (let index = 0; index < 8; index += 1)
      assert.equal((await weighted.invokeTool(request())).failure, undefined);
    assert.equal(weightedCalls.filter((name) => name === "weight-three").length, 6);
    assert.equal(weightedCalls.filter((name) => name === "weight-one").length, 2);

    const poolCounts = async (weightA: number) => {
      const calls: string[] = [];
      const provider = new CodexAccountPoolProvider({ accounts: [
        routed("a-1", 5, weightA, calls, "pool-a"),
        routed("a-2", 5, weightA, calls, "pool-a"),
        routed("b-1", 5, 1, calls, "pool-b"),
      ] });
      for (let index = 0; index < 8; index += 1)
        assert.equal((await provider.invokeTool(request())).failure, undefined);
      return calls;
    };
    const equalPools = await poolCounts(1);
    assert.equal(equalPools.filter((name) => name.startsWith("a-")).length, 4);
    assert.equal(equalPools.filter((name) => name.startsWith("b-")).length, 4);
    const weightedPools = await poolCounts(3);
    assert.equal(weightedPools.filter((name) => name.startsWith("a-")).length, 6);
    assert.equal(weightedPools.filter((name) => name.startsWith("b-")).length, 2);
  });

  it("coalesces concurrent refreshes and probes arbitrary accounts sequentially", async () => {
    const reads = [0, 0, 0, 0];
    let activeReaders = 0;
    let maxActiveReaders = 0;
    const accounts = reads.map((_, index) => account({
      read: async () => {
        reads[index] += 1;
        activeReaders += 1;
        maxActiveReaders = Math.max(maxActiveReaders, activeReaders);
        await waitImmediate();
        activeReaders -= 1;
        return { usedPercent: 10, available: true };
      },
    }));
    const provider = new CodexAccountPoolProvider({ accounts, now: () => 1_000 });

    const outcomes = await Promise.all([
      provider.invokeTool(request()),
      provider.invokeTool(request()),
      provider.invokeTool(request()),
    ]);

    assert.ok(outcomes.every((outcome) => outcome.failure === undefined));
    assert.deepEqual(reads, [1, 1, 1, 1], "one pool-wide refresh should serve all callers");
    assert.equal(maxActiveReaders, 1, "app-server probes must never burst in parallel");
  });

  it("skips stale probes while real runs are in flight and spreads concurrent load", async () => {
    let now = 0;
    const reads = [0, 0];
    const started: string[] = [];
    let releaseRuns!: () => void;
    const gate = new Promise<void>((resolve) => {
      releaseRuns = resolve;
    });
    let notifySecond!: () => void;
    const secondStarted = new Promise<void>((resolve) => {
      notifySecond = resolve;
    });
    const accounts = ["a", "b"].map((name, index) => account({
      read: async () => {
        reads[index] += 1;
        return { usedPercent: 20, available: true };
      },
      run: async () => {
        started.push(name);
        if (started.length === 2) notifySecond();
        await gate;
        return success;
      },
    }));
    const provider = new CodexAccountPoolProvider({ accounts, now: () => now });

    const first = provider.invokeTool(request());
    while (started.length === 0) await waitImmediate();
    now = 600_000; // quota cache is stale, but a real run owns account a
    const second = provider.invokeTool(request());
    await secondStarted;

    assert.deepEqual(reads, [1, 1], "no app-server probes may start beside a real run");
    assert.deepEqual(new Set(started), new Set(["a", "b"]));
    assert.equal(provider.health().inFlight, 2);
    releaseRuns();
    await Promise.all([first, second]);
    assert.equal(provider.health().inFlight, 0);
  });

  it("fails over on auth/quota only and never exposes raw account errors", async () => {
    const calls: string[] = [];
    const provider = new CodexAccountPoolProvider({
      accounts: [
        account({ quota: { usedPercent: 5, available: true }, run: async () => {
          calls.push("first");
          throw new Error("401 unauthorized token=super-secret-first");
        } }),
        account({ quota: { usedPercent: 10, available: true }, run: async () => {
          calls.push("second");
          throw new Error("429 quota exceeded account=private-second");
        } }),
      ],
    });

    const outcome = await provider.invokeTool(request());
    assert.deepEqual(calls, ["first", "second"]);
    assert.equal(outcome.failure?.reason, "api_error");
    assert.doesNotMatch(outcome.failure?.detail ?? "", /secret|private|token=/i);
  });

  it("checks abort before trying a failover account", async () => {
    const controller = new AbortController();
    let secondCalls = 0;
    const provider = new CodexAccountPoolProvider({
      accounts: [
        account({ quota: { usedPercent: 1, available: true }, run: async () => {
          controller.abort();
          throw new Error("429 quota exceeded");
        } }),
        account({ quota: { usedPercent: 2, available: true }, run: async () => {
          secondCalls += 1;
          return success;
        } }),
      ],
    });

    const outcome = await provider.invokeTool(request(controller.signal));
    assert.equal(outcome.failure?.reason, "aborted");
    assert.equal(secondCalls, 0);
  });

  it("reprobes and recovers a single quota-limited account after the bounded TTL", async () => {
    let now = 1_000;
    let reads = 0;
    let runs = 0;
    const only: CodexAccount = {
      route: { accountId: "only", poolId: "single-pool", priority: 0, weight: 1 },
      quotaReader: { read: async () => {
        reads += 1;
        return null;
      } },
      runner: { run: async () => {
        runs += 1;
        if (runs === 1) throw new Error("429 quota exceeded");
        return success;
      } },
    };
    const provider = new CodexAccountPoolProvider({
      accounts: [only],
      now: () => now,
      quotaTtlMs: 100,
    });

    assert.equal((await provider.invokeTool(request())).failure?.reason, "api_error");
    assert.equal(runs, 1);
    assert.equal(reads, 1);
    assert.equal(provider.accountStatuses()[0]?.status, "quota_exhausted");
    assert.equal((await provider.invokeTool(request())).failure?.reason, "api_error");
    assert.equal(runs, 1, "cooldown must avoid an immediate rejected-account retry");
    assert.equal(reads, 1);

    now += 101;
    assert.equal((await provider.invokeTool(request())).failure, undefined);
    assert.equal(reads, 2);
    assert.equal(runs, 2);
    assert.equal(provider.accountStatuses()[0]?.status, "ready");
  });

  it("resets transient auth_invalid after a successful TTL reprobe/run", async () => {
    let now = 5_000;
    let runs = 0;
    const provider = new CodexAccountPoolProvider({
      accounts: [{
        route: { accountId: "auth", poolId: "auth-pool", priority: 0, weight: 1 },
        quotaReader: { read: async () => null },
        runner: { run: async () => {
          runs += 1;
          if (runs === 1) throw new Error("401 unauthorized");
          return success;
        } },
      }],
      now: () => now,
      quotaTtlMs: 100,
    });
    assert.equal((await provider.invokeTool(request())).failure?.reason, "auth");
    assert.equal(provider.accountStatuses()[0]?.status, "auth_invalid");
    assert.equal(provider.health().availableAccounts, 0);
    now += 101;
    assert.equal((await provider.invokeTool(request())).failure, undefined);
    assert.equal(provider.accountStatuses()[0]?.status, "ready");
    assert.equal(provider.health().availableAccounts, 1);
  });

  it("reports a proactive unavailable quota as quota_exhausted consistently", async () => {
    const provider = new CodexAccountPoolProvider({ accounts: [{
      ...account({ quota: { usedPercent: 100, available: false } }),
      route: { accountId: "limited", poolId: "limited-pool", priority: 0, weight: 1 },
    }] });
    assert.equal((await provider.invokeTool(request())).failure?.reason, "api_error");
    assert.equal(provider.health().availableAccounts, 0);
    assert.deepEqual(provider.accountStatuses(), [{
      accountId: "limited",
      poolId: "limited-pool",
      status: "quota_exhausted",
      available: false,
      inFlight: 0,
    }]);
  });
});
