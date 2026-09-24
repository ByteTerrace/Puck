// Loads the portal's real sources under Node's CommonJS loader, the one place every test file gets it from:
// TypeScript and TSX transpile on require (types erased, no checking; `npm run build` owns type checking),
// a CSS module resolves each class to its own name so markup assertions read the source's class names, plain
// CSS loads as nothing, and an image resolves to its file name.
const fs = require('node:fs');
const path = require('node:path');
const ts = require('typescript');

const compilerOptions = { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX };

for (const extension of ['.ts', '.tsx']) {
  require.extensions[extension] = (module, file) =>
    module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions, fileName: file }).outputText, file);
}

const classNames = new Proxy({}, { get: (_, key) => (typeof key === 'string' ? key : undefined) });

require.extensions['.css'] = (module, file) => {
  module.exports = file.endsWith('.module.css') ? { __esModule: true, default: classNames } : {};
};

require.extensions['.png'] = (module, file) => {
  module.exports = { __esModule: true, default: path.basename(file) };
};
