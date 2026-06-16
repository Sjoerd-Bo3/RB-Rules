import { Pool } from "pg";

const connectionString =
  process.env.DATABASE_URL ?? "postgres://rbrules:rbrules@localhost:5432/rbrules";

// Eén gedeelde pool. Werkt zowel in Next.js (server) als in de ingest-scripts.
const globalForPg = globalThis as unknown as { pgPool?: Pool };

export const pool: Pool = globalForPg.pgPool ?? new Pool({ connectionString });

if (!globalForPg.pgPool) globalForPg.pgPool = pool;
