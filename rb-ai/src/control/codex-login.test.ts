import assert from "node:assert/strict";
import { setTimeout as wait } from "node:timers/promises";
import { setImmediate as waitImmediate } from "node:timers/promises";
import { describe, it } from "node:test";
import {
  CodexDeviceLoginManager,
  safeVerificationUrl,
  type DeviceLoginTransport,
  type DeviceLoginTransportFactory,
} from "./codex-login.js";

interface DeferredTransport extends DeviceLoginTransport {
  succeed(): void;
  fail(): void;
  reject(): void;
  cancelCalls: number;
  closeCalls: number;
}

function transport(input: {
  verificationUrl?: string;
  loginId?: string;
  userCode?: string;
} = {}): DeferredTransport {
  let resolve!: (success: boolean) => void;
  let reject!: () => void;
  const completion = new Promise<boolean>((done, failed) => {
    resolve = done;
    reject = failed;
  });
  const value: DeferredTransport = {
    loginId: input.loginId ?? "upstream-login-id-must-stay-private",
    verificationUrl: input.verificationUrl ?? "https://auth.openai.com/device",
    userCode: input.userCode ?? "SAFE-CODE",
    completion,
    cancelCalls: 0,
    closeCalls: 0,
    succeed: () => resolve(true),
    fail: () => resolve(false),
    reject,
    cancel: async () => {
      value.cancelCalls += 1;
      resolve(false);
    },
    close: () => {
      value.closeCalls += 1;
    },
  };
  return value;
}

function factoryFor(...transports: DeferredTransport[]): DeviceLoginTransportFactory {
  let index = 0;
  return {
    start: async () => {
      const selected = transports[index++];
      if (!selected) throw new Error("raw upstream factory detail");
      return selected;
    },
  };
}

async function settle(): Promise<void> {
  await waitImmediate();
  await waitImmediate();
}

