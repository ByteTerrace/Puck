const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const textmate = require('vscode-textmate');
const oniguruma = require('vscode-oniguruma');
const { collectDelimiterOffsets } = require('../container-colors');

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

test('fixed delimiter colors skip quoted text and comments, including unfinished input', () => {
    const source = 'palette [{ color: "[{}]", label: "escaped \\" [ ]" }] // [{}]\n/* [{}] */ noise {}\n`[{}]`';
    const result = collectDelimiterOffsets(source);
    assert.equal(result.arrays.map(offset => source[offset]).join(''), '[]');
    assert.equal(result.objects.map(offset => source[offset]).join(''), '{}{}');
    for (const source of ['label: "unfinished [ {', '/* unfinished [{', '`unfinished [{']) {
        assert.deepEqual(collectDelimiterOffsets(source), { arrays: [], objects: [] });
    }
});
