import type { AskImage, Task } from "./ai.js";

// Pure request-validatie voor POST /ask — apart van server.ts zodat dit
// zonder netwerk of SDK unit-testbaar is (npm test).

export interface AskRequest {
  prompt: string;
  system?: string;
  task: Task;
  images: AskImage[];
  /** Model-sweep (#174): expliciete modeloverride voor benchmarkruns —
   * doorgegeven ongewijzigd naar askClaude()/de SDK-query(). Undefined
   * zonder override (het bestaande gedrag, MODEL[task]). */
  model?: string;
}

export type ParseResult =
  | { ok: true; request: AskRequest }
  | { ok: false; error: string };

const ALLOWED_MEDIA = new Set(["image/jpeg", "image/png", "image/webp", "image/gif"]);
const MAX_IMAGE_B64 = 8_000_000; // ~6 MB binair per afbeelding
const MAX_IMAGES = 2;

export function parseAskRequest(body: unknown): ParseResult {
  const b = (typeof body === "object" && body !== null ? body : {}) as Record<
    string,
    unknown
  >;

  const prompt = typeof b.prompt === "string" ? b.prompt : "";
  if (!prompt.trim()) return { ok: false, error: "prompt vereist" };

  const system =
    typeof b.system === "string" && b.system.trim() ? b.system : undefined;

  // Opt-in per taak: web-toegang alléén bij expliciet task="research" (#64);
  // brein-tools alléén bij expliciet task="agentic" (#106). Onbekende waarden
  // vallen — net als voorheen — terug op "cheap", zodat een tikfout nooit
  // stilzwijgend tools (kosten/latency) aanzet.
  const task: Task =
    b.task === "hard" || b.task === "research" || b.task === "agentic"
      ? b.task
      : "cheap";

  const rawImages = Array.isArray(b.images) ? b.images.slice(0, MAX_IMAGES) : [];
  const images: AskImage[] = [];
  for (const raw of rawImages) {
    const img = (typeof raw === "object" && raw !== null ? raw : {}) as Record<
      string,
      unknown
    >;
    const mediaType = typeof img.mediaType === "string" ? img.mediaType : "";
    if (!ALLOWED_MEDIA.has(mediaType))
      return { ok: false, error: `mediaType niet ondersteund: ${mediaType}` };
    const data = typeof img.data === "string" ? img.data : "";
    if (!data || data.length > MAX_IMAGE_B64)
      return { ok: false, error: "afbeelding ontbreekt of is te groot (max ~6 MB)" };
    images.push({ mediaType, data });
  }

  // Model-sweep (#174): puur doorgeefluik — geen allowlist/validatie van de
  // waarde hier. Een onbekend model laten we de SDK-call zelf laten falen
  // (nette degradatie via het bestaande foutpad in server.ts), net zoals een
  // ontbrekend model of een tikfout in `task` dat al deed.
  const model = typeof b.model === "string" && b.model.trim() ? b.model.trim() : undefined;

  return { ok: true, request: { prompt, system, task, images, ...(model ? { model } : {}) } };
}
