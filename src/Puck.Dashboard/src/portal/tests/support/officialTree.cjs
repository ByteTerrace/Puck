// The built official tree (`puck official build`) as the real-engine tests read it: its schema bundle, straight from
// the tree's content-addressed objects. A test that needs it skips itself by name when no tree has been built.
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

/** The tree's world schema bundle. */
function officialSchemaBundle() {
  return JSON.parse(readObject(manifest.worldSchemaBundle));
}

module.exports = { root, manifestPath, treeDir, manifest, officialTreeMissing, officialSchemaBundle };
