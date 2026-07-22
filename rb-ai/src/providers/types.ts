import type { z } from "zod";
import type { AiFailure, AiFailureReason } from "../failure.js";

/** Provider-neutral accounting for one tool-forced model run. */
export interface ProviderUsage {
  inputTokens: number;
  outputTokens: number;
  unit: "tokens";
  /** Providers that expose a billed amount may include it. */
  costUsd?: number;
}

export interface ProviderDiagnostics {
  /** Sanitized provider diagnostics only; never prompt or tool-input content. */
  detail?: string;
  /** Upstream cause that should remain visible when the outer deadline aborts. */
  timeoutReason?: AiFailureReason;
}

export interface ToolDefinition {
  name: string;
  description: string;
  schema: z.ZodRawShape;
}

export interface ToolProviderRequest {
  modelId: string;
  systemPrompt: string;
  prompt: string;
  tool: ToolDefinition;
  signal: AbortSignal;
  /** Returns true only when the common schema gate accepted the payload. */
  onToolCall: (name: string, input: unknown) => boolean;
}

export interface ToolProviderResult {
  failure?: AiFailure;
  usage: ProviderUsage | null;
  diagnostics?: ProviderDiagnostics;
}

/** Stateless, tool-forced provider port. The agentic /ask path deliberately stays out. */
export interface ToolProvider {
  readonly id: string;
  configured(): boolean;
  /** Aggregate operational state only; never account identity or credentials. */
  health?(): ProviderAccountHealth;
  invokeTool(request: ToolProviderRequest): Promise<ToolProviderResult>;
}

export interface ProviderAccountHealth {
  configuredAccounts: number;
  availableAccounts: number;
  inFlight: number;
}

/** Safe routing metadata supplied by managed/environment pool builders. */
export interface AccountRouteMetadata {
  accountId: string;
  poolId: string;
  priority: number;
  weight: number;
}

export interface ResolvedModel {
  alias: ModelAlias;
  provider: ToolProvider;
  providerId: string;
  modelId: string;
}

/** Closed aliases: no request-controlled model id ever reaches a provider. */
export type ModelAlias = "sonnet" | "opus" | "fable" | "codex";
