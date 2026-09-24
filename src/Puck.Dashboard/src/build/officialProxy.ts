import type { ProxyOptions } from "vite";

/**
 * Local serving's stand-in for the platform edge. Production's Front Door rewrites /official/* onto the official
 * content container for the host and portal alike; locally, the dev and preview servers forward it to a
 * `puck official serve` instance (rooted at /, so the prefix is stripped). The studio therefore keeps the same
 * relative "/official" base in every environment, whether it is served on its own or inside the host.
 */
export function officialProxy(target = "http://localhost:61102"): Record<string, ProxyOptions> {
  return {
    "/official": {
      changeOrigin: true,
      rewrite: (requestPath) => requestPath.replace(/^\/official/, ""),
      target,
    },
  };
}
