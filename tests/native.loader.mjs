// A test-only ESM loader hook for tests/native.test.cjs: Node's own module loader requires an explicit extension
// on a relative specifier, but the portal's TypeScript sources under src/Puck.Dashboard/src/portal/src/native/
// author extensionless relative imports (e.g. `from "./inlineHost"`) — correct for the "bundler" module resolution
// Vite and `tsc -b` both use, and never to be rewritten just to suit this loader. This hook tries the same
// specifier with a ".ts" suffix whenever the bare one fails to resolve, so the real, unmodified sources load
// straight through Node's own native TypeScript support with no separate transpilation step of this test's own.
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";

export async function resolve(specifier, context, nextResolve) {
  if (specifier.startsWith(".") && !/\.[a-zA-Z0-9]+$/.test(specifier)) {
    const candidate = new URL(`${specifier}.ts`, context.parentURL);

    if (existsSync(fileURLToPath(candidate))) {
      return nextResolve(`${specifier}.ts`, context);
    }
  }

  return nextResolve(specifier, context);
}
