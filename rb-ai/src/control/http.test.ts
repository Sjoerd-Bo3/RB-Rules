import assert from "node:assert/strict";
import type { IncomingMessage, ServerResponse } from "node:http";
import { Readable } from "node:stream";
import { describe, it } from "node:test";
import { createControlHttpHandler } from "./http.js";
import type { AiControlPlane, AiControlService } from "./service.js";

async function invoke(plane: AiControlPlane, key?: string) {
  const req = Readable.from([]) as unknown as IncomingMessage;
  req.method = "GET";
  req.url = "/control";
  req.headers = key ? { "x-rb-ai-control-key": key } : {};
  let text = "";
  const headers = new Map<string, string | number | readonly string[]>();
  const res = {
    statusCode: 200,
    setHeader(name: string, value: string | number | readonly string[]) {
      headers.set(name.toLowerCase(), value);
      return this;
    },
    end(value?: string) {
      text = value ?? "";
      return this;
    },
  } as unknown as ServerResponse;
  assert.equal(await createControlHttpHandler(plane)(req, res), true);
  return {
    status: res.statusCode,
    body: text ? JSON.parse(text) as unknown : undefined,
    headers,
  };
}

describe("control HTTP fail-closed boundary", () => {
  it("returns 503 before authentication when control configuration is disabled", async () => {
    const result = await invoke({ enabled: false, authorize: () => false }, "attacker-key");
    assert.equal(result.status, 503);
    assert.deepEqual(result.body, { error: "control_unavailable" });
    assert.equal(result.headers.get("cache-control"), "no-store");
  });

  it("never calls the service for a rejected X-RB-AI-Control-Key", async () => {
    let calls = 0;
    const service = {
      ready: async () => { calls += 1; },
      get: async () => ({ generation: 1, models: [], providers: [], pools: [], accounts: [] }),
    } as unknown as AiControlService;
    const result = await invoke({ enabled: true, authorize: () => false, service }, "wrong");
    assert.equal(result.status, 401);
    assert.deepEqual(result.body, { error: "unauthorized" });
    assert.equal(calls, 0);
  });

  it("returns the closed catalog shape after authorization", async () => {
    const catalog = { generation: 7, models: [], providers: [], pools: [], accounts: [] };
    const service = {
      ready: async () => {},
      get: async () => catalog,
    } as unknown as AiControlService;
    const result = await invoke({ enabled: true, authorize: () => true, service }, "valid");
    assert.equal(result.status, 200);
    assert.deepEqual(result.body, catalog);
    assert.match(String(result.headers.get("content-type")), /^application\/json/);
  });
});
