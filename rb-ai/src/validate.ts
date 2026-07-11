import type { AskImage, Task } from "./ai.js";

// Pure request-validatie voor POST /ask — apart van server.ts zodat dit
// zonder netwerk of SDK unit-testbaar is (npm test).

export interface AskRequest {
  prompt: string;
  system?: string;
  task: Task;
  images: AskImage[];
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

  // Opt-in per taak (#64): web-toegang alléén bij expliciet task="research".
  // Onbekende waarden vallen — net als voorheen — terug op "cheap", zodat
  // een tikfout nooit stilzwijgend web-tools (kosten/latency) aanzet.
  const task: Task =
    b.task === "hard" || b.task === "research" ? b.task : "cheap";

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

  return { ok: true, request: { prompt, system, task, images } };
}
