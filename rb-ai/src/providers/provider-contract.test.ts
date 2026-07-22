import assert from "node:assert/strict";
import { describe, test } from "node:test";
import type { Options } from "@anthropic-ai/claude-agent-sdk";
import {
  buildInteractionToolShape,
  enforceInteractionVocabulary,
  type InteractionExtractRequest,
} from "../extract.js";
import { ClaudeAgentToolProvider, type QueryRunner } from "./claude-agent.js";
import {
  CodexAccountPoolProvider,
  type CodexRunRequest,
  type CodexRunResult,
} from "./codex.js";
import { MODEL_TARGETS, ProviderRegistry } from "./registry.js";
import type {
  ModelAlias,
  ToolProvider,
  ToolProviderRequest,
  ToolProviderResult,
} from "./types.js";

// ai.ts reads these at module initialization. This test worker deliberately
// gets a short real deadline and enough background slots to exercise every
// registered target in parallel without ever starting a real provider call.
process.env.AI_EXTRACT_TIMEOUT_MS = "1000";
process.env.AI_MAX_CONCURRENCY = "8";
process.env.AI_INTERACTIVE_RESERVE = "0";

// A developer machine may have live subscription credentials. Remove them in
// this isolated test worker before the production registry is imported: a
// contract test must be unable to contact an LLM even after a future refactor.
for (const key of Object.keys(process.env)) {
  if (/^(?:CLAUDE_CODE_OAUTH_TOKEN|ANTHROPIC_API_KEY|CODEX_ACCESS_TOKEN|CODEX_HOME)(?:_\d+)?$/.test(key))
    delete process.env[key];
}

const { extractWithTool, providerRegistry } = await import("../ai.js");

type ContractBehavior =
  | { kind: "payload"; payload: unknown }
  | { kind: "no_output" }
  | { kind: "auth_error" }
  | { kind: "api_error" }
  | { kind: "spawn_error" }
  | { kind: "wait_for_abort" };

interface ContractDriver {
  provider: ToolProvider;
  modelIds: string[];
  assertStructuredGate(toolName: string): void;
}

type ContractFactory = (behavior: ContractBehavior) => ContractDriver;

const untilAbort = (signal: AbortSignal) =>
  new Promise<void>((resolve) => {
    if (signal.aborted) return resolve();
    signal.addEventListener("abort", () => resolve(), { once: true });
  });

type RegisteredTool = {
  handler: (input: unknown, extra: unknown) => Promise<unknown>;
};

type SdkServerInternals = {
  instance: {
    _registeredTools: Record<string, RegisteredTool>;
  };
};

function claudeContract(behavior: ContractBehavior): ContractDriver {
  const modelIds: string[] = [];
  const observed: Options[] = [];
  const runQuery: QueryRunner = ({ options }) => (async function* () {
    observed.push(options);
    modelIds.push(String(options.model));
    const server = options.mcpServers?.extract as unknown as SdkServerInternals | undefined;

    if (behavior.kind === "wait_for_abort") {
      await untilAbort(options.abortController!.signal);
      throw Object.assign(new Error("Claude mock run aborted"), { name: "AbortError" });
    }
    if (behavior.kind === "spawn_error")
      throw new Error("Failed to spawn Claude Code process: ENOENT");
    if (behavior.kind === "auth_error") {
      yield {
        type: "assistant",
        error: "authentication_failed",
        message: { content: [] },
      };
      yield {
        type: "result",
        subtype: "error_during_execution",
        is_error: true,
        errors: ["authentication_failed"],
      };
      return;
    }
    if (behavior.kind === "api_error") {
      yield {
        type: "result",
        subtype: "error_during_execution",
        is_error: true,
        api_error_status: 529,
        errors: ["overloaded"],
      };
      return;
    }
    if (behavior.kind === "payload") {
      assert.ok(server, "Claude-contract kreeg geen in-process extract-server");
      const tools = server.instance._registeredTools;
      const [registeredName] = Object.keys(tools);
      assert.ok(registeredName, "Claude-contract kreeg geen geregistreerde tool");
      await tools[registeredName]!.handler(behavior.payload, {});
    }
    yield {
      type: "result",
      subtype: "success",
      is_error: false,
      usage: { input_tokens: 11, output_tokens: 7 },
    };
  })();

  return {
    provider: new ClaudeAgentToolProvider(runQuery, {
      ANTHROPIC_API_KEY: "provider-contract-placeholder",
    }),
    modelIds,
    assertStructuredGate(toolName: string) {
      assert.equal(observed.length, 1);
      const [options] = observed;
      assert.deepEqual(options!.tools, [], "built-in tools moeten uit staan");
      assert.deepEqual(options!.allowedTools, [`mcp__extract__${toolName}`]);
      assert.equal(options!.permissionMode, "dontAsk");
      assert.deepEqual(Object.keys(options!.mcpServers ?? {}), ["extract"]);
    },
  };
}

