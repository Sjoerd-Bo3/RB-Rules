import { query } from "@anthropic-ai/claude-agent-sdk";

// Auth: de Agent SDK leest CLAUDE_CODE_OAUTH_TOKEN (abonnement) of valt terug op
// ANTHROPIC_API_KEY. Zie docs/AI_AUTH.md. Laat ANTHROPIC_API_KEY leeg als je het
// abonnement gebruikt, anders wint die.
export type Task = "cheap" | "hard";

const MODEL: Record<Task, string> = {
  cheap: "claude-sonnet-4-6", // change-uitleg, classificatie, goedkope Q&A
  hard: "claude-opus-4-8", // lastige rulings, foto-redenering
};

/** Stuur één prompt naar Claude en geef de samengevoegde tekst terug. */
export async function askClaude(opts: {
  prompt: string;
  system?: string;
  task?: Task;
}): Promise<string> {
  const { prompt, system, task = "cheap" } = opts;

  const arg = {
    prompt,
    options: {
      model: MODEL[task],
      maxTurns: 1,
      ...(system ? { systemPrompt: system } : {}),
    },
  };

  let out = "";
  for await (const message of query(arg as Parameters<typeof query>[0])) {
    const m = message as {
      type: string;
      text?: string;
      result?: string;
      message?: { content?: Array<{ type: string; text?: string }> };
    };
    if (m.type === "assistant" && Array.isArray(m.message?.content)) {
      for (const block of m.message!.content!) {
        if (block.type === "text" && block.text) out += block.text;
      }
    } else if (m.type === "text" && m.text) {
      out += m.text;
    } else if (m.type === "result" && m.result) {
      out += m.result;
    }
  }
  return out.trim();
}
