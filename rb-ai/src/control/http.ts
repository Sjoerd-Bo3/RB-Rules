import type { IncomingMessage, ServerResponse } from "node:http";
import { ControlError, createControlPlane, type AiControlPlane } from "./service.js";

const MAX_CONTROL_BODY = 64 * 1024;

async function body(req: IncomingMessage): Promise<unknown> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    const value = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    size += value.length;
    if (size > MAX_CONTROL_BODY) throw new ControlError(400, "invalid_request");
    chunks.push(value);
  }
  if (size === 0) return {};
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8")) as unknown;
  } catch {
    throw new ControlError(400, "invalid_request");
  }
}

function send(res: ServerResponse, status: number, value?: unknown): void {
  res.statusCode = status;
  res.setHeader("cache-control", "no-store");
  res.setHeader("x-content-type-options", "nosniff");
  if (value === undefined) {
    res.end();
    return;
  }
  res.setHeader("content-type", "application/json; charset=utf-8");
  res.end(JSON.stringify(value));
}

const decoded = (value: string): string => {
  try {
    return decodeURIComponent(value);
  } catch {
    throw new ControlError(400, "invalid_request");
  }
};

export function createControlHttpHandler(
  plane: AiControlPlane = createControlPlane(),
): (req: IncomingMessage, res: ServerResponse) => Promise<boolean> {
  return async (req, res) => {
    const url = new URL(req.url ?? "/", "http://rb-ai.internal");
    if (url.pathname !== "/control" && !url.pathname.startsWith("/control/")) return false;
    if (!plane.enabled || !plane.service) {
      send(res, 503, { error: "control_unavailable" });
      return true;
    }
    if (!plane.authorize(req.headers["x-rb-ai-control-key"])) {
      send(res, 401, { error: "unauthorized" });
      return true;
    }
    try {
      await plane.service.ready();
      if (req.method === "GET" && url.pathname === "/control")
        send(res, 200, await plane.service.get());
      else if (req.method === "POST" && url.pathname === "/control/pools")
        send(res, 201, { pool: await plane.service.createPool(await body(req)) });
      else {
        const pool = /^\/control\/pools\/([^/]+)$/.exec(url.pathname);
        const account = /^\/control\/accounts\/([^/]+)$/.exec(url.pathname);
        const credential = /^\/control\/accounts\/([^/]+)\/credential$/.exec(url.pathname);
        const test = /^\/control\/accounts\/([^/]+)\/test$/.exec(url.pathname);
        const deviceStart = /^\/control\/accounts\/([^/]+)\/device-login$/.exec(url.pathname);
        const device = /^\/control\/device-login\/([^/]+)$/.exec(url.pathname);
        if (pool && req.method === "PATCH")
          send(res, 200, { pool: await plane.service.patchPool(decoded(pool[1]), await body(req)) });
        else if (pool && req.method === "DELETE") {
          await plane.service.deletePool(decoded(pool[1]));
          send(res, 204);
        } else if (req.method === "POST" && url.pathname === "/control/accounts")
          send(res, 201, { account: await plane.service.createAccount(await body(req)) });
        else if (account && req.method === "PATCH")
          send(res, 200, {
            account: await plane.service.patchAccount(decoded(account[1]), await body(req)),
          });
        else if (account && req.method === "DELETE") {
          await plane.service.deleteAccount(decoded(account[1]));
          send(res, 204);
        } else if (credential && req.method === "PUT")
          send(res, 200, {
            account: await plane.service.putCredential(decoded(credential[1]), await body(req)),
          });
        else if (test && req.method === "POST")
          send(res, 200, await plane.service.testAccount(decoded(test[1])));
        else if (deviceStart && req.method === "POST")
          send(res, 200, await plane.service.startDeviceLogin(decoded(deviceStart[1])));
        else if (device && req.method === "GET")
          send(res, 200, await plane.service.deviceLoginStatus(decoded(device[1])));
        else if (device && req.method === "DELETE") {
          await plane.service.cancelDeviceLogin(decoded(device[1]));
          send(res, 204);
        } else send(res, 404, { error: "not_found" });
      }
    } catch (error) {
      if (error instanceof ControlError) send(res, error.status, { error: error.code });
      else send(res, 500, { error: "control_error" });
    }
    return true;
  };
}

export const controlHttpHandler = createControlHttpHandler();