function codexContract(behavior: ContractBehavior): ContractDriver {
  const modelIds: string[] = [];
  const requests: CodexRunRequest[] = [];
  const runner = {
    async run(request: CodexRunRequest): Promise<CodexRunResult> {
      requests.push(request);
      modelIds.push(request.modelId);
      if (behavior.kind === "wait_for_abort") {
        await untilAbort(request.signal);
        throw Object.assign(new Error("Codex mock run aborted"), { name: "AbortError" });
      }
      if (behavior.kind === "spawn_error") throw new Error("spawn ENOENT: codex");
      if (behavior.kind === "auth_error") throw new Error("401 unauthorized: access token rejected");
      if (behavior.kind === "api_error") throw new Error("429 rate limit: quota exhausted");
      return {
        finalResponse: behavior.kind === "payload"
          ? JSON.stringify(behavior.payload)
          : "geen gestructureerde JSON",
        usage: { input_tokens: 11, output_tokens: 7 },
      };
    },
  };
  const provider = new CodexAccountPoolProvider({
    accounts: [{ runner, quotaReader: { read: async () => null } }],
  });

  return {
    provider,
    modelIds,
    assertStructuredGate(toolName: string) {
      assert.equal(requests.length, 1);
      const [request] = requests;
      assert.match(request!.prompt, /Return only one JSON object/);
      assert.match(request!.prompt, /untrusted data/);
      const schema = request!.outputSchema as {
        type?: unknown;
        properties?: Record<string, unknown>;
      };
      assert.equal(schema.type, "object");
      assert.ok(schema.properties?.interactions, `${toolName} mist in Codex outputSchema`);
    },
  };
}

/**
 * Explicit adapter proof catalogue. MODEL_TARGETS and the production registry
 * are compared with this catalogue below. Adding a provider without a mock
 * contract therefore fails before any network-capable path can become live.
 */
const CONTRACT_FACTORIES: Readonly<Record<string, ContractFactory>> = Object.freeze({
  "claude-agent-sdk": claudeContract,
  "codex-sdk": codexContract,
});

const targetEntries = Object.entries(MODEL_TARGETS) as Array<[
  ModelAlias,
  { providerId: string; modelId: string },
]>;

const interactionRequest: InteractionExtractRequest = {
  text: "Deflect prevents Assault damage during a showdown.",
  refs: [
    { ref: "mechanic:Deflect", label: "Deflect" },
    { ref: "mechanic:Assault", label: "Assault" },
  ],
  kinds: ["COUNTERS", "MODIFIES"],
  conditionKinds: ["WINDOW"],
  roles: ["agent", "patient"],
  windowLexicon: ["Showdown"],
  statusLexicon: [],
  sections: ["section:core-4.2b"],
};

const validInteraction = {
  from: "mechanic:Deflect",
  to: "mechanic:Assault",
  kind: "COUNTERS",
  interacts: true,
  explanation: "Deflect prevents the damage.",
  conditions: [{ on_kind: "WINDOW", window: "Showdown", subject_role: "patient" }],
  governed_by: "section:core-4.2b",
};

const mixedPayload = {
  interactions: [
    validInteraction,
    { ...validInteraction, to: "mechanic:Invented" },
    {
      ...validInteraction,
      from: "mechanic:Assault",
      to: "mechanic:Deflect",
      kind: "MODIFIES",
      conditions: [{ on_kind: "WINDOW", window: "Invented window" }],
    },
  ],
};

function factoryFor(providerId: string): ContractFactory {
  const factory = CONTRACT_FACTORIES[providerId];
  assert.ok(
    factory,
    `provider '${providerId}' heeft geen mock-adapter in provider-contract.test.ts`,
  );
  return factory;
}

async function extract(
  alias: ModelAlias,
  driver: ContractDriver,
  signal?: AbortSignal,
) {
  return await extractWithTool({
    toolName: "emit_interactions",
    description: "Emit interactions",
    schema: buildInteractionToolShape(),
    resultKey: "interactions",
    addendum: "Call emit_interactions exactly once.",
    text: "public rule text",
    model: alias,
    registry: new ProviderRegistry([driver.provider]),
    signal,
  });
}

function directRequest(signal: AbortSignal): ToolProviderRequest {
  return {
    modelId: "contract-model",
    systemPrompt: "contract system",
    prompt: "public rule text",
    tool: {
      name: "emit_interactions",
      description: "Emit interactions",
      schema: buildInteractionToolShape(),
    },
    signal,
    onToolCall: () => false,
  };
}

test("contractcatalogus dekt exact alle productieproviders en registry-targets", () => {
  const covered = Object.keys(CONTRACT_FACTORIES).sort();
  const targeted = [...new Set(targetEntries.map(([, target]) => target.providerId))].sort();
  const registered = providerRegistry.list().map((provider) => provider.id).sort();
  assert.deepEqual(covered, targeted, "MODEL_TARGETS bevat een provider zonder contractsuite");
  assert.deepEqual(covered, registered, "production ProviderRegistry bevat een provider zonder contractsuite");
  assert.ok(targetEntries.length > 0, "lege registry zou de matrix vacuüm maken");
});

