import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createRequire } from "node:module";
import { randomUUID } from "node:crypto";
import { createInterface, type Interface } from "node:readline";
import type { MinimalEnvironment } from "../providers/codex.js";

const moduleRequire = createRequire(import.meta.url);
const codexBin = moduleRequire.resolve("@openai/codex/bin/codex.js");
const START_TIMEOUT_MS = 10_000;
const SESSION_TTL_MS = 15 * 60_000;
const POLL_AFTER_MS = 5_000;
const TERMINAL_RETENTION_MS = 60_000;

export interface DeviceLoginTransport {
  loginId: string;
  verificationUrl: string;
  userCode: string;
  completion: Promise<boolean>;
  cancel(): Promise<void>;
  close(): void;
}

export interface DeviceLoginTransportFactory {
  start(environment: MinimalEnvironment): Promise<DeviceLoginTransport>;
  readAccount?(environment: MinimalEnvironment): Promise<boolean | null>;
}

function record(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

export function safeVerificationUrl(value: string): string | null {
  try {
    const url = new URL(value);
    const host = url.hostname.toLowerCase();
    const allowed = host === "openai.com"
      || host.endsWith(".openai.com")
      || host === "chatgpt.com"
      || host.endsWith(".chatgpt.com");
    if (url.protocol !== "https:" || !allowed || url.username || url.password) return null;
    url.search = "";
    url.hash = "";
    return url.toString();
  } catch {
    return null;
  }
}

class AppServerClient {
  private readonly child: ChildProcessWithoutNullStreams;
  private readonly lines: Interface;
  private nextId = 1;
  private readonly pending = new Map<number, {
    resolve: (value: unknown) => void;
    reject: () => void;
    timer: ReturnType<typeof setTimeout>;
  }>();
  private readonly notifications = new Set<(method: string, params: unknown) => void>();

  private constructor(environment: MinimalEnvironment) {
    this.child = spawn(process.execPath, [codexBin, "app-server", "--stdio"], {
      env: { ...environment },
      stdio: ["pipe", "pipe", "pipe"],
    });
    // Drain stderr without logging it: it may contain upstream account data.
    this.child.stderr.resume();
    this.lines = createInterface({ input: this.child.stdout, crlfDelay: Infinity });
    this.lines.on("line", (line) => this.receive(line));
    this.child.stdin.on("error", () => {
      this.failAll();
      if (!this.child.killed) this.child.kill();
    });
    this.child.once("error", () => this.failAll());
    this.child.once("exit", () => this.failAll());
  }

  static async open(environment: MinimalEnvironment): Promise<AppServerClient> {
    const client = new AppServerClient(environment);
    try {
      await client.request("initialize", {
        clientInfo: { name: "rb-ai", title: "RB AI", version: "1.0.0" },
        capabilities: { experimentalApi: false, requestAttestation: false },
      });
      client.notify("initialized");
      return client;
    } catch (error) {
      client.close();
      throw error;
    }
  }

  onNotification(listener: (method: string, params: unknown) => void): () => void {
    this.notifications.add(listener);
    return () => this.notifications.delete(listener);
  }

  private receive(line: string): void {
    let message: Record<string, unknown> | null;
    try {
      message = record(JSON.parse(line));
    } catch {
      return;
    }
    if (!message) return;
    if (typeof message.id === "number") {
      const pending = this.pending.get(message.id);
      if (!pending) return;
      this.pending.delete(message.id);
      clearTimeout(pending.timer);
      if (message.error) pending.reject();
      else pending.resolve(message.result);
      return;
    }
    if (typeof message.method === "string")
      for (const listener of this.notifications) listener(message.method, message.params);
  }

  private failAll(): void {
    for (const item of this.pending.values()) {
      clearTimeout(item.timer);
      item.reject();
    }
    this.pending.clear();
  }

  request(method: string, params?: unknown, timeoutMs = START_TIMEOUT_MS): Promise<unknown> {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error("codex_control_unavailable"));
      }, timeoutMs);
      timer.unref?.();
      this.pending.set(id, {
        resolve,
        reject: () => reject(new Error("codex_control_unavailable")),
        timer,
      });
      try {
        this.child.stdin.write(`${JSON.stringify({ method, id, ...(params === undefined ? {} : { params }) })}\n`);
      } catch {
        this.pending.delete(id);
        clearTimeout(timer);
        reject(new Error("codex_control_unavailable"));
      }
    });
  }

  notify(method: string, params?: unknown): void {
    try {
      this.child.stdin.write(`${JSON.stringify({ method, ...(params === undefined ? {} : { params }) })}\n`);
    } catch {
      this.failAll();
      if (!this.child.killed) this.child.kill();
      throw new Error("codex_control_unavailable");
    }
  }

  close(): void {
    this.lines.close();
    if (!this.child.killed) this.child.kill();
    this.failAll();
  }
}

