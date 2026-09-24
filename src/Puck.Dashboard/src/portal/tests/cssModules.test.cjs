// Every class a component reads from a CSS module exists in that module. TypeScript types a module's classes as
// an open record, so `classes.missing` compiles and renders `class="undefined"`; this closes that gap by reading
// the sources the way the bundler does.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');

const sourceRoot = path.join(__dirname, '..', 'src');

function sourceFiles(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return sourceFiles(full);
    return /\.tsx?$/.test(entry.name) ? [full] : [];
  });
}

function definedClasses(cssFile) {
  const css = fs.readFileSync(cssFile, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  return new Set([...css.matchAll(/\.(-?[_a-zA-Z][\w-]*)/g)].map((match) => match[1]));
}

const imports = sourceFiles(sourceRoot).flatMap((file) => {
  const source = fs.readFileSync(file, 'utf8');
  return [...source.matchAll(/^import (\w+) from "(\.[^"]+\.module\.css)";$/gm)].map(([, name, specifier]) => ({
    cssFile: path.resolve(path.dirname(file), specifier),
    file,
    name,
    source,
  }));
});

test('the portal imports CSS modules (the scan below is not vacuous)', () => {
  assert.ok(imports.length > 10, `expected many CSS module imports, found ${imports.length}`);
});

for (const { cssFile, file, name, source } of imports) {
  test(`${path.relative(sourceRoot, file)} reads only classes ${path.basename(cssFile)} defines`, () => {
    assert.ok(fs.existsSync(cssFile), `${cssFile} does not exist`);
    const defined = definedClasses(cssFile);
    const used = new Set([...source.matchAll(new RegExp(`\\b${name}\\.(\\w+)`, 'g'))].map((match) => match[1]));
    const missing = [...used].filter((className) => !defined.has(className));
    assert.deepEqual(missing, [], `missing from ${path.basename(cssFile)}: ${missing.join(', ')}`);
  });
}
