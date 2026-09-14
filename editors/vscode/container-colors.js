const fs = require('node:fs');
const path = require('node:path');
const textmate = require('vscode-textmate');
const oniguruma = require('vscode-oniguruma');

async function createGrammar(wasmPath, grammarPath) {
    const wasm = fs.readFileSync(wasmPath);
    await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
    const registry = new textmate.Registry({
        onigLib: Promise.resolve({ createOnigScanner: patterns => new oniguruma.OnigScanner(patterns), createOnigString: text => new oniguruma.OnigString(text) }),
        loadGrammar: async () => JSON.parse(fs.readFileSync(grammarPath, 'utf8'))
    });
    return { grammar: await registry.loadGrammar('source.puck'), dispose: () => registry.dispose() };
}

// The innermost scope wins: expressions inside interpolated strings are code again.
function roleForScopes(scopes) {
    for (const scope of [...scopes].reverse()) {
        if (scope.startsWith('comment.')) return 'comment';
        if (scope === 'punctuation.section.array.puck') return 'arrayDelimiter';
        if (scope === 'punctuation.section.object.puck') return 'objectDelimiter';
        if (scope.startsWith('punctuation.definition.interpolation')) return 'keyword';
        if (scope.startsWith('variable.object.property')) return 'property';
        if (scope.startsWith('variable.') || scope.startsWith('entity.name.namespace')) return 'variable';
        if (scope.startsWith('entity.name.function') || scope.startsWith('support.function')) return 'function';
        if (scope.startsWith('entity.name.type') || scope.startsWith('storage.type')) return 'type';
        if (scope.startsWith('constant.numeric')) return 'number';
        if (scope.startsWith('constant.language') || scope.startsWith('constant.other.enum')) return 'literal';
        if (scope.startsWith('constant.character.escape')) return 'keyword';
        if (scope.startsWith('string.')) return 'string';
        if (scope.startsWith('keyword.operator') || scope.startsWith('punctuation.')) return 'operator';
        if (scope.startsWith('keyword.')) return 'keyword';
    }
    return 'variable';
}

function tokenizeLines(grammar, text) {
    let state = textmate.INITIAL;
    return text.split(/\r?\n/).map(line => {
        const result = grammar.tokenizeLine(line, state);
        state = result.ruleStack;
        return result.tokens.map(token => ({ start: token.startIndex, end: Math.min(token.endIndex, line.length), role: roleForScopes(token.scopes) }));
    });
}

async function registerContainerColors(vscode, context) {
    const registry = await createGrammar(path.join(context.extensionPath, 'dist/onig.wasm'), path.join(context.extensionPath, 'syntaxes/puck.tmLanguage.json'));
    const roles = ['comment', 'property', 'variable', 'function', 'type', 'number', 'literal', 'string', 'operator', 'keyword', 'arrayDelimiter', 'objectDelimiter'];
    const decorations = new Map(roles.map(role => [role, vscode.window.createTextEditorDecorationType({ color: new vscode.ThemeColor('puck.' + role) })]));
    const cache = new WeakMap();
    let timer;
    function refresh() {
        for (const editor of vscode.window.visibleTextEditors) {
            const document = editor.document;
            if (document.languageId !== 'puck') {
                for (const decoration of decorations.values()) editor.setDecorations(decoration, []);
                continue;
            }
            const config = vscode.workspace.getConfiguration('puck', document.uri);
            const palette = config.get('highlighting.consistentColors', false);
            const containers = config.get('highlighting.containerColors', true);
            let entry = cache.get(document);
            if ((palette || containers) && (!entry || entry.version !== document.version)) {
                const ranges = Object.fromEntries(roles.map(role => [role, []]));
                tokenizeLines(registry.grammar, document.getText()).forEach((tokens, line) => {
                    for (const token of tokens) {
                        if (token.end > token.start) ranges[token.role].push(new vscode.Range(line, token.start, line, token.end));
                    }
                });
                entry = { version: document.version, ranges };
                cache.set(document, entry);
            }
            for (const [role, decoration] of decorations) {
                const enabled = role.endsWith('Delimiter') ? containers : palette;
                editor.setDecorations(decoration, enabled ? entry.ranges[role] : []);
            }
        }
    }
    context.subscriptions.push(registry, ...decorations.values(),
        vscode.window.onDidChangeVisibleTextEditors(refresh),
        vscode.workspace.onDidOpenTextDocument(refresh),
        vscode.workspace.onDidCloseTextDocument(refresh),
        vscode.workspace.onDidChangeTextDocument(event => {
            if (event.document.languageId !== 'puck') return;
            clearTimeout(timer);
            timer = setTimeout(refresh, 75);
        }),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('puck.highlighting')) refresh();
        }),
        { dispose() { clearTimeout(timer); } });
    refresh();
}

module.exports = { createGrammar, roleForScopes, tokenizeLines, registerContainerColors };
