// Loads the portal's real sources under Node's CommonJS loader, the one place every test file gets it from:
// TypeScript and TSX transpile on require (types erased, no checking; `npm run build` owns type checking),
// a CSS module resolves each class to its own name so markup assertions read the source's class names, plain
// CSS loads as nothing, and an image resolves to its file name. TypeScript 7 has no in-process transpiler, so
// esbuild does the erasing.
const fs = require('node:fs');
const path = require('node:path');
const esbuild = require('esbuild');

for (const extension of ['.ts', '.tsx']) {
  const options = { format: 'cjs', jsx: 'automatic', loader: extension.slice(1), target: 'es2022' };

  require.extensions[extension] = (module, file) =>
    module._compile(esbuild.transformSync(fs.readFileSync(file, 'utf8'), { ...options, sourcefile: file }).code, file);
}

const classNames = new Proxy({}, { get: (_, key) => (typeof key === 'string' ? key : undefined) });

require.extensions['.css'] = (module, file) => {
  module.exports = file.endsWith('.module.css') ? { __esModule: true, default: classNames } : {};
};

require.extensions['.png'] = (module, file) => {
  module.exports = { __esModule: true, default: path.basename(file) };
};
