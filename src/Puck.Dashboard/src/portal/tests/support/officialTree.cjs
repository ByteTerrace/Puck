// The built official tree (`puck official build`) as the real-engine tests read it: the authoring workspace's
// `sources[]`, the compiled world documents, and the schema bundle, straight from the tree's content-addressed
// objects. The game's worlds directory holds sources, not documents, so a test reads them here, and skips itself by
// name when no tree has been built. The engine tests mount the sources the way the studio does.
const fs = require('node:fs');
const path = require('node:path');

function repositoryRoot() {
  let dir = __dirname;
  while (!fs.existsSync(path.join(dir, 'Puck.slnx'))) {
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error('could not find Puck.slnx walking up from ' + __dirname);
    dir = parent;
  }
  return dir;
}

const root = repositoryRoot();
const manifestPath = process.env.PUCK_TEST_OFFICIAL_MANIFEST || path.join(root, 'artifacts', 'official', 'dev', 'manifest.json');
const treeDir = path.dirname(path.dirname(manifestPath));
const manifest = fs.existsSync(manifestPath) ? JSON.parse(fs.readFileSync(manifestPath, 'utf8')) : null;

const readObject = (entry) => fs.readFileSync(path.join(treeDir, entry.path), 'utf8');

/** Why the official tree cannot serve these tests, or `null` when it can. */
function officialTreeMissing() {
  if (!manifest) return `no official tree at ${manifestPath} — run 'puck official build'`;
  if (!Array.isArray(manifest.sources)) return `the official tree at ${manifestPath} predates sources[] — rebuild it with 'puck official build'`;
  return null;
}

/** One compiled document's JSON text, by its manifest name (`games/tictactoe`, `standard`). */
function officialDocument(name) {
  const entry = manifest?.documents.find((document) => document.name === name);
  if (!entry) throw new Error(`the official tree names no document '${name}'.`);
  return readObject(entry);
}

/** Every `sources[]` file's text, by worlds-relative path: what the studio mounts. */
function officialSources() {
  return Object.fromEntries((manifest?.sources ?? []).map((entry) => [entry.name, readObject(entry)]));
}

/** The source of the composed island root, found through `composed[]` the way the studio finds it. */
function officialIslandRootSource() {
  const rootName = manifest?.composed?.[0]?.name;
  return manifest?.documents.find((document) => document.name === rootName)?.source ?? null;
}

/** The tree's world schema bundle. */
function officialSchemaBundle() {
  return JSON.parse(readObject(manifest.worldSchemaBundle));
}

/** A root that imports the tictactoe fragment over the standard basis, written as `.puck` beside the sources. */
const TICTACTOE_ROOT = { path: 'test-root.puck', text: 'schema: "puck.world.definition.v1"\nbasis: "standard"\n\nimport "games/tictactoe"\n' };

module.exports = {
  root, manifestPath, treeDir, manifest, officialTreeMissing, officialDocument, officialSources, officialIslandRootSource,
  officialSchemaBundle, TICTACTOE_ROOT,
};