for (const [alias, target] of targetEntries) {
  describe(`providercontract ${alias} -> ${target.providerId}/${target.modelId}`, () => {
    test("registry resolveert gesloten alias en structured output door de providerpoort", async () => {
      const driver = factoryFor(target.providerId)({ kind: "payload", payload: mixedPayload });
      assert.equal(driver.provider.configured(), true);

      const outcome = await extract(alias, driver);

      assert.equal(outcome.provider, target.providerId);
      assert.equal(outcome.model, target.modelId);
      assert.deepEqual(driver.modelIds, [target.modelId]);
      assert.deepEqual(outcome.usage, { inputTokens: 11, outputTokens: 7, unit: "tokens" });
      assert.equal(outcome.failure, undefined);
      assert.equal(outcome.items?.length, 3);
      driver.assertStructuredGate("emit_interactions");

      // The schema deliberately accepts free vocabulary strings. The shared
      // deterministic gate then salvages good items/conditions and removes
      // only the invented values, independent of provider/model.
      const gated = enforceInteractionVocabulary(outcome.items ?? [], interactionRequest);
      assert.equal(gated.rejected, 1);
      assert.equal(gated.rejectedConditions, 1);
      assert.equal(gated.accepted.length, 2);
      assert.deepEqual(gated.accepted[0], {
        ...validInteraction,
        conditions: [{
          on_kind: "WINDOW",
          subject_role: "patient",
          window: "Showdown",
          status: null,
          value: null,
          operator: null,
        }],
      });
      assert.equal(gated.accepted[1]?.from, "mechanic:Assault");
      assert.equal(gated.accepted[1]?.conditions, undefined);
    });

    test("schema-invalid toolinput wordt nooit gevangen", async () => {
      const payload = {
        interactions: [{ ...validInteraction, interacts: "yes" }],
      };
      const outcome = await extract(
        alias,
        factoryFor(target.providerId)({ kind: "payload", payload }),
      );
      assert.equal(outcome.items, null);
      assert.equal(outcome.failure?.reason, "no_tool_call");
    });

    test("ontbrekende structured output wordt no_tool_call", async () => {
      const outcome = await extract(
        alias,
        factoryFor(target.providerId)({ kind: "no_output" }),
      );
      assert.equal(outcome.items, null);
      assert.equal(outcome.failure?.reason, "no_tool_call");
    });

    test("een provider kan de naam-allowlist niet omzeilen", async () => {
      let wrongToolAccepted: boolean | undefined;
      const hostile: ToolProvider = {
        id: target.providerId,
        configured: () => true,
        async invokeTool(request): Promise<ToolProviderResult> {
          wrongToolAccepted = request.onToolCall("emit_something_else", mixedPayload);
          return { usage: null };
        },
      };
      const outcome = await extract(alias, {
        provider: hostile,
        modelIds: [],
        assertStructuredGate: () => {},
      });
      assert.equal(wrongToolAccepted, false);
      assert.equal(outcome.items, null);
      assert.equal(outcome.failure?.reason, "no_tool_call");
    });

    test("een geldige vroege toolvangst overleeft een latere providerfout", async () => {
      const partial: ToolProvider = {
        id: target.providerId,
        configured: () => true,
        async invokeTool(request): Promise<ToolProviderResult> {
          assert.equal(request.onToolCall(request.tool.name, { interactions: [validInteraction] }), true);
          return {
            failure: { reason: "api_error", detail: "provider viel na de tool-call uit" },
            usage: { inputTokens: 3, outputTokens: 2, unit: "tokens" },
          };
        },
      };
      const outcome = await extract(alias, {
        provider: partial,
        modelIds: [],
        assertStructuredGate: () => {},
      });
      assert.deepEqual(outcome.items, [validInteraction]);
      assert.equal(outcome.failure, undefined, "bruikbare toolvangst hoort te winnen");
      assert.deepEqual(outcome.usage, { inputTokens: 3, outputTokens: 2, unit: "tokens" });
    });

    for (const [kind, reason] of [
      ["auth_error", "auth"],
      ["api_error", "api_error"],
      ["spawn_error", "spawn"],
    ] as const) {
      test(`classificeert ${kind} als ${reason}`, async () => {
        const outcome = await extract(alias, factoryFor(target.providerId)({ kind }));
        assert.equal(outcome.items, null);
        assert.equal(outcome.failure?.reason, reason);
      });
    }

    test("provideradapter honoreert een reeds afgebroken signal", async () => {
      const controller = new AbortController();
      controller.abort();
      const driver = factoryFor(target.providerId)({ kind: "wait_for_abort" });
      const result = await driver.provider.invokeTool(directRequest(controller.signal));
      assert.equal(result.failure?.reason, "aborted");
    });
  });
}

test("harde extractietimeout breekt elke geregistreerde targetadapter af", async () => {
  const outcomes = await Promise.all(targetEntries.map(async ([alias, target]) => {
    const driver = factoryFor(target.providerId)({ kind: "wait_for_abort" });
    return [alias, await extract(alias, driver)] as const;
  }));

  for (const [alias, outcome] of outcomes) {
    assert.equal(outcome.items, null, alias);
    assert.equal(outcome.timedOut, true, alias);
    assert.equal(outcome.failure?.reason, "timeout", alias);
  }
});
