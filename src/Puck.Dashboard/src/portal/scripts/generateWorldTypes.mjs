#!/usr/bin/env node
// Generates src/document/worldDefinition.generated.ts from a puck.world.def.v1 JSON Schema
// bundle. This script NEVER fetches or builds that bundle itself in --bundle mode — the caller
// produces it first with:
//
//   ./src/Puck.Cli/publish/puck.exe schema --bundle <path>
//
// and passes <path> here with --bundle. package.json's "types:generate" script documents this
// two-step call; "check:types" (this same script with --check) is the one exception that DOES
// shell out to the published CLI itself, purely to regenerate a fresh comparison copy.
//
// Usage:
//   node scripts/generateWorldTypes.mjs --bundle <path> [--out <path>]
//   node scripts/generateWorldTypes.mjs --check

import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { compile } from "json-schema-to-typescript";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PORTAL_DIR = path.join(HERE, "..");
const DEFAULT_OUT = path.join(PORTAL_DIR, "src", "document", "worldDefinition.generated.ts");
const ROOT_TYPE_NAME = "WorldDefinition";

function findRepositoryRoot(start) {
  let dir = start;
  for (;;) {
    if (fs.existsSync(path.join(dir, "Puck.slnx"))) {
      return dir;
    }
    const parent = path.dirname(dir);
    if (parent === dir) {
      throw new Error(`could not find Puck.slnx walking up from ${start}`);
    }
    dir = parent;
  }
}

function parseArgs(argv) {
  const args = { bundle: null, out: null, check: false };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === "--bundle") {
      args.bundle = argv[(i += 1)];
    } else if (argv[i] === "--out") {
      args.out = argv[(i += 1)];
    } else if (argv[i] === "--check") {
      args.check = true;
    }
  }
  return args;
}

/** Reads and compiles `bundlePath` into the generated file's full text (header included). */
async function renderFromBundle(bundlePath) {
  const bundleText = fs.readFileSync(bundlePath, "utf8");
  const schema = JSON.parse(bundleText);
  const meta = schema["x-puck"] ?? {};

  // The bundle's own title ("Puck world definition (puck.world.def.v1)") would otherwise name
  // the root export via json-schema-to-typescript's title-over-name precedence; force the clean
  // name the rest of the studio imports instead.
  const rootSchema = { ...schema, title: ROOT_TYPE_NAME };

  const body = await compile(rootSchema, ROOT_TYPE_NAME, {
    bannerComment: "",
    additionalProperties: false,
    unreachableDefinitions: true,
  });

  const header =
    "// GENERATED FILE — do not hand-edit.\n" +
    "// Produced by scripts/generateWorldTypes.mjs from a puck.world.def.v1 schema bundle\n" +
    "// (`puck.exe schema --bundle <path>` produces the bundle; this script never fetches it itself).\n" +
    "// Regenerate: dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish\n" +
    "//             ./src/Puck.Cli/publish/puck.exe schema --bundle <bundle.json>\n" +
    "//             node scripts/generateWorldTypes.mjs --bundle <bundle.json>\n" +
    // The bundle's x-puck.commit is deliberately NOT stamped here: a header that changes on every commit would
    // make check:types report drift when the schema itself is unchanged.
    `// Source bundle: schemaVersion=${meta.schemaVersion ?? "unknown"} ` +
    `generator=${meta.generator ?? "unknown"}\n\n`;

  return header + body;
}

async function runGenerate(bundlePath, outPath) {
  const rendered = await renderFromBundle(bundlePath);
  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  fs.writeFileSync(outPath, rendered, "utf8");
  console.log(`wrote ${outPath}`);
}

async function runCheck() {
  const repoRoot = findRepositoryRoot(PORTAL_DIR);
  const puckExe = path.join(repoRoot, "src", "Puck.Cli", "publish", "puck.exe");

  if (!fs.existsSync(puckExe)) {
    console.error(`check:types needs a published puck CLI at ${puckExe}.`);
    console.error("Produce it with: dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish");
    process.exitCode = 1;
    return;
  }

  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "puck-world-types-"));
  try {
    const bundlePath = path.join(tmpDir, "world.schema.bundle.json");
    execFileSync(puckExe, ["schema", "--bundle", bundlePath], { stdio: "inherit" });

    const fresh = await renderFromBundle(bundlePath);
    const existing = fs.existsSync(DEFAULT_OUT) ? fs.readFileSync(DEFAULT_OUT, "utf8") : null;

    if (fresh !== existing) {
      console.error(`${path.relative(repoRoot, DEFAULT_OUT)} is stale relative to the current schema.`);
      console.error("Regenerate it with:");
      console.error(`  ${puckExe} schema --bundle <bundle.json>`);
      console.error("  node scripts/generateWorldTypes.mjs --bundle <bundle.json>");
      process.exitCode = 1;
    } else {
      console.log(`${path.relative(repoRoot, DEFAULT_OUT)} is current.`);
    }
  } finally {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  }
}

async function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.check) {
    await runCheck();
    return;
  }

  if (!args.bundle) {
    console.error("usage: node scripts/generateWorldTypes.mjs --bundle <path> [--out <path>]");
    console.error("       node scripts/generateWorldTypes.mjs --check");
    console.error("produce <path> first with: ./src/Puck.Cli/publish/puck.exe schema --bundle <path>");
    process.exitCode = 1;
    return;
  }

  await runGenerate(args.bundle, args.out ?? DEFAULT_OUT);
}

main().catch((error) => {
  console.error(error.stack ?? String(error));
  process.exitCode = 1;
});
