/*
    Stages `dist/` into `dist-deploy/`, matching the Front Door serving contract
    defined by the `portal` rule set in Azure.Resources/main.bicep:
      - `$web/index.html`, `$web/assets/**`, `$web/portal/assets/**`, and
        `$web/portal/duckdb-extensions/**` are served with a forced
        `Content-Encoding: br` response header, so those files must contain
        brotli-compressed bytes at rest.
      - Every other path (the portal's `mf-manifest.json` and
        `portal-entry.js` among them) is served without a Content-Encoding
        header, so those files must be uncompressed.

    Brotli runs at its highest quality with its default window: the bytes are
    compressed once and downloaded by every visitor. Over the whole staged set
    (about 100 MB, three quarters of it the two DuckDB modules) quality 11
    stores 17.2 MB in about 280 seconds of one core; quality 10 stores 4% more
    in two thirds of the time, quality 9 14% more in an eighth, and a 16 MB
    window saves 2% for half again the time. The largest module alone takes
    quality 11 about two minutes. The files are therefore compressed on every
    core, the largest first,
    and each output is kept in a content-addressed cache under the per-user
    Puck directory (`brotli/`), named by the input's SHA-256 and everything
    else that decides the output bytes: the quality, the window and the
    encoder's version. A cached output is used only when it decompresses to
    exactly the input, so a cache never changes what is staged, only how long
    staging takes. The DuckDB modules and extensions change only with their
    pins, so a staging after the first compresses only the portal's own
    changed chunks. Entries this staging did not use are removed afterwards,
    which keeps the cache at one staging's outputs.
*/

