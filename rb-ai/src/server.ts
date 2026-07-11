// rb-ai: interne AI-sidecar. Draait de Claude Agent SDK op het abonnement
// (CLAUDE_CODE_OAUTH_TOKEN) zodat rb-api (.NET) geen per-token API-key nodig
// heeft. Alleen bereikbaar binnen het compose-netwerk — nooit publiek exposen.
import { createServer } from "node:http";
import { askClaude, type AskImage, type Task } from "./ai.js";

const PORT = Number(process.env.PORT ?? 8090);

interface AskBody {
  prompt?: string;
  system?: string;
  task?: Task;
  images?: AskImage[];
}

const ALLOWED_MEDIA = new Set(["image/jpeg", "image/png", "image/webp", "image/gif"]);
const MAX_IMAGE_B64 = 8_000_000; // ~6 MB binair per afbeelding

async function readJson(req: import("node:http").IncomingMessage): Promise<AskBody> {
  const chunks: Buffer[] = [];
  for await (const c of req) chunks.push(c as Buffer);
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8")) as AskBody;
  } catch {
    return {};
  }
}

const server = createServer(async (req, res) => {
  const send = (status: number, body: unknown) => {
    res.writeHead(status, { "content-type": "application/json" });
    res.end(JSON.stringify(body));
  };

  try {
    if (req.method === "GET" && req.url === "/health") {
      const configured = Boolean(
        process.env.CLAUDE_CODE_OAUTH_TOKEN || process.env.ANTHROPIC_API_KEY,
      );
      return send(200, { status: "ok", service: "rb-ai", configured });
    }

    if (req.method === "POST" && req.url === "/ask") {
      const body = await readJson(req);
      if (!body.prompt?.trim()) return send(400, { error: "prompt vereist" });
      const images = (body.images ?? []).slice(0, 2);
      for (const img of images) {
        if (!ALLOWED_MEDIA.has(img.mediaType))
          return send(400, { error: `mediaType niet ondersteund: ${img.mediaType}` });
        if (!img.data || img.data.length > MAX_IMAGE_B64)
          return send(400, { error: "afbeelding ontbreekt of is te groot (max ~6 MB)" });
      }
      const answer = await askClaude({
        prompt: body.prompt,
        system: body.system,
        task: body.task === "hard" ? "hard" : "cheap",
        images,
      });
      return send(200, { answer });
    }

    return send(404, { error: "not found" });
  } catch (e) {
    return send(500, { error: String(e) });
  }
});

server.listen(PORT, () => {
  console.log(`rb-ai luistert op :${PORT}`);
});
