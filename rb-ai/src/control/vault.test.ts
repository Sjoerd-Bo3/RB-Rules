import assert from "node:assert/strict";
import { randomBytes, randomUUID } from "node:crypto";
import { mkdtemp, readFile, readdir, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, it } from "node:test";
import { decodeVaultKey, ManagedVault, VaultUnavailableError } from "./vault.js";
import type { VaultDocument } from "./types.js";

function document(secret = "managed-secret-that-must-never-appear"): VaultDocument {
  return {
    version: 1,
    revision: 1,
    pools: [{
      id: randomUUID(),
      provider: "claude-agent-sdk",
      label: "Primary Claude",
      enabled: true,
      priority: 10,
      weight: 2,
      accounts: [{
        id: randomUUID(),
        label: "Operator label",
        authType: "oauth-token",
        enabled: true,
        secret,
      }],
    }],
  };
}

describe("managed AI vault", () => {
  it("encrypts ciphertext, round-trips, and enforces 0700/0600 modes", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-vault-test-"));
    const vault = new ManagedVault({ directory, key: randomBytes(32) });
    const expected = document();
    await vault.save(expected);

    const raw = await readFile(vault.path, "utf8");
    assert.doesNotMatch(raw, /managed-secret|Operator label|Primary Claude/);
    assert.deepEqual(await vault.load(), expected);
    assert.equal((await stat(directory)).mode & 0o777, 0o700);
    assert.equal((await stat(vault.path)).mode & 0o777, 0o600);
  });

  it("rejects tampered ciphertext and the wrong key without plaintext detail", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-vault-tamper-"));
    const key = randomBytes(32);
    const vault = new ManagedVault({ directory, key });
    await vault.save(document());

    const wrong = new ManagedVault({ directory, key: randomBytes(32) });
    await assert.rejects(wrong.load(), (error: unknown) => {
      assert.ok(error instanceof VaultUnavailableError);
      assert.doesNotMatch(String(error), /secret|ciphertext|key=/i);
      return true;
    });

    const envelope = JSON.parse(await readFile(vault.path, "utf8")) as Record<string, string>;
    const ciphertext = envelope.ciphertext;
    envelope.ciphertext = `${ciphertext[0] === "A" ? "B" : "A"}${ciphertext.slice(1)}`;
    await writeFile(vault.path, JSON.stringify(envelope), { mode: 0o600 });
    await assert.rejects(vault.load(), VaultUnavailableError);
  });

  it("persists by atomic rename and leaves no temporary files", async () => {
    const directory = await mkdtemp(join(tmpdir(), "rb-ai-vault-atomic-"));
    const vault = new ManagedVault({ directory, key: randomBytes(32) });
    for (let revision = 1; revision <= 6; revision += 1) {
      const next = document(`secret-revision-${revision}-long-enough`);
      next.revision = revision;
      await vault.save(next);
      assert.equal((await vault.load()).revision, revision);
    }
    assert.deepEqual(await readdir(directory), ["ai-config.v1.enc"]);
  });

  it("accepts exact 32-byte base64/base64url/hex keys and rejects all other lengths", () => {
    const key = randomBytes(32);
    assert.deepEqual(decodeVaultKey(key.toString("base64")), key);
    assert.deepEqual(decodeVaultKey(key.toString("base64url")), key);
    assert.deepEqual(decodeVaultKey(key.toString("hex")), key);
    assert.deepEqual(decodeVaultKey(`hex:${key.toString("hex")}`), key);
    assert.throws(() => decodeVaultKey(randomBytes(31).toString("base64")), VaultUnavailableError);
  });
});
