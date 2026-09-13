const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const textmate = require('vscode-textmate');
const oniguruma = require('vscode-oniguruma');
const { roleForScopes, tokenizeLines } = require('../container-colors');

const grammarReady = (async () => {
    const wasm = fs.readFileSync(require.resolve('vscode-oniguruma/release/onig.wasm'));
    await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
    const registry = new textmate.Registry({
        onigLib: Promise.resolve({ createOnigScanner: patterns => new oniguruma.OnigScanner(patterns), createOnigString: text => new oniguruma.OnigString(text) }),
        loadGrammar: async () => JSON.parse(fs.readFileSync(path.join(__dirname, '../syntaxes/puck.tmLanguage.json'), 'utf8'))
    });
    return registry.loadGrammar('source.puck');
})();

function scopeAt(grammar, line, word) {
    const offset = line.indexOf(word);
    assert.ok(offset >= 0);
    return grammar.tokenizeLine(line).tokens.find(token => token.startIndex <= offset && token.endIndex > offset).scopes;
}

test('scalar, array, and object fields share property styling', async () => {
    const grammar = await grammarReady;
    for (const [line, key] of [
        ['palette [ { color: "#888778", roughness: 0.93 } ]', 'palette'],
        ['noise { frequency: 1.8 }', 'noise'],
        ['position [0, 0.43, 0]', 'position'],
        ['scale [0.95, 0.75, 0.7]', 'scale'],
        ['rotation [0, 0, 0, 1]', 'rotation'],
        ['exponent: 2.7', 'exponent'],
        ['host { width: 1280 }', 'host'],
        ['palette: [1, 2]', 'palette']
    ]) assert.ok(scopeAt(grammar, line, key).includes('variable.object.property.puck'), key);
    assert.ok(!scopeAt(grammar, 'value: items[0]', 'items').includes('variable.object.property.puck'));
    assert.ok(scopeAt(grammar, 'label: "noise { [] }"', 'noise').includes('string.quoted.double.puck'));
});

test('declarations, enums, and container delimiters retain distinct scopes', async () => {
    const grammar = await grammarReady;
    assert.ok(scopeAt(grammar, 'shape Superellipsoid "stone" {', 'shape').includes('keyword.declaration.shape.puck'));
    assert.ok(scopeAt(grammar, 'shape Superellipsoid "stone" {', 'Superellipsoid').includes('entity.name.type.shape.puck'));
    assert.ok(scopeAt(grammar, 'blend: SmoothUnion', 'SmoothUnion').includes('constant.other.enum.puck'));
    assert.ok(scopeAt(grammar, 'palette [ {} ]', '[').includes('punctuation.section.array.puck'));
    assert.ok(scopeAt(grammar, 'palette [ {} ]', '{').includes('punctuation.section.object.puck'));
});

function roleAt(grammar, line, word) { return roleForScopes(scopeAt(grammar, line, word)); }

test('loop sources are variables and both loop spellings use control keywords', async () => {
    const grammar = await grammarReady;
    for (const source of ['for (tile, tileIndex) in meadowTiles {', 'for tile in meadowTiles {']) {
        assert.equal(roleAt(grammar, source, 'for'), 'keyword');
        assert.equal(roleAt(grammar, source, 'in '), 'keyword');
        assert.equal(roleAt(grammar, source, 'meadowTiles'), 'variable');
    }
    assert.equal(roleAt(grammar, 'value: items [0]', 'items'), 'variable');
    assert.equal(roleAt(grammar, 'for blade in filter(meadowBlades, b => b["x"] > 0) {', 'filter'), 'function');
});

test('interpolated and raw strings distinguish text, expressions, and escaped braces', async () => {
    const grammar = await grammarReady;
    for (const line of ['prototype $"meadow-{tileIndex}" {', 'shape Prism $"""stem-{blade["id"]}""" {']) {
        const variable = line.includes('tileIndex') ? 'tileIndex' : 'blade';
        assert.equal(roleAt(grammar, line, variable), 'variable');
        assert.equal(roleAt(grammar, line, line.includes('meadow-') ? 'meadow-' : 'stem-'), 'string');
    }
    assert.equal(roleAt(grammar, 'name: $"{{literal}}-{tileIndex}"', 'literal'), 'string');
    assert.equal(roleAt(grammar, 'name: """literal { blade["id"] }"""', 'blade'), 'string');
    assert.equal(roleAt(grammar, 'name: $"{tile[0]}"', '['), 'arrayDelimiter');
    assert.equal(roleAt(grammar, 'name: $"{tile[0]}"', '{'), 'keyword');
});

test('multiline filter and raw-string state recover for following properties', async () => {
    const grammar = await grammarReady;
    const source = 'for blade in filter(meadowBlades, b =>\n  b["x"] > 0) {\n shape Prism $"""stem-{blade["id"]}""" {\n position [-blade["x"], 0, 1]\n }\n}';
    const tokens = tokenizeLines(grammar, source);
    const lines = source.split('\n');
    const role = (line, word) => tokens[line].find(token => token.start <= lines[line].indexOf(word) && token.end > lines[line].indexOf(word)).role;
    assert.equal(role(1, 'b['), 'variable');
    assert.equal(role(2, 'blade'), 'variable');
    assert.equal(role(3, 'position'), 'property');
    assert.equal(role(3, 'blade'), 'variable');
    const raw = tokenizeLines(grammar, 'name: """\nnot code [ {\n"""\nposition [0]');
    assert.ok(raw[1].every(token => token.role === 'string'));
    assert.equal(raw[3][0].role, 'property');
});

test('container colors never leak into comments or plain strings', async () => {
    const grammar = await grammarReady;
    for (const source of ['label: "unfinished [ {', '/* unfinished [{', '`unfinished [{', 'label: "[{}]" // [{}]', 'label: """[{"quoted"}]"""']) {
        const roles = tokenizeLines(grammar, source).flat().map(token => token.role);
        assert.ok(!roles.includes('arrayDelimiter') && !roles.includes('objectDelimiter'), source);
    }
});
