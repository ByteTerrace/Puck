/*
    Stages `dist/` into `dist-deploy/`, matching the Front Door serving contract
    defined by the `portal` rule set in Azure.Resources/main.bicep:
      - `$web/index.html` and `$web/assets/**` are served with a forced
        `Content-Encoding: br` response header, so those files must contain
        brotli-compressed bytes at rest.
      - Every other path (including the `portal/**` federated remote) is served
        without a Content-Encoding header, so those files must be uncompressed.
*/

import {
  cpSync,
  existsSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { brotliCompressSync, constants } from "node:zlib";

const rootDirectory = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const distDirectory = join(rootDirectory, "dist");
const stageDirectory = join(rootDirectory, "dist-deploy");

function compressInPlace(path) {
  const uncompressed = readFileSync(path);

  writeFileSync(
    path,
    brotliCompressSync(uncompressed, {
      params: {
        [constants.BROTLI_PARAM_QUALITY]: constants.BROTLI_MAX_QUALITY,
        [constants.BROTLI_PARAM_SIZE_HINT]: uncompressed.length,
      },
    }),
  );
}
function compressTreeInPlace(directoryPath) {
  for (const entry of readdirSync(directoryPath, { withFileTypes: true })) {
    const entryPath = join(directoryPath, entry.name);

    if (entry.isDirectory()) {
      compressTreeInPlace(entryPath);
    } else {
      compressInPlace(entryPath);
    }
  }
}

for (const requiredPath of [
  join(distDirectory, "host", "index.html"),
  join(distDirectory, "portal", "mf-manifest.json"),
]) {
  if (!existsSync(requiredPath)) {
    console.error(`Missing required build output: ${requiredPath}`);
    process.exit(1);
  }
}

rmSync(stageDirectory, { force: true, recursive: true });
cpSync(join(distDirectory, "host"), stageDirectory, { recursive: true });
cpSync(join(distDirectory, "portal"), join(stageDirectory, "portal"), {
  filter: (source) => !source.split(/[\\/]/).some((s) => s.startsWith(".")),
  recursive: true,
});
compressInPlace(join(stageDirectory, "index.html"));
compressTreeInPlace(join(stageDirectory, "assets"));
console.log(`Staged deployment layout at: ${stageDirectory}`);
