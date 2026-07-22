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
});
