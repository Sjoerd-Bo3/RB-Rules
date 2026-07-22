import { z } from "zod";

export const providerIdSchema = z.enum(["claude-agent-sdk", "codex-sdk"]);
export type ManagedProviderId = z.infer<typeof providerIdSchema>;

export const authTypeSchema = z.enum([
  "oauth-token",
  "api-key",
  "access-token",
  "chatgpt-device",
]);
export type ManagedAuthType = z.infer<typeof authTypeSchema>;

export const safeStatusSchema = z.enum([
  "unknown",
  "ready",
  "cooldown",
  "quota_exhausted",
  "auth_invalid",
  "disabled",
]);
export type SafeAccountStatus = z.infer<typeof safeStatusSchema>;

const labelSchema = z.string().trim().min(1).max(80)
  .refine((value) => !/[\u0000-\u001f\u007f]/.test(value), "label bevat stuurtekens");
const idSchema = z.string().uuid();

export const managedAccountSchema = z.object({
  id: idSchema,
  label: labelSchema,
  authType: authTypeSchema,
  enabled: z.boolean(),
  secret: z.string().min(8).max(32_768).optional(),
  /** Server-owned relative directory name; never supplied by the client. */
  homeName: idSchema.optional(),
  deviceAuthorized: z.boolean().optional(),
  lastTestedAt: z.string().datetime().optional(),
  lastStatus: safeStatusSchema.optional(),
}).strict();
export type ManagedAccountRecord = z.infer<typeof managedAccountSchema>;

export const managedPoolSchema = z.object({
  id: idSchema,
  provider: providerIdSchema,
  label: labelSchema,
  enabled: z.boolean(),
  priority: z.number().int().min(-100).max(100),
  weight: z.number().int().min(1).max(100),
  accounts: z.array(managedAccountSchema).max(500),
}).strict().superRefine((pool, context) => {
  const expected = pool.provider === "claude-agent-sdk"
    ? new Set<ManagedAuthType>(["oauth-token", "api-key"])
    : new Set<ManagedAuthType>(["access-token", "chatgpt-device"]);
  const ids = new Set<string>();
  for (const [index, account] of pool.accounts.entries()) {
    if (!expected.has(account.authType))
      context.addIssue({
        code: "custom",
        path: ["accounts", index, "authType"],
        message: "authType past niet bij provider",
      });
    if (ids.has(account.id))
      context.addIssue({
        code: "custom",
        path: ["accounts", index, "id"],
        message: "dubbel account-id",
      });
    ids.add(account.id);
    if (account.authType === "chatgpt-device") {
      if (!account.homeName)
        context.addIssue({
          code: "custom",
          path: ["accounts", index, "homeName"],
          message: "device-account mist serverhome",
        });
      if (account.secret !== undefined)
        context.addIssue({
          code: "custom",
          path: ["accounts", index, "secret"],
          message: "device-account mag geen secret bevatten",
        });
    } else if (account.deviceAuthorized !== undefined) {
      context.addIssue({
        code: "custom",
        path: ["accounts", index, "deviceAuthorized"],
        message: "alleen device-account heeft deviceAuthorized",
      });
    }
  }
});
export type ManagedPoolRecord = z.infer<typeof managedPoolSchema>;

export const vaultDocumentSchema = z.object({
  version: z.literal(1),
  revision: z.number().int().nonnegative(),
  pools: z.array(managedPoolSchema).max(100),
}).strict().superRefine((document, context) => {
  const pools = new Set<string>();
  const accounts = new Set<string>();
  for (const [poolIndex, pool] of document.pools.entries()) {
    if (pools.has(pool.id))
      context.addIssue({ code: "custom", path: ["pools", poolIndex, "id"], message: "dubbel pool-id" });
    pools.add(pool.id);
    for (const [accountIndex, account] of pool.accounts.entries()) {
      if (accounts.has(account.id))
        context.addIssue({
          code: "custom",
          path: ["pools", poolIndex, "accounts", accountIndex, "id"],
          message: "dubbel account-id",
        });
      accounts.add(account.id);
    }
  }
});
export type VaultDocument = z.infer<typeof vaultDocumentSchema>;

export const emptyVaultDocument = (): VaultDocument => ({ version: 1, revision: 0, pools: [] });

export interface PublicPool {
  id: string;
  provider: ManagedProviderId;
  label: string;
  enabled: boolean;
  priority: number;
  weight: number;
  source: "managed" | "environment";
  editable: boolean;
  accountCount: number;
  availableAccounts: number;
  status: SafeAccountStatus;
}

export interface PublicAccount {
  id: string;
  poolId: string;
  label: string;
  enabled: boolean;
  authType: ManagedAuthType;
  status: SafeAccountStatus;
  lastTestedAt?: string;
  credentialConfigured: boolean;
  editable: boolean;
}

export function authTypeAllowed(provider: ManagedProviderId, authType: ManagedAuthType): boolean {
  return provider === "claude-agent-sdk"
    ? authType === "oauth-token" || authType === "api-key"
    : authType === "access-token" || authType === "chatgpt-device";
}
