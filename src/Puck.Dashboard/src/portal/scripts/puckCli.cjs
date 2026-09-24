const fs = require('node:fs');
const path = require('node:path');

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
    if (!fs.existsSync(candidate)) throw new Error(`Install this run's Puck CLI artifact before running the dashboard's CLI-backed tests: ${candidate}`);
    return candidate;
  }
  const published = path.join(root, 'src', 'Puck.Cli', 'publish', executable);
  return fs.existsSync(published) ? published : fs.existsSync(candidate) ? candidate : 'puck';
}

module.exports = { findRepositoryRoot, puckCommand };
