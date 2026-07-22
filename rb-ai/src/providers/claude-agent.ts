import {
  createSdkMcpServer,
  query,
  tool,
  type Options,
} from "@anthropic-ai/claude-agent-sdk";
import {
  AiRunError,
  describeThrown,
  failureOf,
  resultFailure,
  RetryTracker,
  safeDetail,
  StderrTail,
  withRetries,
  withStderr,
  type AiFailure,
} from "../failure.js";
import { usageFromSdk } from "../usage.js";
import type {
  ProviderDiagnostics,
  ProviderUsage,
  ToolProvider,
  ToolProviderRequest,
} from "./types.js";

const EXTRACT_MAX_TURNS = 3;

export type QueryRunner = (arg: {
  prompt: string;
  options: Options;
}) => AsyncIterable<unknown>;

export interface ClaudeRateLimitSignal {
  status: "allowed" | "allowed_warning" | "rejected";
  utilization?: number;
  resetsAt?: number;
  rateLimitType?: string;
}

export interface ClaudeToolProviderResult {
  failure?: AiFailure;
  usage: ProviderUsage | null;
  diagnostics?: ProviderDiagnostics;
  accountSignal?: ClaudeRateLimitSignal;
}

export function claudeRateLimitSignal(message: unknown): ClaudeRateLimitSignal | undefined {
  if (typeof message !== "object" || message === null) return undefined;
  const root = message as Record<string, unknown>;
  if (root.type !== "rate_limit_event") return undefined;
  const info = typeof root.rate_limit_info === "object" && root.rate_limit_info !== null
    ? root.rate_limit_info as Record<string, unknown>
    : null;
  if (!info || !["allowed", "allowed_warning", "rejected"].includes(String(info.status)))
    return undefined;
  const rawUtilization = typeof info.utilization === "number" && Number.isFinite(info.utilization)
    ? info.utilization
    : undefined;
  // CLI rate-limit events have historically reported a 0..1 fraction, while
  // the experimental usage control response documents 0..100 percentages.
  // Normalize the event here before it is combined with quota scores.
  const utilization = rawUtilization === undefined
    ? undefined
    : Math.max(0, Math.min(100, rawUtilization <= 1 ? rawUtilization * 100 : rawUtilization));
  return {
    status: info.status as ClaudeRateLimitSignal["status"],
    ...(utilization !== undefined ? { utilization } : {}),
    ...(typeof info.resetsAt === "number" && Number.isFinite(info.resetsAt)
      ? { resetsAt: info.resetsAt }
      : {}),
    ...(typeof info.rateLimitType === "string" ? { rateLimitType: info.rateLimitType } : {}),
  };
}

export function claudeAssistantFailure(message: unknown): AiFailure | undefined {
  if (typeof message !== "object" || message === null) return undefined;
  const root = message as Record<string, unknown>;
  if (root.type !== "assistant" || typeof root.error !== "string") return undefined;
  if (["authentication_failed", "oauth_org_not_allowed", "billing_error"].includes(root.error))
    return { reason: "auth", detail: "Claude-accountauthenticatie mislukt" };
  if (root.error === "rate_limit")
    return { reason: "api_error", detail: "Claude-account heeft zijn rate limit bereikt" };
  return undefined;
}

/** Preserve account-specific signals when the terminal result is less specific. */
export function mergeClaudeMessageFailure(
  current: AiFailure | undefined,
  message: unknown,
): AiFailure | undefined {
  const accountFailure = claudeAssistantFailure(message);
  if (accountFailure) return accountFailure;
  const currentIsAccountSpecific = current?.reason === "auth"
    || (current?.reason === "api_error" && /rate.?limit|\b429\b/i.test(current.detail));
  return currentIsAccountSpecific ? current : resultFailure(message) ?? current;
}

function diagnostics(stderr: StderrTail, retries: RetryTracker): ProviderDiagnostics | undefined {
  const parts: string[] = [];
  const tail = stderr.tail();
  if (tail) parts.push(`stderr: ${tail}`);
  const retrySummary = retries.summary();
  if (retrySummary) parts.push(retrySummary);
  const detail = parts.length > 0 ? safeDetail(parts.join(" | ")) : undefined;
  const timeoutReason = retries.reason() ?? undefined;
  return detail || timeoutReason ? { detail, timeoutReason } : undefined;
}

/** Claude Agent SDK implementation of the stateless tool-forced provider port. */
export class ClaudeAgentToolProvider implements ToolProvider {
  readonly id = "claude-agent-sdk";

  constructor(
    private readonly runQuery: QueryRunner = query as unknown as QueryRunner,
    private readonly environment?: Readonly<Record<string, string>>,
  ) {}

  configured(): boolean {
    if (this.environment)
      return Boolean(
        this.environment.CLAUDE_CODE_OAUTH_TOKEN || this.environment.ANTHROPIC_API_KEY,
      );
    return Boolean(process.env.CLAUDE_CODE_OAUTH_TOKEN || process.env.ANTHROPIC_API_KEY);
  }

  async invokeTool(request: ToolProviderRequest): Promise<ClaudeToolProviderResult> {
    const serverName = "extract";
    const controller = new AbortController();
    const onAbort = () => controller.abort();
    if (request.signal.aborted) controller.abort();
    else request.signal.addEventListener("abort", onAbort, { once: true });

    const stderr = new StderrTail();
    const retries = new RetryTracker();
    let failure: AiFailure | undefined;
    let usage: ProviderUsage | null = null;
    let accountSignal: ClaudeRateLimitSignal | undefined;
    const extractServer = createSdkMcpServer({
      name: serverName,
      version: "1.0.0",
      tools: [
        tool(request.tool.name, request.tool.description, request.tool.schema, async (args) => {
          request.onToolCall(request.tool.name, args);
          return { content: [{ type: "text" as const, text: "ok" }] };
        }),
      ],
    });

    const options: Options = {
      model: request.modelId,
      maxTurns: EXTRACT_MAX_TURNS,
      tools: [],
      mcpServers: { [serverName]: extractServer },
      allowedTools: [`mcp__${serverName}__${request.tool.name}`],
      permissionMode: "dontAsk" as const,
      abortController: controller,
      systemPrompt: request.systemPrompt,
      stderr: (data: string) => stderr.append(data),
      ...(this.environment
        ? {
            env: { ...this.environment },
            persistSession: false,
            settingSources: [],
          }
        : {}),
    };

    try {
      for await (const message of this.runQuery({ prompt: request.prompt, options })) {
        retries.observe(message);
        accountSignal = claudeRateLimitSignal(message) ?? accountSignal;
        failure = mergeClaudeMessageFailure(failure, message);
        if (accountSignal?.status === "rejected")
          failure = failure
            ?? { reason: "api_error", detail: "Claude-account heeft zijn rate limit bereikt" };
        if (typeof message === "object" && message !== null) {
          const mapped = usageFromSdk((message as Record<string, unknown>).usage);
          if (mapped) usage = { ...mapped, unit: "tokens" };
        }
      }
      return {
        failure: failure ? withRetries(withStderr(failure, stderr), retries) : undefined,
        usage,
        diagnostics: diagnostics(stderr, retries),
        ...(accountSignal ? { accountSignal } : {}),
      };
    } catch (error) {
      const mapped = error instanceof AiRunError ? failureOf(error) : failure ?? describeThrown(error);
      return {
        failure: withRetries(withStderr(mapped, stderr), retries),
        usage,
        diagnostics: diagnostics(stderr, retries),
        ...(accountSignal ? { accountSignal } : {}),
      };
    } finally {
      request.signal.removeEventListener("abort", onAbort);
    }
  }
}