export class InstalledCodexLoginTransportFactory implements DeviceLoginTransportFactory {
  async start(environment: MinimalEnvironment): Promise<DeviceLoginTransport> {
    const client = await AppServerClient.open(environment);
    try {
      const response = record(await client.request(
        "account/login/start",
        { type: "chatgptDeviceCode" },
      ));
      if (
        response?.type !== "chatgptDeviceCode"
        || typeof response.loginId !== "string"
        || typeof response.verificationUrl !== "string"
        || typeof response.userCode !== "string"
      ) throw new Error("codex_device_login_unavailable");
      const verificationUrl = safeVerificationUrl(response.verificationUrl);
      if (!verificationUrl) throw new Error("codex_device_login_unavailable");
      let resolveCompletion!: (success: boolean) => void;
      const completion = new Promise<boolean>((resolve) => {
        resolveCompletion = resolve;
      });
      const remove = client.onNotification((method, params) => {
        if (method !== "account/login/completed") return;
        const notification = record(params);
        if (notification?.loginId !== response.loginId) return;
        remove();
        resolveCompletion(notification?.success === true);
      });
      return {
        loginId: response.loginId,
        verificationUrl,
        userCode: response.userCode,
        completion,
        cancel: async () => {
          await client.request("account/login/cancel", { loginId: response.loginId });
          remove();
          resolveCompletion(false);
        },
        close: () => {
          remove();
          client.close();
        },
      };
    } catch (error) {
      client.close();
      throw error;
    }
  }

  async readAccount(environment: MinimalEnvironment): Promise<boolean | null> {
    let client: AppServerClient | undefined;
    try {
      client = await AppServerClient.open(environment);
      const response = record(await client.request("account/read", { refreshToken: true }));
      return response ? record(response.account) !== null : null;
    } catch {
      return null;
    } finally {
      client?.close();
    }
  }
}

interface DeviceSession {
  id: string;
  accountId: string;
  transport: DeviceLoginTransport;
  status: "pending" | "claimed" | "complete" | "expired" | "error";
  expiresAt: number;
  expiryTimer?: ReturnType<typeof setTimeout>;
  terminalTimer?: ReturnType<typeof setTimeout>;
}

export class CodexDeviceLoginManager {
  private readonly sessions = new Map<string, DeviceSession>();
  private readonly accountStartTails = new Map<string, Promise<void>>();

  constructor(
    private readonly factory: DeviceLoginTransportFactory = new InstalledCodexLoginTransportFactory(),
    private readonly now: () => number = Date.now,
    private readonly sessionTtlMs = SESSION_TTL_MS,
    private readonly terminalRetentionMs = TERMINAL_RETENTION_MS,
  ) {}

  private async withAccountStartLock<T>(accountId: string, work: () => Promise<T>): Promise<T> {
    const previous = this.accountStartTails.get(accountId) ?? Promise.resolve();
    let release!: () => void;
    const turn = new Promise<void>((resolve) => { release = resolve; });
    const tail = previous.then(() => turn);
    this.accountStartTails.set(accountId, tail);
    await previous;
    try {
      return await work();
    } finally {
      release();
      if (this.accountStartTails.get(accountId) === tail)
        this.accountStartTails.delete(accountId);
    }
  }

  private terminal(
    session: DeviceSession,
    status: "complete" | "expired" | "error",
    close = true,
  ): void {
    if (this.sessions.get(session.id) !== session) return;
    clearTimeout(session.expiryTimer);
    session.status = status;
    if (close) session.transport.close();
    clearTimeout(session.terminalTimer);
    session.terminalTimer = setTimeout(() => {
      if (this.sessions.get(session.id) === session && session.status === status)
        this.sessions.delete(session.id);
    }, this.terminalRetentionMs);
    session.terminalTimer.unref?.();
  }

