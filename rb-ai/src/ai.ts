import { query } from "@anthropic-ai/claude-agent-sdk";

// Auth: CLAUDE_CODE_OAUTH_TOKEN (abonnement) of ANTHROPIC_API_KEY.
// Laat ANTHROPIC_API_KEY leeg bij abonnementsgebruik — die wint stilletjes.
export type Task = "cheap" | "hard";

export interface AskImage {
  mediaType: string; // image/jpeg | image/png | image/webp | image/gif
  data: string; // base64
}

const MODEL: Record<Task, string> = {
  cheap: "claude-sonnet-4-6",
  hard: "claude-opus-4-8",
};

/** Streaming-input met content-blocks — nodig zodra er afbeeldingen meegaan. */
async function* userMessage(prompt: string, images: AskImage[]) {
  yield {
    type: "user" as const,
    message: {
      role: "user" as const,
      content: [
        ...images.map((img) => ({
          type: "image" as const,
          source: {
            type: "base64" as const,
            media_type: img.mediaType,
            data: img.data,
          },
        })),
        { type: "text" as const, text: prompt },
      ],
    },
    parent_tool_use_id: null,
    session_id: "rb-ai",
  };
}

/** Stuur één prompt (optioneel met afbeeldingen) naar Claude. */
export async function askClaude(opts: {
  prompt: string;
  system?: string;
  task?: Task;
  images?: AskImage[];
}): Promise<string> {
  const { prompt, system, task = "cheap", images = [] } = opts;

  const arg = {
    prompt: images.length > 0 ? userMessage(prompt, images) : prompt,
    options: {
      model: MODEL[task],
      maxTurns: 1,
      ...(system ? { systemPrompt: system } : {}),
    },
  };

  // De Agent SDK levert dezelfde tekst twee keer: als streaming 'assistant'-
  // berichten én als afsluitend 'result'-bericht. Tel ze NIET op — verzamel
  // apart en geef het 'result' terug; val terug op assistant-tekst zonder result.
  let assistantText = "";
  let resultText = "";
  for await (const message of query(arg as Parameters<typeof query>[0])) {
    const m = message as {
      type: string;
      text?: string;
      result?: string;
      message?: { content?: Array<{ type: string; text?: string }> };
    };
    if (m.type === "assistant" && Array.isArray(m.message?.content)) {
      for (const block of m.message.content) {
        if (block.type === "text" && block.text) assistantText += block.text;
      }
    } else if (m.type === "text" && m.text) {
      assistantText += m.text;
    } else if (m.type === "result" && m.result) {
      resultText += m.result;
    }
  }
  return (resultText || assistantText).trim();
}