describe("Codex ChatGPT device login", () => {
  it("accepts only HTTPS verification URLs on official OpenAI/ChatGPT hosts", () => {
    assert.equal(safeVerificationUrl("https://openai.com/device"), "https://openai.com/device");
    assert.equal(
      safeVerificationUrl("https://auth.chatgpt.com/device?flow=1#private"),
      "https://auth.chatgpt.com/device",
    );
    for (const value of [
      "http://openai.com/device",
      "https://openai.com.evil.test/device",
      "https://openai.com@evil.test/device",
      "https://user:pass@openai.com/device",
      "javascript:alert(1)",
      "not a URL",
    ]) assert.equal(safeVerificationUrl(value), null);
  });

  it("returns only the approved public fields and completes through a mocked transport", async () => {
    const login = transport({ userCode: "USER-325" });
    const manager = new CodexDeviceLoginManager(factoryFor(login));
    let completed = 0;
    const started = await manager.start(
      "managed-account-id",
      { CODEX_HOME: "/tmp/server-owned-codex-home" },
      async () => { completed += 1; },
    );

    assert.match(started.sessionId, /^[0-9a-f-]{36}$/i);
    assert.notEqual(started.sessionId, login.loginId);
    assert.equal(started.verificationUri, "https://auth.openai.com/device");
    assert.equal(started.userCode, "USER-325");
    assert.equal(started.intervalSeconds, 5);
    assert.deepEqual(manager.status(started.sessionId), { status: "pending", pollAfterMs: 5_000 });
    assert.doesNotMatch(JSON.stringify(started), /upstream-login-id|CODEX_HOME|managed-account-id/);

    login.succeed();
    await settle();
    assert.equal(completed, 1);
    assert.deepEqual(manager.status(started.sessionId), { status: "complete" });
    assert.equal(login.closeCalls, 1);
  });

  it("maps failed transports to a generic error and supports explicit cancellation", async () => {
    const failed = transport({ loginId: "private-login", userCode: "FAIL-CODE" });
    const canceled = transport({ userCode: "CANCEL-CODE" });
    const manager = new CodexDeviceLoginManager(factoryFor(failed, canceled));
    const first = await manager.start("account-one", { CODEX_HOME: "/tmp/one" }, async () => {});
    failed.reject();
    await settle();
    const status = manager.status(first.sessionId);
    assert.deepEqual(status, { status: "error", detail: "authentication_failed" });
    assert.doesNotMatch(JSON.stringify(status), /private-login|FAIL-CODE|upstream/i);

    const second = await manager.start("account-two", { CODEX_HOME: "/tmp/two" }, async () => {});
    assert.equal(await manager.cancel(second.sessionId), true);
    assert.equal(canceled.cancelCalls, 1);
    assert.equal(manager.status(second.sessionId), null);
    assert.equal(await manager.cancel(second.sessionId), false);
  });

  it("cancels by account and replaces an existing pending login for that account", async () => {
    const first = transport();
    const replacement = transport();
    const other = transport();
    const manager = new CodexDeviceLoginManager(factoryFor(first, replacement, other));
    const original = await manager.start("same-account", { CODEX_HOME: "/tmp/a" }, async () => {});
    const current = await manager.start("same-account", { CODEX_HOME: "/tmp/a" }, async () => {});
    await manager.start("other-account", { CODEX_HOME: "/tmp/b" }, async () => {});
    assert.equal(first.cancelCalls, 1);
    assert.equal(manager.status(original.sessionId), null);
    assert.equal(manager.status(current.sessionId)?.status, "pending");
    assert.equal(await manager.cancelAccount("same-account"), 1);
    assert.equal(replacement.cancelCalls, 1);
    assert.equal(other.cancelCalls, 0);
  });

  it("serializes concurrent starts for one account and leaves only the newest active", async () => {
    const first = transport();
    const second = transport();
    let releaseFirst!: () => void;
    const firstGate = new Promise<void>((resolve) => { releaseFirst = resolve; });
    let calls = 0;
    const factory: DeviceLoginTransportFactory = {
      start: async () => {
        calls += 1;
        if (calls === 1) await firstGate;
        return calls === 1 ? first : second;
      },
    };
    const manager = new CodexDeviceLoginManager(factory);
    const originalPromise = manager.start("same", { CODEX_HOME: "/tmp/same" }, async () => {});
    await waitImmediate();
    const replacementPromise = manager.start("same", { CODEX_HOME: "/tmp/same" }, async () => {});
    await waitImmediate();
    assert.equal(calls, 1, "second factory start must wait behind the account lock");
    releaseFirst();
    const [original, replacement] = await Promise.all([originalPromise, replacementPromise]);
    assert.equal(calls, 2);
    assert.equal(first.cancelCalls, 1);
    assert.equal(manager.status(original.sessionId), null);
    assert.equal(manager.status(replacement.sessionId)?.status, "pending");
    await manager.cancel(replacement.sessionId);
  });

  it("atomically assigns completion ownership on either side of cancellation", async () => {
    const canceledFirst = transport();
    const canceledManager = new CodexDeviceLoginManager(factoryFor(canceledFirst));
    let canceledPersistence = 0;
    const canceledSession = await canceledManager.start(
      "cancel-wins",
      { CODEX_HOME: "/tmp/cancel-wins" },
      async () => { canceledPersistence += 1; },
    );
    assert.equal(await canceledManager.cancel(canceledSession.sessionId), true);
    canceledFirst.succeed();
    await settle();
    assert.equal(canceledPersistence, 0, "cancel before claim must prevent persistence");

    const login = transport();
    const manager = new CodexDeviceLoginManager(factoryFor(login));
    let callbackEntered!: () => void;
    const entered = new Promise<void>((resolve) => { callbackEntered = resolve; });
    let releaseCallback!: () => void;
    const callbackGate = new Promise<void>((resolve) => { releaseCallback = resolve; });
    let persisted = 0;
    const started = await manager.start(
      "race-account",
      { CODEX_HOME: "/tmp/race" },
      async (isActive) => {
        callbackEntered();
        await callbackGate;
        assert.equal(isActive(), true);
        persisted += 1;
      },
    );
    login.succeed();
    await entered;
    assert.equal(manager.status(started.sessionId)?.status, "pending");
    assert.equal(await manager.cancel(started.sessionId), false,
      "completion claim owns persistence once callback starts");
    releaseCallback();
    await settle();
    assert.equal(persisted, 1);
    assert.deepEqual(manager.status(started.sessionId), { status: "complete" });
  });

  it("expires and closes an abandoned session without requiring a status poll", async () => {
    const abandoned = transport();
    const manager = new CodexDeviceLoginManager(factoryFor(abandoned), Date.now, 15);
    const started = await manager.start(
      "abandoned-account",
      { CODEX_HOME: "/tmp/abandoned" },
      async () => assert.fail("expired login must not persist completion"),
    );
    await wait(40);
    assert.equal(abandoned.cancelCalls, 1);
    assert.equal(abandoned.closeCalls, 1);
    assert.deepEqual(manager.status(started.sessionId), { status: "expired" });
  });

  it("rejects an unsafe verification URL and closes the transport", async () => {
    const unsafe = transport({ verificationUrl: "https://openai.com.attacker.test/device" });
    const manager = new CodexDeviceLoginManager(factoryFor(unsafe));
    await assert.rejects(manager.start(
      "account",
      { CODEX_HOME: "/tmp/account" },
      async () => {},
    ), /device_login_unavailable/);
    assert.equal(unsafe.closeCalls, 1);
  });

  it("retains one terminal polling window, then purges transport/session identity", async () => {
    const login = transport();
    const manager = new CodexDeviceLoginManager(factoryFor(login), Date.now, 1_000, 100);
    const started = await manager.start("terminal", { CODEX_HOME: "/tmp/terminal" }, async () => {});
    login.succeed();
    await settle();
    assert.deepEqual(manager.status(started.sessionId), { status: "complete" });
    await wait(150);
    assert.equal(manager.status(started.sessionId), null);
  });
});
