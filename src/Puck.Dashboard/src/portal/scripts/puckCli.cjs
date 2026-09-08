const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

function findRepositoryRoot(start) {
  let directory = start;
  while (!fs.existsSync(path.join(directory, 'Puck.slnx'))) {
    const parent = path.dirname(directory);
    if (parent === directory) throw new Error(`Could not locate Puck.slnx above ${start}.`);
    directory = parent;
  }
  return directory;
}

// CI must use its installed producer artifact. Local development may use a published or installed CLI.
function puckCommand(root) {
  const executable = process.platform === 'win32' ? 'puck.exe' : 'puck';
  const candidate = path.join(root, '.tmp', 'puck-ci', executable);
  if (process.env.GITHUB_ACTIONS === 'true') {
    if (!fs.existsSync(candidate)) throw new Error(`Install this run's Puck CLI artifact before checking dashboard schemas: ${candidate}`);
    return candidate;
  }
  const published = path.join(root, 'src', 'Puck.Cli', 'publish', executable);
  return fs.existsSync(published) ? published : fs.existsSync(candidate) ? candidate : 'puck';
}

function readSchemaBundle(start) {
  const root = findRepositoryRoot(start);
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'puck-schema-'));
  try {
    const bundle = path.join(temporary, 'bundle.json');
    execFileSync(puckCommand(root), ['schema', '--bundle', bundle], { cwd: root, stdio: 'pipe' });
    return JSON.parse(fs.readFileSync(bundle, 'utf8'));
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

module.exports = { findRepositoryRoot, puckCommand, readSchemaBundle };
