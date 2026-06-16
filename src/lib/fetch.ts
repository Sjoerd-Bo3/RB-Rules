// Gedeelde HTTP-fetch met een browser-achtige User-Agent (sommige bronnen
// blokkeren bot-UA's) en een optionele uitgaande proxy (OUTBOUND_PROXY) voor
// bronnen die datacenter-IP's blokkeren (bv. Cloudflare op de Azure-VM).
//
// We gebruiken de globale fetch (decomprimeert gzip/br/deflate zelf) en laden
// undici alléén lazy wanneer er een proxy is geconfigureerd — een statische
// undici-import breekt sommige TS-runners (tsx/esbuild).
const DEFAULT_UA =
  "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
  "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

let dispatcher: unknown;
let dispatcherInit = false;
async function getDispatcher(): Promise<unknown> {
  const proxy = process.env.OUTBOUND_PROXY;
  if (!proxy) return undefined;
  if (!dispatcherInit) {
    dispatcherInit = true;
    const { ProxyAgent } = await import("undici");
    dispatcher = new ProxyAgent(proxy);
  }
  return dispatcher;
}

export async function browserFetch(url: string, init: RequestInit = {}): Promise<Response> {
  const headers: Record<string, string> = {
    "user-agent": process.env.FETCH_USER_AGENT ?? DEFAULT_UA,
    ...((init.headers as Record<string, string>) ?? {}),
  };
  const opts = { redirect: "follow" as const, ...init, headers } as RequestInit & {
    dispatcher?: unknown;
  };
  const d = await getDispatcher();
  if (d) opts.dispatcher = d;
  return fetch(url, opts);
}
