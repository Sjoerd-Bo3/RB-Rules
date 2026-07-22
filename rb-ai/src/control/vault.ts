import {
  createCipheriv,
  createDecipheriv,
  randomBytes,
  randomUUID,
} from "node:crypto";
import {
  chmod,
  mkdir,
  open,
  readFile,
  rename,
  unlink,
} from "node:fs/promises";
import { isAbsolute, join } from "node:path";
import { emptyVaultDocument, vaultDocumentSchema, type VaultDocument } from "./types.js";

const VAULT_FILE = "ai-config.v1.enc";
const AAD = Buffer.from("rb-ai-managed-config:v1", "utf8");

interface VaultEnvelope {
  version: 1;
  algorithm: "aes-256-gcm";
  iv: string;
  tag: string;
  ciphertext: string;
}

export class VaultUnavailableError extends Error {
  constructor() {
    super("managed AI-configuratie is niet beschikbaar");
    this.name = "VaultUnavailableError";
  }
}

export function decodeVaultKey(encoded: string): Buffer {
  const value = encoded.trim();
  let key: Buffer;
  if (value.startsWith("hex:")) key = Buffer.from(value.slice(4), "hex");
  else if (value.startsWith("base64:")) key = Buffer.from(value.slice(7), "base64");
  else if (/^[a-fA-F0-9]{64}$/.test(value)) key = Buffer.from(value, "hex");
  else key = Buffer.from(value.replace(/-/g, "+").replace(/_/g, "/"), "base64");
  if (key.length !== 32) throw new VaultUnavailableError();
  return key;
}

function parseEnvelope(value: unknown): VaultEnvelope {
  if (typeof value !== "object" || value === null) throw new VaultUnavailableError();
  const item = value as Record<string, unknown>;
  if (
    item.version !== 1
    || item.algorithm !== "aes-256-gcm"
    || typeof item.iv !== "string"
    || typeof item.tag !== "string"
    || typeof item.ciphertext !== "string"
  ) throw new VaultUnavailableError();
  return item as unknown as VaultEnvelope;
}

export interface ManagedVaultOptions {
  directory: string;
  key: Buffer;
}

export class ManagedVault {
  readonly path: string;
  private readonly directory: string;
  private readonly key: Buffer;

  constructor(options: ManagedVaultOptions) {
    if (!isAbsolute(options.directory) || options.key.length !== 32)
      throw new VaultUnavailableError();
    this.directory = options.directory;
    this.path = join(options.directory, VAULT_FILE);
    this.key = Buffer.from(options.key);
  }

  private async prepareDirectory(): Promise<void> {
    await mkdir(this.directory, { recursive: true, mode: 0o700 });
    await chmod(this.directory, 0o700);
  }

  async load(): Promise<VaultDocument> {
    await this.prepareDirectory();
    let encoded: string;
    try {
      encoded = await readFile(this.path, "utf8");
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return emptyVaultDocument();
      throw new VaultUnavailableError();
    }
    try {
      const envelope = parseEnvelope(JSON.parse(encoded));
      const decipher = createDecipheriv(
        "aes-256-gcm",
        this.key,
        Buffer.from(envelope.iv, "base64"),
      );
      decipher.setAAD(AAD);
      decipher.setAuthTag(Buffer.from(envelope.tag, "base64"));
      const plaintext = Buffer.concat([
        decipher.update(Buffer.from(envelope.ciphertext, "base64")),
        decipher.final(),
      ]);
      return vaultDocumentSchema.parse(JSON.parse(plaintext.toString("utf8")));
    } catch {
      throw new VaultUnavailableError();
    }
  }

  async save(document: VaultDocument): Promise<void> {
    const validated = vaultDocumentSchema.parse(document);
    await this.prepareDirectory();
    const iv = randomBytes(12);
    const cipher = createCipheriv("aes-256-gcm", this.key, iv);
    cipher.setAAD(AAD);
    const ciphertext = Buffer.concat([
      cipher.update(Buffer.from(JSON.stringify(validated), "utf8")),
      cipher.final(),
    ]);
    const envelope: VaultEnvelope = {
      version: 1,
      algorithm: "aes-256-gcm",
      iv: iv.toString("base64"),
      tag: cipher.getAuthTag().toString("base64"),
      ciphertext: ciphertext.toString("base64"),
    };
    const temporary = join(this.directory, `.${VAULT_FILE}.${randomUUID()}.tmp`);
    let handle: Awaited<ReturnType<typeof open>> | undefined;
    try {
      handle = await open(temporary, "wx", 0o600);
      await handle.writeFile(`${JSON.stringify(envelope)}\n`, "utf8");
      await handle.sync();
      await handle.close();
      handle = undefined;
      await chmod(temporary, 0o600);
      await rename(temporary, this.path);
      await chmod(this.path, 0o600);
      try {
        const directory = await open(this.directory, "r");
        await directory.sync();
        await directory.close();
      } catch {
        // Some filesystems cannot fsync directories; rename remains atomic.
      }
    } catch {
      await handle?.close().catch(() => {});
      await unlink(temporary).catch(() => {});
      throw new VaultUnavailableError();
    }
  }
}

export function vaultFromEnvironment(
  source: NodeJS.ProcessEnv,
  defaultDirectory = "/var/lib/rb-ai/config",
): ManagedVault | null {
  if (!source.RB_AI_VAULT_KEY?.trim()) return null;
  try {
    return new ManagedVault({
      directory: source.RB_AI_CONFIG_DIR?.trim() || defaultDirectory,
      key: decodeVaultKey(source.RB_AI_VAULT_KEY),
    });
  } catch {
    return null;
  }
}
