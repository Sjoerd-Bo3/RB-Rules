import { AiRunError } from "../failure.js";
import type { ModelAlias, ResolvedModel, ToolProvider } from "./types.js";

export const MODEL_TARGETS = {
  sonnet: { providerId: "claude-agent-sdk", modelId: "claude-sonnet-4-6" },
  opus: { providerId: "claude-agent-sdk", modelId: "claude-opus-4-8" },
  fable: { providerId: "claude-agent-sdk", modelId: "claude-fable-5" },
  codex: { providerId: "codex-sdk", modelId: "gpt-5.6-sol" },
} as const satisfies Record<ModelAlias, { providerId: string; modelId: string }>;

export function isModelAlias(value: unknown): value is ModelAlias {
  return typeof value === "string" && Object.hasOwn(MODEL_TARGETS, value);
}

export class ProviderRegistry {
  private readonly providers = new Map<string, ToolProvider>();

  constructor(providers: readonly ToolProvider[]) {
    for (const provider of providers) {
      if (this.providers.has(provider.id))
        throw new Error(`dubbele AI-providerregistratie: ${provider.id}`);
      this.providers.set(provider.id, provider);
    }
  }

  resolve(alias: ModelAlias): ResolvedModel {
    const target = MODEL_TARGETS[alias];
    const provider = this.providers.get(target.providerId);
    if (!provider)
      throw new AiRunError({
        reason: "unknown",
        detail: `provider '${target.providerId}' voor modelalias '${alias}' is niet geregistreerd`,
      });
    return { alias, provider, providerId: provider.id, modelId: target.modelId };
  }

  list(): readonly ToolProvider[] {
    return [...this.providers.values()];
  }
}