import { createHash } from "node:crypto";
import {
  cpSync,
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  renameSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { availableParallelism, homedir } from "node:os";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { isMainThread, parentPort, Worker } from "node:worker_threads";
import { brotliCompressSync, brotliDecompressSync, constants } from "node:zlib";

const QUALITY = constants.BROTLI_MAX_QUALITY;
const WINDOW = constants.BROTLI_DEFAULT_WINDOW;

function compress(uncompressed) {
  return brotliCompressSync(uncompressed, {
    params: {
      [constants.BROTLI_PARAM_LGWIN]: WINDOW,
      [constants.BROTLI_PARAM_QUALITY]: QUALITY,
      [constants.BROTLI_PARAM_SIZE_HINT]: uncompressed.length,
    },
  });
}

if (!isMainThread) {
  parentPort.on("message", ({ id, path }) => {
    // Copied rather than transferred: a small output can sit in Node's shared buffer pool, which cannot be transferred.
    parentPort.postMessage({ id, output: compress(readFileSync(path)) });
  });
} else {
  await stage();
}

function cacheDirectory() {
  const base =
    process.platform === "win32"
      ? (process.env.LOCALAPPDATA ?? join(homedir(), "AppData", "Local"))
      : (process.env.XDG_DATA_HOME ?? join(homedir(), ".local", "share"));

  return join(base, "Puck", "brotli");
}
function filesUnder(directoryPath) {
  return readdirSync(directoryPath, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile())
    .map((entry) => join(entry.parentPath, entry.name));
}
function size(length) {
  if (length < 1024) {
    return `${length} B`;
  }
  return length < 1024 * 1024 ? `${(length / 1024).toFixed(1)} KB` : `${(length / (1024 * 1024)).toFixed(2)} MB`;
}
function seconds(start) {
  return `${((performance.now() - start) / 1000).toFixed(1)} s`;
}
function readCached(entryPath, uncompressed) {
  try {
    const cached = readFileSync(entryPath);

    return brotliDecompressSync(cached).equals(uncompressed) ? cached : undefined;
  } catch {
    return undefined;
  }
}
function writeCached(entryPath, compressed) {
  const temporary = `${entryPath}.${process.pid}.tmp`;

  try {
    writeFileSync(temporary, compressed);
    renameSync(temporary, entryPath);
  } catch {
    rmSync(temporary, { force: true });
  }
}

async function stage() {
  const rootDirectory = resolve(dirname(fileURLToPath(import.meta.url)), "..");
  const distDirectory = join(rootDirectory, "dist");
  const stageDirectory = join(rootDirectory, "dist-deploy");

  for (const requiredPath of [
    join(distDirectory, "host", "index.html"),
    join(distDirectory, "portal", "mf-manifest.json"),
  ]) {
    if (!existsSync(requiredPath)) {
      console.error(`Missing required build output: ${requiredPath}`);
      process.exit(1);
    }
  }

  const start = performance.now();
  const hostDirectory = join(distDirectory, "host");
  const portalDirectory = join(distDirectory, "portal");
  // The portal's dot-directories (Vite's `.vite` manifest) are build records, not site content. Only the path below
  // `dist/portal` is read, so a checkout under a dot-directory (`.claude/worktrees`) still stages the whole portal.
  const portalContent = (source) =>
    !relative(portalDirectory, source)
      .split(/[\\/]/)
      .some((s) => s.startsWith("."));
  // What is stored compressed is read from `dist/` and written to `dist-deploy/` once, compressed; everything else is
  // copied as it is.
  const sources = [
    join(hostDirectory, "index.html"),
    ...filesUnder(join(hostDirectory, "assets")),
    ...filesUnder(join(portalDirectory, "assets")).filter(portalContent),
    ...filesUnder(join(portalDirectory, "duckdb-extensions")).filter(portalContent),
  ];
  const compressedSources = new Set(sources);

  rmSync(stageDirectory, { force: true, recursive: true });
  cpSync(hostDirectory, stageDirectory, {
    filter: (source) => !compressedSources.has(source),
    recursive: true,
  });
  cpSync(portalDirectory, join(stageDirectory, "portal"), {
    filter: (source) => portalContent(source) && !compressedSources.has(source),
    recursive: true,
  });

  const cache = cacheDirectory();
  const tag = `q${QUALITY}-w${WINDOW}-brotli${process.versions.brotli}`;
  const files = sources.map((source) => {
    const uncompressed = readFileSync(source);
    const digest = createHash("sha256").update(uncompressed).digest("hex");
    const path = source.startsWith(portalDirectory)
      ? join(stageDirectory, "portal", relative(portalDirectory, source))
      : join(stageDirectory, relative(hostDirectory, source));

    mkdirSync(dirname(path), { recursive: true });
    return { entry: `${digest}-${tag}.br`, length: uncompressed.length, path, source, uncompressed };
  });
  const used = new Set(files.map((file) => file.entry));
  const pending = [];
  let done = 0;
  let hits = 0;
  const report = (file, compressedLength, how) => {
    done += 1;
    console.log(
      `stage: ${done}/${files.length} ${relative(stageDirectory, file.path).replaceAll("\\", "/")}: ${size(file.length)} -> ${size(compressedLength)}, ${how}`,
    );
  };

  mkdirSync(cache, { recursive: true });
  for (const file of files) {
    const cached = readCached(join(cache, file.entry), file.uncompressed);

    if (cached) {
      writeFileSync(file.path, cached);
      hits += 1;
      report(file, cached.length, "from the cache");
    } else {
      pending.push(file);
    }
  }
  // Largest first, so the longest compression starts at once and the rest fill the other cores around it.
  pending.sort((a, b) => b.length - a.length);
  console.log(
    `stage: ${hits} of ${files.length} file(s) from the cache (${cache})` +
      (pending.length === 0
        ? "."
        : `; compressing ${pending.length} (${size(pending.reduce((sum, file) => sum + file.length, 0))}) at quality ${QUALITY} on ${Math.min(availableParallelism(), pending.length)} thread(s).`),
  );
  await new Promise((resolvePool, rejectPool) => {
    const workers = [];
    let next = 0;
    let finished = 0;
    const finish = (error) => {
      for (const worker of workers) {
        worker.terminate();
      }
      if (error) {
        rejectPool(error);
      } else {
        resolvePool();
      }
    };
    const dispatch = (worker) => {
      if (next < pending.length) {
        const id = next++;
        const file = pending[id];

        file.started = performance.now();
        if (file.length >= 1024 * 1024) {
          console.log(`stage: compressing ${relative(stageDirectory, file.path).replaceAll("\\", "/")} (${size(file.length)})...`);
        }
        worker.postMessage({ id, path: file.source });
      }
    };

    if (pending.length === 0) {
      resolvePool();
      return;
    }
    for (let index = 0; index < Math.min(availableParallelism(), pending.length); index++) {
      const worker = new Worker(fileURLToPath(import.meta.url));

      workers.push(worker);
      worker.on("error", finish);
      worker.on("message", ({ id, output }) => {
        const file = pending[id];
        const compressed = Buffer.from(output.buffer, output.byteOffset, output.byteLength);

        writeFileSync(file.path, compressed);
        writeCached(join(cache, file.entry), compressed);
        report(file, compressed.length, `compressed in ${seconds(file.started)}`);
        finished += 1;
        if (finished === pending.length) {
          finish();
        } else {
          dispatch(worker);
        }
      });
      dispatch(worker);
    }
  });
  for (const entry of readdirSync(cache)) {
    if (!used.has(entry)) {
      rmSync(join(cache, entry), { force: true });
    }
  }
  console.log(`Staged deployment layout at: ${stageDirectory} in ${seconds(start)}`);
}
