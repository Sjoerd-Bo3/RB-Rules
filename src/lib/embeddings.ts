// Embeddings — provider-pluggable. Default: lokaal via Ollama (gratis, geen externe
// call). Alternatief: Voyage (cloud). Kies met EMBEDDINGS_PROVIDER=ollama|voyage.
type Provider = "ollama" | "voyage";

function provider(): Provider {
  return process.env.EMBEDDINGS_PROVIDER === "voyage" ? "voyage" : "ollama";
}

export function embeddingsConfigured(): boolean {
  return provider() === "voyage" ? Boolean(process.env.VOYAGE_API_KEY) : true;
}

export function embeddingsInfo(): string {
  return provider() === "voyage"
    ? "voyage:voyage-3"
    : `ollama:${process.env.OLLAMA_EMBED_MODEL ?? "nomic-embed-text"}`;
}

export async function embed(
  texts: string[],
  inputType: "document" | "query",
): Promise<number[][]> {
  return provider() === "voyage" ? voyageEmbed(texts, inputType) : ollamaEmbed(texts);
}

// ── Lokaal: Ollama ──────────────────────────────────────────────
async function ollamaEmbed(texts: string[]): Promise<number[][]> {
  const url = process.env.OLLAMA_URL ?? "http://localhost:11434";
  const model = process.env.OLLAMA_EMBED_MODEL ?? "nomic-embed-text";
  const res = await fetch(`${url}/api/embed`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ model, input: texts }),
  });
  if (!res.ok) throw new Error(`Ollama HTTP ${res.status} (draait het model '${model}'?)`);
  const j = (await res.json()) as { embeddings?: number[][] };
  if (!j.embeddings) throw new Error("Ollama gaf geen embeddings terug");
  return j.embeddings;
}

// ── Cloud: Voyage ───────────────────────────────────────────────
async function voyageEmbed(
  texts: string[],
  inputType: "document" | "query",
): Promise<number[][]> {
  const key = process.env.VOYAGE_API_KEY;
  if (!key) throw new Error("VOYAGE_API_KEY ontbreekt");
  const res = await fetch("https://api.voyageai.com/v1/embeddings", {
    method: "POST",
    headers: { authorization: `Bearer ${key}`, "content-type": "application/json" },
    body: JSON.stringify({ input: texts, model: "voyage-3", input_type: inputType }),
  });
  if (!res.ok) throw new Error(`Voyage HTTP ${res.status}`);
  const j = (await res.json()) as { data: { embedding: number[] }[] };
  return j.data.map((d) => d.embedding);
}

/** pgvector-literal: [0.1,0.2,...] */
export function toVectorLiteral(v: number[]): string {
  return `[${v.join(",")}]`;
}