  private expire(session: DeviceSession): void {
    if (session.status !== "pending") return;
    this.terminal(session, "expired", false);
    void session.transport.cancel().catch(() => {}).finally(() => session.transport.close());
  }

  async start(
    accountId: string,
    environment: MinimalEnvironment,
    onCompleted: (isActive: () => boolean) => Promise<void>,
  ): Promise<{
    sessionId: string;
    verificationUri: string;
    userCode: string;
    expiresAt: string;
    intervalSeconds: number;
  }> {
    return await this.withAccountStartLock(accountId, async () => {
      for (const session of this.sessions.values())
        if (session.accountId === accountId) {
          if (session.status === "claimed") throw new Error("device_login_unavailable");
          if (session.status === "pending") await this.cancel(session.id);
        }
      const transport = await this.factory.start(environment);
      const verificationUri = safeVerificationUrl(transport.verificationUrl);
      if (!verificationUri) {
        transport.close();
        throw new Error("device_login_unavailable");
      }
      const id = randomUUID();
      const session: DeviceSession = {
        id,
        accountId,
        transport,
        status: "pending",
        expiresAt: this.now() + this.sessionTtlMs,
      };
      session.expiryTimer = setTimeout(() => this.expire(session), this.sessionTtlMs);
      session.expiryTimer.unref?.();
      this.sessions.set(id, session);
      void transport.completion.then(async (success) => {
        if (session.status !== "pending") return;
        if (!success) {
          this.terminal(session, "error");
          return;
        }
        clearTimeout(session.expiryTimer);
        // This transition is the atomic completion claim. JavaScript cannot
        // interleave cancel() between the pending check and this assignment:
        // cancel wins before it, or completion owns persistence after it.
        session.status = "claimed";
        const isActive = () =>
          this.sessions.get(session.id) === session && session.status === "claimed";
        try {
          await onCompleted(isActive);
          if (isActive()) this.terminal(session, "complete");
        } catch {
          if (isActive()) this.terminal(session, "error");
        }
      }).catch(() => {
        if (session.status === "pending" || session.status === "claimed")
          this.terminal(session, "error");
      });
      return {
        sessionId: id,
        verificationUri,
        userCode: transport.userCode,
        expiresAt: new Date(session.expiresAt).toISOString(),
        intervalSeconds: POLL_AFTER_MS / 1000,
      };
    });
  }

  status(id: string): {
    status: "pending" | "complete" | "expired" | "error";
    pollAfterMs?: number;
    detail?: "authentication_failed";
  } | null {
    const session = this.sessions.get(id);
    if (!session) return null;
    if (session.status === "pending" && this.now() >= session.expiresAt) {
      this.expire(session);
    }
    return {
      status: session.status === "claimed" ? "pending" : session.status,
      ...(session.status === "pending" || session.status === "claimed"
        ? { pollAfterMs: POLL_AFTER_MS }
        : {}),
      ...(session.status === "error" ? { detail: "authentication_failed" as const } : {}),
    };
  }

  async cancel(id: string): Promise<boolean> {
    const session = this.sessions.get(id);
    if (!session) return false;
    if (session.status === "claimed") return false;
    this.sessions.delete(id);
    clearTimeout(session.expiryTimer);
    clearTimeout(session.terminalTimer);
    // Prevent a completion already queued by the transport from invoking the
    // persistence callback after this session has been canceled.
    const active = session.status === "pending";
    session.status = "error";
    if (active) await session.transport.cancel().catch(() => {});
    session.transport.close();
    return true;
  }

  async cancelAccount(accountId: string): Promise<number> {
    const sessions = [...this.sessions.values()]
      .filter((session) => session.accountId === accountId);
    const pending = sessions.filter((session) => session.status === "pending");
    await Promise.all(pending.map((session) => this.cancel(session.id)));
    for (const session of sessions) {
      if (session.status === "pending" || session.status === "claimed") continue;
      this.sessions.delete(session.id);
      clearTimeout(session.expiryTimer);
      clearTimeout(session.terminalTimer);
    }
    return pending.length;
  }

  transportFactory(): DeviceLoginTransportFactory {
    return this.factory;
  }
}
