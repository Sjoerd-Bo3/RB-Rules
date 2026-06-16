import type { CapacitorConfig } from "@capacitor/cli";

// Native iOS/Android-schil rond de gehoste PWA. De app laadt de live site
// (server.url), zodat server components, API en push gewoon werken — geen
// aparte native build van de Next.js-app nodig.
const config: CapacitorConfig = {
  appId: "dev.bo3.riftbound",
  appName: "Riftbound Rules",
  webDir: "public", // niet gebruikt zolang server.url is gezet
  server: {
    url: process.env.CAP_SERVER_URL ?? "https://riftbound.bo3.dev",
    cleartext: false,
  },
};

export default config;
